using Computerwelt.Emulation.Python.Compilation;

namespace Computerwelt.Emulation.Python.Runtime;

/// <summary>
/// A generator.
/// </summary>
/// <remarks>
/// <para>
/// A suspended frame is what a generator needs, and the VM's <see cref="OpCode.Yield"/>
/// would have to unwind and resume the C# stack to provide one. Rather than write a second
/// interpreter loop, this runs the frame on its own thread of control via C#'s own
/// iterator machinery: the body executes lazily, one <c>yield</c> at a time, driven by
/// <see cref="Next"/>.
/// </para>
/// <para>
/// The consequence is that a generator holds a live enumerator rather than a serializable
/// frame, so generators are the one thing a snapshot cannot yet capture. That is recorded
/// in the ledger rather than hidden.
/// </para>
/// </remarks>
public sealed class PyGenerator : PyObject
{
    private readonly IEnumerator<PyObject> _enumerator;
    private readonly GeneratorChannel _channel = new();
    private bool _finished;
    private bool _started;
    private bool _running;

    /// <summary>Creates a generator over a prepared frame.</summary>
    internal PyGenerator(VirtualMachine machine, VirtualMachine.Frame frame) =>
        _enumerator = GeneratorRunner.Run(machine, frame, _channel).GetEnumerator();

    /// <inheritdoc />
    public override string TypeName => "generator";

    /// <inheritdoc />
    public override string Repr() => "<generator object>";

    /// <summary>Advances the generator. Returns null when it is exhausted.</summary>
    public PyObject? Next()
    {
        if (_finished)
        {
            return null;
        }

        if (_running)
        {
            throw new PyRaise(PyErrors.ValueError("generator already executing"));
        }

        _started = true;
        _running = true;

        try
        {
            if (_enumerator.MoveNext())
            {
                return _enumerator.Current;
            }

            _finished = true;
            return null;
        }
        catch
        {
            // A generator that raised is over; asking again is exhaustion, not a retry.
            _finished = true;
            throw;
        }
        finally
        {
            _running = false;
        }
    }

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name) => name switch
    {
        "__next__" => new PyBuiltinFunction("__next__", _ => Next() ?? throw new PyRaise(PyErrors.StopIteration())),
        "__iter__" => new PyBuiltinFunction("__iter__", _ => this),

        // `send` resumes the generator with a value as the result of its `yield`.
        "send" => new PyBuiltinFunction("send", arguments =>
        {
            Arity.Exact("send", arguments, 1);

            if (!_started && arguments[0] is not PyNone)
            {
                throw new PyRaise(PyErrors.TypeError("can't send non-None value to a just-started generator"));
            }

            _channel.Sent = arguments[0];
            return Next() ?? throw new PyRaise(PyErrors.StopIteration());
        }),

        // `throw` raises at the point the generator is suspended, so its own `try` blocks
        // see the exception; what it yields next, if anything, is the result.
        "throw" => new PyBuiltinFunction("throw", arguments =>
        {
            Arity.Between("throw", arguments, 1, 1);
            var exception = Instance(arguments[0]);

            if (!_started || _finished)
            {
                _finished = true;
                throw new PyRaise(exception);
            }

            _channel.Thrown = exception;
            return Next() ?? throw new PyRaise(PyErrors.StopIteration());
        }),

        // `close` throws `GeneratorExit`, so `finally` blocks run; the generator may end or
        // let the exit propagate, but not carry on yielding.
        "close" => new PyBuiltinFunction("close", arguments =>
        {
            Arity.Exact("close", arguments, 0);

            if (!_started || _finished)
            {
                _finished = true;
                return PyNone.Instance;
            }

            _channel.Thrown = new PyException(PyExceptionType.GeneratorExit, string.Empty);

            try
            {
                if (Next() is not null)
                {
                    throw new PyRaise(PyErrors.RuntimeError("generator ignored GeneratorExit"));
                }
            }
            catch (PyRaise raise) when (raise.Exception.ExceptionType.IsSubclassOf(PyExceptionType.GeneratorExit)
                || raise.Exception.ExceptionType.IsSubclassOf(PyExceptionType.StopIteration))
            {
            }

            return PyNone.Instance;
        }),

        _ => null,
    };

    private static PyException Instance(PyObject value) => value switch
    {
        PyException exception => exception,
        PyExceptionType type => new PyException(type, string.Empty),
        _ => throw new PyRaise(PyErrors.TypeError("exceptions must be classes or instances deriving from BaseException, not " + value.TypeName)),
    };

    /// <inheritdoc />
    public override IEnumerable<PyObject>? Iterate()
    {
        while (Next() is { } value)
        {
            yield return value;
        }
    }
}

/// <summary>
/// A coroutine: an <c>async def</c> body that has been called but not yet awaited.
/// </summary>
/// <remarks>
/// With no I/O to wait on, nothing a coroutine does can actually block, so awaiting one
/// runs it straight through. What the object buys is the deferral — the body does not run
/// at the call — and the record of having been awaited, which is what makes awaiting the
/// same coroutine twice the error CPython reports.
/// </remarks>
public sealed class PyCoroutine : PyObject
{
    private readonly Func<PyObject> _run;
    private bool _awaited;

    internal PyCoroutine(string name, Func<PyObject> run)
    {
        Name = name;
        _run = run;
    }

    /// <summary>The name of the function that produced it.</summary>
    public string Name { get; }

    /// <inheritdoc />
    public override string TypeName => "coroutine";

    /// <inheritdoc />
    public override string Repr() => $"<coroutine object {Name}>";

    /// <summary>Runs the body and returns its result.</summary>
    public PyObject Resolve()
    {
        if (_awaited)
        {
            throw new PyRaise(PyErrors.RuntimeError("cannot reuse already awaited coroutine"));
        }

        _awaited = true;
        return _run();
    }
}

/// <summary>
/// A value that is already settled, standing in for a future.
/// </summary>
/// <remarks>
/// <c>gather</c> has to return something awaitable, and with every coroutine completing
/// synchronously there is nothing left to wait for by the time it returns — so the result
/// is simply carried until the <c>await</c> unwraps it.
/// </remarks>
public sealed class PyFuture : PyObject
{
    private Func<PyObject>? _pending;
    private PyObject? _value;
    private PyRaise? _failure;

    /// <summary>Creates a future that has already settled.</summary>
    public PyFuture(PyObject value) => _value = value;

    /// <summary>Creates a future whose work runs when it is first awaited.</summary>
    public PyFuture(Func<PyObject> pending) => _pending = pending;

    /// <inheritdoc />
    public override string TypeName => "Future";

    /// <inheritdoc />
    public override string Repr() =>
        _pending is null ? $"<Future finished result={_value!.Repr()}>" : "<Future pending>";

    /// <summary>
    /// Settles the future, running its work the first time and caching the result.
    /// </summary>
    /// <remarks>
    /// Unlike a coroutine, a future may be awaited any number of times: the second await
    /// gets the same object back rather than an error.
    /// </remarks>
    public PyObject Resolve()
    {
        if (_failure is { } failed)
        {
            throw failed;
        }

        if (_pending is { } work)
        {
            _pending = null;

            try
            {
                _value = work();
            }
            catch (PyRaise raise)
            {
                // A future that failed stays failed: awaiting it again reports the same
                // error rather than retrying work that already ran.
                _failure = raise;
                throw;
            }
        }

        return _value!;
    }
}

/// <summary>
/// Runs a generator frame, yielding each value the body produces.
/// </summary>
/// <remarks>
/// This is a second, simplified interpreter loop that handles only what a generator body
/// needs and can suspend at a <c>yield</c>, which the main loop cannot. The duplication is
/// deliberate: threading suspension through every instruction of the main loop would slow
/// down the common case for the sake of the rare one.
/// </remarks>
internal sealed class GeneratorChannel
{
    /// <summary>The value <c>send</c> hands to the suspended <c>yield</c>.</summary>
    public PyObject? Sent { get; set; }

    /// <summary>The exception <c>throw</c> raises at the suspended <c>yield</c>.</summary>
    public PyException? Thrown { get; set; }
}

internal static class GeneratorRunner
{
    public static IEnumerable<PyObject> Run(VirtualMachine machine, VirtualMachine.Frame frame, GeneratorChannel channel)
    {
        while (frame.InstructionPointer < frame.Code.Instructions.Count)
        {
            var instruction = frame.Code.Instructions[frame.InstructionPointer];

            if (instruction.OpCode == OpCode.Yield)
            {
                frame.InstructionPointer++;
                yield return frame.Pop();

                // A thrown exception is raised here, where the body is suspended, and goes
                // to the body's own handlers; with none, it leaves the generator.
                if (channel.Thrown is { } thrown)
                {
                    channel.Thrown = null;

                    if (!VirtualMachine.Unwind(frame, thrown))
                    {
                        throw new PyRaise(thrown);
                    }

                    continue;
                }

                // `yield` is an expression, so it leaves a value behind — whatever `send`
                // supplied, or None. Without it the `Pop` the compiler emits for `yield x`
                // as a statement takes the stack below empty, and a generator with a second
                // `yield` in it brings the host down rather than the program.
                frame.Push(channel.Sent ?? PyNone.Instance);
                channel.Sent = null;
                continue;
            }

            if (instruction.OpCode == OpCode.YieldFrom)
            {
                frame.InstructionPointer++;
                var source = frame.Pop();

                foreach (var value in VirtualMachine.RequireIterable(source))
                {
                    yield return value;
                }

                frame.Push(PyNone.Instance);
                continue;
            }

            // Everything up to the next yield runs on the main loop, which stops as soon
            // as it reaches one.
            var reachedYield = machine.RunUntilYield(frame);

            if (!reachedYield)
            {
                yield break;
            }
        }
    }
}
