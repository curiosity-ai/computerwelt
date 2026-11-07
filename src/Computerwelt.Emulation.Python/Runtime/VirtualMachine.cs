using System.Numerics;
using System.Text;
using Computerwelt.Emulation.Python.Compilation;

namespace Computerwelt.Emulation.Python.Runtime;

/// <summary>
/// The bytecode interpreter.
/// </summary>
/// <remarks>
/// <para>
/// A stack machine with a separate block stack for <c>try</c>, <c>finally</c> and context
/// managers. Python exceptions travel as <see cref="PyRaise"/>, so a raise inside a nested
/// C# call — an operator, a builtin, a comparison — unwinds to the enclosing frame's
/// handler without any explicit error propagation in the loop.
/// </para>
/// <para>
/// Resource limits are charged inside the loop rather than checked around it: a script that
/// spins in a tight loop must still terminate.
/// </para>
/// </remarks>
public sealed class VirtualMachine
{
    private readonly ExecutionLimits _limits;
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private long _instructionCount;
    private int _depth;

    /// <summary>Creates a machine with the given limits and module globals.</summary>
    public VirtualMachine(PyDict globals, PyDict builtins, ExecutionLimits? limits = null)
    {
        Globals = globals;
        Builtins = builtins;
        _limits = limits ?? ExecutionLimits.Default;
    }

    /// <summary>The module namespace.</summary>
    public PyDict Globals { get; }

    /// <summary>The builtin namespace, searched after globals.</summary>
    public PyDict Builtins { get; }

    /// <summary>The resource caps this machine enforces.</summary>
    public ExecutionLimits Limits => _limits;

    /// <summary>
    /// The call depth a program may reach, which <c>sys.setrecursionlimit</c> may lower.
    /// </summary>
    /// <remarks>
    /// It can be lowered but never raised past the configured cap: the limit is a safety
    /// bound on the host stack, and a script must not be able to lift it.
    /// </remarks>
    public int RecursionLimit
    {
        get => _recursionLimit ?? _limits.MaxRecursionDepth;
        set => _recursionLimit = Math.Clamp(value, 1, _limits.MaxRecursionDepth);
    }

    private int? _recursionLimit;

    /// <summary>Everything the program wrote to standard output.</summary>
    public string Stdout => _stdout.ToString();

    /// <summary>Everything the program wrote to standard error.</summary>
    public string Stderr => _stderr.ToString();

    /// <summary>Modules the host has made importable.</summary>
    public Dictionary<string, PyObject> Modules { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Consulted when an import names a module <see cref="Modules"/> does not hold.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is how a host library is built on first use rather than for every run that
    /// might not import it — and, for a library written in Python, the only moment at which
    /// it <i>can</i> be built, since running its source needs the machine that is asking.
    /// </para>
    /// <para>
    /// It resolves a name to a module; it does not search anywhere. A name no resolver
    /// claims is still <c>ModuleNotFoundError</c>, so the importable set stays the closed
    /// list the host wrote.
    /// </para>
    /// </remarks>
    public Func<string, PyObject?>? ModuleResolver { get; set; }

    /// <summary>Module names currently being resolved, so a cycle is reported rather than run.</summary>
    private readonly HashSet<string> _resolving = new(StringComparer.Ordinal);

    /// <summary>Work a host library asked to have done when the run ends.</summary>
    private readonly List<Action> _completions = [];

    /// <summary>
    /// Registers work to do when this run ends, however it ends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A host library is built per run and is otherwise never told the run is over, which is
    /// fine for a library that only computes and wrong for one holding something that has to
    /// be given back — a browser context, a handle, a lease. Without this the only thing
    /// that releases such a resource is the program remembering to, and a program that
    /// forgot is exactly the case the resource needs protecting from.
    /// </para>
    /// <para>
    /// Callbacks run after the program has finished and after an uncaught exception has been
    /// turned into a result, so nothing they do can change what the run reported.
    /// </para>
    /// </remarks>
    public void WhenRunCompleted(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        _completions.Add(work);
    }

    /// <summary>
    /// Runs the registered completions. Called once, by whoever started the run.
    /// </summary>
    /// <remarks>
    /// A callback that throws is swallowed, and this is the one place in the port where that
    /// is right: the program has already ended and its result is already decided, so there
    /// is no one left to report to — and a failure to hand back a resource must not turn an
    /// orderly ending into a crash. Each callback is attempted even if an earlier one threw,
    /// because one library's problem is not another's.
    /// </remarks>
    public void CompleteRun()
    {
        foreach (var work in _completions)
        {
            try
            {
                work();
            }
            catch (Exception)
            {
                // Deliberate: see the remarks above.
            }
        }

        _completions.Clear();
    }

    /// <summary>
    /// Stops the program from outside: once cancelled, the next instruction boundary (checked
    /// every 1024 instructions) throws <see cref="OperationCanceledException"/> out of the
    /// machine. It is a host exception on purpose — no <c>except</c> in the program can
    /// catch it, so an interrupt always interrupts.
    /// </summary>
    public CancellationToken Cancellation { get; set; }

    private void CheckCancellation()
    {
        if ((_instructionCount & 0x3FF) == 0 && Cancellation.IsCancellationRequested)
        {
            throw new OperationCanceledException(Cancellation);
        }
    }

    /// <summary>
    /// Readies a long-lived machine for its next unit of work — a notebook cell: the
    /// instruction budget, the call depth and the captured output start again, while
    /// globals, imported modules and everything the program built stay.
    /// </summary>
    public void ResetForNextRun()
    {
        _instructionCount = 0;
        _depth = 0;
        _stdout.Clear();
        _stderr.Clear();
        CallStack.Clear();
    }

    /// <summary>Characters written to standard output so far in this run.</summary>
    internal int StdoutLength => _stdout.Length;

    /// <summary>Characters written to standard error so far in this run.</summary>
    internal int StderrLength => _stderr.Length;

    /// <summary>Reads captured standard output from <paramref name="start"/> on.</summary>
    internal string StdoutFrom(int start) => _stdout.ToString(start, _stdout.Length - start);

    /// <summary>Reads captured standard error from <paramref name="start"/> on.</summary>
    internal string StderrFrom(int start) => _stderr.ToString(start, _stderr.Length - start);

    /// <summary>
    /// Where <c>display(obj)</c> sends what it is given, when the host has a place for rich
    /// output (a notebook); null leaves <c>display</c> undefined.
    /// </summary>
    public Action<PyObject, IReadOnlyDictionary<string, string>?>? DisplayHook { get; set; }

    /// <summary>Frames currently executing, innermost last. Used to build tracebacks.</summary>
    internal List<Frame> CallStack { get; } = [];

    /// <summary>Writes to the captured standard output.</summary>
    public void Write(string text) => _stdout.Append(text);

    /// <summary>Writes to the captured standard error.</summary>
    public void WriteError(string text) => _stderr.Append(text);

    /// <summary>Runs a module's code object in the program's own namespace.</summary>
    public PyObject RunModule(CodeObject code) => RunModule(code, Globals);

    /// <summary>
    /// Runs a module's code object in <paramref name="globals"/>.
    /// </summary>
    /// <remarks>
    /// A library written in Python is run this way, in a namespace of its own: functions it
    /// defines capture the namespace of the frame that made them, so its globals are the
    /// module's own and the importing program cannot reach into them except through the
    /// names the module ends up with.
    /// </remarks>
    public PyObject RunModule(CodeObject code, PyDict globals)
    {
        ArgumentNullException.ThrowIfNull(globals);

        var frame = new Frame(code, globals, [], null);
        return Execute(frame);
    }

    /// <summary>
    /// Builds a module the host registered but has not yet needed, and remembers it.
    /// </summary>
    /// <remarks>
    /// The result is cached in <see cref="Modules"/>, so importing the same library twice in
    /// one run yields one object — module state a program mutates is seen by every importer
    /// of it, as in CPython — while a fresh run starts from a fresh module.
    /// </remarks>
    private PyObject? ResolveModule(string name)
    {
        if (ModuleResolver is not { } resolver)
        {
            return null;
        }

        // A library that imports itself would otherwise recurse until the host's stack ran
        // out, which is a crash rather than an error a program can see.
        if (!_resolving.Add(name))
        {
            throw new PyRaise(new PyException(
                PyExceptionType.ImportError,
                $"cannot import name '{name}': it is already being imported"));
        }

        try
        {
            if (resolver(name) is not { } module)
            {
                return null;
            }

            Modules[name] = module;
            return module;
        }
        finally
        {
            _resolving.Remove(name);
        }
    }

    /// <summary>Calls any callable with positional and keyword arguments.</summary>
    public PyObject Call(PyObject callable, PyObject[] arguments, PyDict? keywords = null)
    {
        switch (callable)
        {
            case PyBuiltinFunction builtin:
                return builtin.Invoke(arguments, keywords);

            case PyBoundMethod method:
                return method.Invoke(arguments, keywords);

            case PyFunction function:
                return CallFunction(function, arguments, keywords);

            case PyType builtinType:
                return builtinType.Construct(arguments, keywords);

            case PyClass type:
                return type.Behaviour?.Call(this, type, arguments, keywords)
                    ?? Instantiate(type, arguments, keywords);

            case PyNamedTupleType namedTuple:
                return namedTuple.Instantiate(arguments, keywords);

            case PyCallable constructible when Computerwelt.Emulation.Python.Modules.DatetimeModule.TryConstruct(constructible, arguments, keywords) is { } built:
                return built;

            case PyExceptionType { Factory: { } factory }:
                return factory(arguments);

            case PyExceptionType exceptionType:
                return exceptionType.Instantiate(arguments);

            case PyMethod method:
                return Call(method.Function, [method.Self, .. arguments], keywords);

            case PyStaticMethod staticMethod:
                return Call(staticMethod.Function, arguments, keywords);

            default:
                return CallThroughDunder(callable, arguments, keywords);
        }
    }

    /// <summary>
    /// How far a chain of <c>__call__</c> attributes is followed before it is called a
    /// cycle.
    /// </summary>
    /// <remarks>
    /// Two objects whose <c>__call__</c> is the other would otherwise recurse until the
    /// host's stack ran out, which is a crash rather than an error a program can catch.
    /// Nothing legitimate needs more than a hop or two.
    /// </remarks>
    private const int MaxCallableChain = 8;

    /// <summary>
    /// Calls an object through its <c>__call__</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Upstream does not dispatch this dunder — <c>.reference/monty/limitations/classes.md</c>
    /// lists it among the protocols user-defined instances do not get — so this is an
    /// addition rather than a port, covered by <c>tests/monty-extensions/class__call.py</c>.
    /// It contradicts nothing upstream pins: an instance whose class has no <c>__call__</c>
    /// is still not callable, with the same message.
    /// </para>
    /// <para>
    /// It is what lets a host library expose something that is both callable and a namespace,
    /// which several real Python APIs are, and it costs one attribute lookup on a path that
    /// was already about to raise.
    /// </para>
    /// </remarks>
    private PyObject CallThroughDunder(PyObject callable, PyObject[] arguments, PyDict? keywords)
    {
        var target = callable;

        // A `__call__` may itself be an object carrying one. The chain is followed in a
        // bounded loop rather than by recursing back into Call, so a cycle is reported
        // instead of exhausting the host's stack.
        for (var hop = 0; hop < MaxCallableChain; hop++)
        {
            if (target.GetAttribute("__call__") is not { } implementation
                || ReferenceEquals(implementation, target))
            {
                break;
            }

            // Everything the switch above knows how to call; anything else is another
            // object that may carry a `__call__` of its own.
            if (implementation is PyCallable or PyExceptionType)
            {
                return Call(implementation, arguments, keywords);
            }

            target = implementation;
        }

        // The message names whatever the chain ended on, which for an object with no
        // `__call__` at all is the object the program called. That is the rule the rest of
        // the port already follows for a dunder that is present but is not a function —
        // `tests/monty-spec/class__contains.py` pins the same shape for `__contains__` —
        // and it is what CPython reports.
        throw new PyRaise(PyErrors.TypeError($"'{target.TypeName}' object is not callable"));
    }

    /// <summary>
    /// Calls a class: <c>__new__</c> builds the instance, then <c>__init__</c> initializes
    /// it when <c>__new__</c> returned one of this class's own instances.
    /// </summary>
    internal PyObject Instantiate(PyClass type, PyObject[] arguments, PyDict? keywords, bool runInit = true)
    {
        // A class built by `type(name, bases, namespace)` learns its machine on first use.
        type.Machine ??= this;
        RefuseAbstract(type);

        PyObject instance;
        var initializer = type.Lookup("__init__");

        if (type.LookupWithOwner("__new__") is { } custom)
        {
            // `__new__` is a static method whether or not it was declared one.
            var constructor = custom.Value is PyStaticMethod declared ? declared.Function : custom.Value;
            instance = Call(constructor, [type, .. arguments], keywords);

            var ours = instance switch
            {
                PyInstance created => created.Class.IsSubclassOf(type),
                PyException { UserClass: { } owner } => owner.IsSubclassOf(type),
                _ => false,
            };

            if (!ours || !runInit)
            {
                return instance;
            }
        }
        else if (type.ExceptionType is { } exceptionType)
        {
            // An exception's arguments are its `args` before any `__init__` runs, as
            // `BaseException.__new__` arranges — which is why a subclass that never calls
            // `super().__init__` still has them.
            var template = exceptionType.NearestBuiltin().Instantiate(
                initializer is null && keywords is not { Count: > 0 } ? arguments : arguments);

            instance = new PyException(exceptionType, template.Message, template.Arguments)
            {
                UserClass = type,
                Fields = template.Fields is null ? null : new(template.Fields, StringComparer.Ordinal),
            };

            if (initializer is null)
            {
                if (keywords is { Count: > 0 })
                {
                    throw new PyRaise(PyErrors.TypeError($"{type.Name}() takes no keyword arguments"));
                }

                return instance;
            }
        }
        else
        {
            // Without a user `__init__`, a mutable built-in base takes the arguments
            // itself, as `dict.__init__` would.
            instance = ClassModel.CreateInstance(this, type, arguments, keywords, fillMutable: initializer is null);

            if (initializer is null)
            {
                if ((arguments.Length > 0 || keywords is { Count: > 0 }) && type.BuiltinBase is null)
                {
                    throw new PyRaise(PyErrors.TypeError($"{type.Name}() takes no arguments"));
                }

                return instance;
            }
        }

        if (!runInit)
        {
            return instance;
        }

        switch (initializer)
        {
            // An initializer produces the instance by mutating it, so returning anything is
            // a mistake worth reporting rather than quietly dropping.
            case PyFunction function:
                Initialized(CallFunction(function.Bind(instance), arguments, keywords));
                break;

            // A decorator such as `@dataclass` installs a builtin `__init__`, which is
            // unbound and therefore takes the receiver as its first argument. A builtin the
            // class body merely assigned — `__init__ = print` — is not a descriptor and so
            // gets the constructor's arguments alone.
            case PyBuiltinFunction builtin:
                Initialized(builtin.Invoke(
                    builtin.BindsAsMethod ? [instance, .. arguments] : arguments, keywords));
                break;

            case PyClassMethod or PyStaticMethod:
                Initialized(Call(ClassModel.BindToInstance(this, initializer, instance, type), arguments, keywords));
                break;

            case null:
                break;

            // Anything else in the slot — a class, a bound method, a non-callable — is
            // called as it stands, with no receiver of ours. It gets a recursion level of
            // its own because `A.__init__ = A` re-enters here before any frame is pushed,
            // and nothing else would charge the budget.
            case var other:
            {
                using var level = EnterRecursion();
                Initialized(Call(other, arguments, keywords));
                break;
            }
        }

        return instance;
    }

    /// <summary>
    /// Refuses to instantiate a class that still has abstract methods, as <c>abc.ABC</c>
    /// classes do in CPython.
    /// </summary>
    private static void RefuseAbstract(PyClass type)
    {
        if (type.Lookup("__abstractmethods__") is not PyObject marker || marker is PyNone)
        {
            return;
        }

        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var owner in type.Mro)
        {
            foreach (var (key, _) in owner.Members.Entries)
            {
                var name = key.Display();

                if (!seen.Add(name))
                {
                    continue;
                }

                if (type.Lookup(name) is { } member && IsAbstract(member))
                {
                    missing.Add(name);
                }
            }
        }

        if (missing.Count == 0)
        {
            return;
        }

        missing.Sort(StringComparer.Ordinal);
        var plural = missing.Count == 1 ? "method" : "methods";

        throw new PyRaise(PyErrors.TypeError(
            $"Can't instantiate abstract class {type.Name} without an implementation for abstract {plural} "
            + string.Join(", ", missing.Select(static m => $"'{m}'"))));

        static bool IsAbstract(PyObject member) =>
            member.GetAttribute("__isabstractmethod__") is { } flag && flag.IsTruthy();
    }

    /// <summary>Rejects an <c>__init__</c> that returned anything but None.</summary>
    private static void Initialized(PyObject returned)
    {
        if (returned is not PyNone)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"__init__() should return None, not '{returned.TypeName}'"));
        }
    }

    /// <summary>
    /// Counts one level of host-side recursion against the call-depth limit.
    /// </summary>
    /// <remarks>
    /// A generated method — a dataclass's <c>__eq__</c> or <c>__repr__</c> — recurses in C#
    /// rather than in bytecode, so it has to charge the same budget or a cyclic structure
    /// would overflow the host stack instead of raising <c>RecursionError</c>.
    /// </remarks>
    public IDisposable EnterRecursion()
    {
        if (++_depth > RecursionLimit)
        {
            _depth--;
            throw new PyRaise(new PyException(
                PyExceptionType.RecursionError, "maximum recursion depth exceeded"));
        }

        return new RecursionLevel(this);
    }

    private sealed class RecursionLevel(VirtualMachine machine) : IDisposable
    {
        public void Dispose() => machine._depth--;
    }

    /// <summary>
    /// Resolves an awaited value.
    /// </summary>
    /// <remarks>
    /// Only a coroutine or a settled future can be awaited. Anything else is a mistake in
    /// the program rather than something to pass through, and saying so is the difference
    /// between a typo being caught and it silently doing nothing.
    /// </remarks>
    public PyObject Await(PyObject value) => value switch
    {
        PyCoroutine coroutine => coroutine.Resolve(),
        PyFuture future => future.Resolve(),
        _ => throw new PyRaise(PyErrors.TypeError($"'{value.TypeName}' object can't be awaited")),
    };

    private PyObject CallFunction(PyFunction function, PyObject[] arguments, PyDict? keywords)
    {
        if (++_depth > RecursionLimit)
        {
            _depth--;
            throw new PyRaise(new PyException(
                PyExceptionType.RecursionError, "maximum recursion depth exceeded"));
        }

        try
        {
            var locals = BindArguments(function, arguments, keywords);
            var cells = BuildCells(function, locals);
            var frame = new Frame(function.Code, function.Globals, locals, cells);

            if (function.Code.IsGenerator)
            {
                // A generator call runs nothing yet; it materializes lazily on iteration.
                return new PyGenerator(this, frame);
            }

            // Calling an `async def` produces a coroutine; the body runs when it is awaited.
            if (function.Code.IsCoroutine)
            {
                return new PyCoroutine(function.Name, () => Execute(frame));
            }

            return Execute(frame);
        }
        finally
        {
            _depth--;
        }
    }

    private Dictionary<string, PyCell>? BuildCells(PyFunction function, PyObject?[] locals)
    {
        var code = function.Code;

        // Nothing captured either way: the frame gets no cell dictionary at all.
        if (function.Closure.Count == 0 && code.CellNames.Count == 0)
        {
            return null;
        }

        var cells = new Dictionary<string, PyCell>(StringComparer.Ordinal);

        // The inherited cells are the free variables this function reads. A name it binds
        // itself shadows the enclosing one completely: `def f(x)` nested in a scope whose
        // own `x` is captured must see its parameter, never the cell next door.
        foreach (var (name, inherited) in function.Closure)
        {
            if (!code.LocalNames.Contains(name))
            {
                cells[name] = inherited;
            }
        }

        // A local this function's body reads through a cell must be shared, not copied,
        // so nested closures observe later assignments.
        foreach (var name in code.CellNames)
        {
            if (cells.ContainsKey(name))
            {
                continue;
            }

            var cell = new PyCell();
            var slot = code.LocalNames.IndexOf(name);

            if (slot >= 0 && slot < locals.Length)
            {
                cell.Value = locals[slot];
            }

            cells[name] = cell;
        }

        return cells;
    }

    /// <summary>
    /// Reports an unknown or duplicated keyword, which CPython does before it complains
    /// about surplus positional arguments.
    /// </summary>
    private static void RejectBadKeywords(PyFunction function, PyDict? keywords, HashSet<string> bound)
    {
        var parameters = function.Code.Parameters;

        foreach (var (key, _) in keywords?.Entries ?? [])
        {
            var name = key.Display();

            if (bound.Contains(name))
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"{function.Code.Name}() got multiple values for argument '{name}'"));
            }

            var known = parameters.Parameters.Any(parameter => parameter.Name == name)
                || parameters.KeywordOnly.Any(parameter => parameter.Name == name);

            if (!known && parameters.KeywordArgs is null)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"{function.Code.Name}() got an unexpected keyword argument '{name}'"));
            }
        }
    }

    /// <summary>Formats a list of parameter names the way CPython's arity errors do.</summary>
    private static string Join(IReadOnlyList<string> names) => names.Count switch
    {
        1 => $"'{names[0]}'",
        2 => $"'{names[0]}' and '{names[1]}'",
        _ => string.Join(", ", names.Take(names.Count - 1).Select(name => $"'{name}'"))
            + $", and '{names[^1]}'",
    };

    /// <summary>Binds call arguments to parameter slots, applying defaults and packing varargs.</summary>
    private PyObject?[] BindArguments(PyFunction function, PyObject[] arguments, PyDict? keywords)
    {
        var code = function.Code;
        var parameters = code.Parameters;
        var locals = new PyObject?[Math.Max(code.LocalNames.Count, 1)];
        var declaredParameters = parameters.Parameters;

        // The ordinary call: one argument per parameter and nothing to reconcile. What
        // follows exists for the shapes that need reconciling — defaults, varargs,
        // keywords, a bound receiver — and none of it says anything about this one, so it
        // is skipped rather than walked through. A recursive function pays this per call.
        if (arguments.Length == declaredParameters.Count
            && keywords is null or { Count: 0 }
            && function.BoundSelf is null
            && parameters.VarArgs is null
            && parameters.KeywordArgs is null
            && parameters.KeywordOnly.Count == 0)
        {
            var slots = code.ParameterSlots;

            for (var i = 0; i < slots.Length; i++)
            {
                locals[slots[i]] = arguments[i];
            }

            return locals;
        }

        var positional = new List<PyObject>(arguments);

        if (function.BoundSelf is { } instance)
        {
            positional.Insert(0, instance);
        }

        var bound = new HashSet<string>(StringComparer.Ordinal);
        var declared = parameters.Parameters;

        var supplied = Math.Min(positional.Count, declared.Count);

        for (var i = 0; i < supplied; i++)
        {
            locals[code.LocalNames.IndexOf(declared[i].Name)] = positional[i];
            bound.Add(declared[i].Name);
        }

        var extra = positional.Skip(declared.Count).ToList();

        if (parameters.VarArgs is { } varArgs)
        {
            locals[code.LocalNames.IndexOf(varArgs)] = new PyTuple(extra);
        }
        else if (extra.Count > 0)
        {
            // A keyword error is reported first, because CPython binds the keywords before
            // it counts the positionals.
            RejectBadKeywords(function, keywords, bound);

            // With defaults the arity is a range, and CPython says so.
            var required = declared.Count(parameter =>
                !function.Defaults.TryGetValue(new PyStr(parameter.Name), out _));

            // Keyword-only arguments that bound are counted in a suffix, so the numbers in
            // the message add up to what the caller actually passed.
            var keywordOnly = parameters.KeywordOnly
                .Count(parameter => keywords?.TryGetValue(new PyStr(parameter.Name), out _) == true);

            throw new PyRaise(PyErrors.TypeError(
                $"{code.Name}() takes "
                + (required == declared.Count
                    ? $"{declared.Count} positional argument{(declared.Count == 1 ? string.Empty : "s")}"
                    : $"from {required} to {declared.Count} positional arguments")
                + " but "
                + (keywordOnly == 0
                    ? $"{positional.Count} were given"
                    : $"{positional.Count} positional arguments "
                        + $"(and {keywordOnly} keyword-only argument{(keywordOnly == 1 ? string.Empty : "s")}) "
                        + "were given")));
        }

        var leftoverKeywords = new PyDict();

        if (keywords is not null)
        {
            foreach (var (key, value) in keywords.Entries)
            {
                var name = key.Display();
                var slot = code.LocalNames.IndexOf(name);
                var positionalOnly = declared.Take(parameters.PositionalOnlyCount).Any(p => p.Name == name);

                // A parameter before `/` can only be filled positionally; naming it is an
                // error even though the name exists.
                if (positionalOnly)
                {
                    if (parameters.KeywordArgs is null)
                    {
                        throw new PyRaise(PyErrors.TypeError(
                            $"{code.Name}() got some positional-only arguments passed as keyword arguments: '{name}'"));
                    }

                    leftoverKeywords.Set(key, value);
                    continue;
                }

                var isParameter = declared.Any(p => p.Name == name) || parameters.KeywordOnly.Any(p => p.Name == name);

                if (isParameter && slot >= 0)
                {
                    if (!bound.Add(name))
                    {
                        throw new PyRaise(PyErrors.TypeError(
                            $"{code.Name}() got multiple values for argument '{name}'"));
                    }

                    locals[slot] = value;
                    continue;
                }

                if (parameters.KeywordArgs is null)
                {
                    throw new PyRaise(PyErrors.TypeError(
                        $"{code.Name}() got an unexpected keyword argument '{name}'"));
                }

                leftoverKeywords.Set(key, value);
            }
        }

        if (parameters.KeywordArgs is { } keywordArgs)
        {
            locals[code.LocalNames.IndexOf(keywordArgs)] = leftoverKeywords;
        }

        // Every missing parameter is collected before reporting, because CPython names
        // them all in one message.
        var missing = new List<string>();

        foreach (var parameter in declared.Concat(parameters.KeywordOnly))
        {
            if (bound.Contains(parameter.Name))
            {
                continue;
            }

            var slot = code.LocalNames.IndexOf(parameter.Name);

            if (function.Defaults.TryGetValue(new PyStr(parameter.Name), out var defaultValue))
            {
                locals[slot] = defaultValue;
                continue;
            }

            missing.Add(parameter.Name);
        }

        if (missing.Count > 0)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"{code.Name}() missing {missing.Count} required positional "
                + $"argument{(missing.Count == 1 ? string.Empty : "s")}: {Join(missing)}"));
        }

        return locals;
    }

    /// <summary>One activation record.</summary>
    internal sealed class Frame
    {
        private Dictionary<string, PyCell>? _cells;

        public Frame(CodeObject code, PyDict globals, PyObject?[] locals, Dictionary<string, PyCell>? cells)
        {
            Code = code;
            Globals = globals;
            Locals = locals.Length >= code.LocalNames.Count
                ? locals
                : [.. locals, .. new PyObject?[code.LocalNames.Count - locals.Length]];
            _cells = cells;
        }

        public CodeObject Code { get; }

        public PyDict Globals { get; }

        public PyObject?[] Locals { get; }

        /// <summary>
        /// The closure cells, made real on first need.
        /// </summary>
        /// <remarks>
        /// A function that neither captures nor is captured has none, which is most of
        /// them, and a frame is built per call — so the dictionary is not built until
        /// something writes a cell. <see cref="TryGetCell"/> is the read side, and answers
        /// without hashing the name when there is nothing to search.
        /// </remarks>
        public Dictionary<string, PyCell> Cells =>
            _cells ??= new Dictionary<string, PyCell>(StringComparer.Ordinal);

        public bool TryGetCell(string name, out PyCell cell)
        {
            if (_cells is null)
            {
                cell = null!;
                return false;
            }

            return _cells.TryGetValue(name, out cell!);
        }

        public List<PyObject> Stack { get; } = [];

        public List<Block> Blocks { get; } = [];

        public int InstructionPointer { get; set; }

        public PyException? CurrentException { get; set; }

        public int CurrentLine { get; set; }

        public void Push(PyObject value) => Stack.Add(value);

        public PyObject Pop()
        {
            var value = Stack[^1];
            Stack.RemoveAt(Stack.Count - 1);
            return value;
        }

        public PyObject Peek(int depth = 0) => Stack[^(depth + 1)];
    }

    /// <summary>An entry on the block stack.</summary>
    /// <summary>One entry of a frame's block stack.</summary>
    /// <param name="Kind">What the block protects.</param>
    /// <param name="Handler">Where control goes when it is triggered.</param>
    /// <param name="StackDepth">The value-stack depth to restore.</param>
    /// <param name="Saved">
    /// For a <see cref="BlockKind.Handler"/> block, the exception that was current before
    /// the handler began — restored when the handler ends, however it ends.
    /// </param>
    internal readonly record struct Block(
        BlockKind Kind,
        int Handler,
        int StackDepth,
        PyException? Saved = null);

    /// <summary>What a block protects.</summary>
    internal enum BlockKind
    {
        Except,
        Finally,
        With,

        /// <summary>
        /// A running exception handler.
        /// </summary>
        /// <remarks>
        /// Pushed when control enters an <c>except</c> clause or an exception-path
        /// <c>finally</c>, and carrying the exception state to put back. That restore is
        /// what makes a handler that raises a new exception leave no trace of the one it was
        /// handling — so a later bare <c>raise</c> finds nothing rather than the wrong thing.
        /// </remarks>
        Handler,
    }

    /// <summary>A control transfer that is not an exception: a return out of a frame.</summary>
    private sealed class ReturnSignal(PyObject value) : Exception
    {
        public PyObject Value { get; } = value;
    }

    /// <summary>
    /// Runs a frame until it reaches a <c>yield</c> or finishes. Returns true when a yield
    /// was reached, leaving the frame suspended for the generator to resume.
    /// </summary>
    internal bool RunUntilYield(Frame frame)
    {
        while (frame.InstructionPointer < frame.Code.Instructions.Count)
        {
            var instruction = frame.Code.Instructions[frame.InstructionPointer];

            if (instruction.OpCode is OpCode.Yield or OpCode.YieldFrom)
            {
                return true;
            }

            frame.InstructionPointer++;
            frame.CurrentLine = instruction.Line;

            if (++_instructionCount > _limits.MaxInstructions)
            {
                throw new PyRaise(PyErrors.RuntimeError("instruction limit exceeded"));
            }

            CheckCancellation();

            try
            {
                if (Step(frame, instruction, out _))
                {
                    return false;
                }
            }
            catch (PyRaise raise)
            {
                if (!Unwind(frame, raise.Exception))
                {
                    RecordTraceback(raise.Exception, frame);
                    throw;
                }
            }
        }

        return false;
    }

    /// <summary>Runs a frame to completion and returns its result.</summary>
    internal PyObject Execute(Frame frame)
    {
        CallStack.Add(frame);

        try
        {
            while (true)
            {
                if (frame.InstructionPointer >= frame.Code.Instructions.Count)
                {
                    return PyNone.Instance;
                }

                if (++_instructionCount > _limits.MaxInstructions)
                {
                    throw new PyRaise(PyErrors.RuntimeError("instruction limit exceeded"));
                }

                CheckCancellation();

                var instruction = frame.Code.Instructions[frame.InstructionPointer++];
                frame.CurrentLine = instruction.Line;

                try
                {
                    if (Step(frame, instruction, out var result))
                    {
                        return result;
                    }
                }
                catch (PyRaise raise)
                {
                    if (!Unwind(frame, raise.Exception))
                    {
                        RecordTraceback(raise.Exception, frame);
                        throw;
                    }
                }
            }
        }
        finally
        {
            CallStack.RemoveAt(CallStack.Count - 1);
        }
    }

    /// <summary>
    /// The instruction after the <c>ReRaise</c> that closes a <c>with</c>'s handler chain.
    /// </summary>
    /// <remarks>
    /// A manager that swallows the exception must skip the outer managers' handlers as
    /// well: they are only reached while an exception is in flight. The chain holds nothing
    /// but handler calls and that final re-raise, so the first one found ends it.
    /// </remarks>
    private static int SkipHandlers(CodeObject code, int from)
    {
        for (var i = from; i < code.Instructions.Count; i++)
        {
            if (code.Instructions[i].OpCode == OpCode.ReRaise)
            {
                return i + 1;
            }
        }

        return code.Instructions.Count;
    }

    /// <summary>
    /// Looks up a special method the way a protocol does: on the type, never on the
    /// instance.
    /// </summary>
    private PyObject Protocol(PyObject value, string name) =>
        (value is PyInstance instance ? instance.Dunder(name) : Attributes.TryGet(this, value, name))
        ?? throw new PyRaise(PyErrors.TypeError(
            $"'{value.TypeName}' object does not support the context manager protocol "
            + $"(missed {name} method)"));

    private void RecordTraceback(PyException exception, Frame frame) =>
        exception.Traceback.Insert(0, new TracebackFrame(frame.Code.Name, frame.CurrentLine, null));

    /// <summary>
    /// Finds a handler for <paramref name="exception"/> in the frame's block stack and
    /// transfers control to it. Returns false when the frame has no handler.
    /// </summary>
    internal static bool Unwind(Frame frame, PyException exception)
    {
        while (frame.Blocks.Count > 0)
        {
            var block = frame.Blocks[^1];

            // A `with` block stays registered: its handler pops it, the same way the
            // normal path's ExitWith does, so both find the manager's exit the same way.
            if (block.Kind != BlockKind.With)
            {
                frame.Blocks.RemoveAt(frame.Blocks.Count - 1);
            }

            if (frame.Stack.Count > block.StackDepth)
            {
                frame.Stack.RemoveRange(block.StackDepth, frame.Stack.Count - block.StackDepth);
            }

            // An exception raised while handling another records the first as its context.
            if (frame.CurrentException is { } previous && !ReferenceEquals(previous, exception))
            {
                exception.Context ??= previous;
            }

            // Leaving a handler this way ends it: its exception stops being current before
            // the new one goes looking further out.
            if (block.Kind == BlockKind.Handler)
            {
                frame.CurrentException = block.Saved;
                continue;
            }

            frame.Blocks.Add(new Block(
                BlockKind.Handler, block.Handler, block.StackDepth, frame.CurrentException));

            frame.CurrentException = exception;
            frame.InstructionPointer = block.Handler;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Executes one instruction. Returns true when the frame is finished, with
    /// <paramref name="result"/> set to its return value.
    /// </summary>
    private bool Step(Frame frame, Instruction instruction, out PyObject result)
    {
        result = PyNone.Instance;
        var code = frame.Code;

        switch (instruction.OpCode)
        {
            case OpCode.Nop:
                return false;

            case OpCode.LoadConst:
                frame.Push(code.Constants[instruction.Operand]);
                return false;

            case OpCode.LoadLocal:
            {
                var name = code.LocalNames[instruction.Operand];

                // A captured local lives in its cell, which a nested function may have
                // written since the slot was last set. The cell is therefore always at
                // least as fresh as the slot, so it wins whenever one exists.
                if (frame.TryGetCell(name, out var cell))
                {
                    if (cell.Value is { } cellValue)
                    {
                        frame.Push(cellValue);
                        return false;
                    }

                    throw new PyRaise(PyErrors.UnboundLocalError(name));
                }

                var value = frame.Locals[instruction.Operand]
                    ?? throw new PyRaise(PyErrors.UnboundLocalError(name));

                frame.Push(value);
                return false;
            }

            case OpCode.LoadName:
            {
                var name = code.LocalNames[instruction.Operand];

                if (frame.Locals[instruction.Operand] is { } bound)
                {
                    frame.Push(bound);
                    return false;
                }

                var key = code.LocalKey(instruction.Operand);

                if (frame.Globals.TryGetValue(key, out var global))
                {
                    frame.Push(global);
                    return false;
                }

                frame.Push(Builtins.TryGetValue(key, out var builtin)
                    ? builtin
                    : throw new PyRaise(PyErrors.NameError(name)));

                return false;
            }

            case OpCode.StoreLocal:
            {
                var value = frame.Pop();
                frame.Locals[instruction.Operand] = value;

                // Keep a captured local's cell in step, so closures see the new value.
                var name = code.LocalNames[instruction.Operand];
                if (frame.TryGetCell(name, out var cell))
                {
                    cell.Value = value;
                }

                return false;
            }

            case OpCode.DeleteLocal:
            {
                frame.Locals[instruction.Operand] = null;

                // Keep a captured local's cell in step, as StoreLocal does: a closure that
                // reads the deleted name must see it unbound, not the stale value.
                if (frame.TryGetCell(code.LocalNames[instruction.Operand], out var cell))
                {
                    cell.Value = null;
                }

                return false;
            }

            case OpCode.LoadGlobal:
            {
                var name = code.Names[instruction.Operand];
                var key = code.NameKey(instruction.Operand);

                if (frame.Globals.TryGetValue(key, out var value))
                {
                    frame.Push(value);
                    return false;
                }

                if (Builtins.TryGetValue(key, out var builtin))
                {
                    frame.Push(builtin);
                    return false;
                }

                throw new PyRaise(PyErrors.NameError(name));
            }

            case OpCode.StoreGlobal:
                frame.Globals.Set(code.NameKey(instruction.Operand), frame.Pop());
                return false;

            case OpCode.DeleteGlobal:
                if (!frame.Globals.Remove(code.NameKey(instruction.Operand)))
                {
                    throw new PyRaise(PyErrors.NameError(code.Names[instruction.Operand]));
                }

                return false;

            case OpCode.LoadCell:
            {
                var name = code.CellNames[instruction.Operand];

                if (!frame.TryGetCell(name, out var cell) || cell.Value is null)
                {
                    // The frame that owns the variable reports it as its own unassigned
                    // local; one that merely captured the cell names it a free variable.
                    throw new PyRaise(code.LocalNames.Contains(name)
                        ? PyErrors.UnboundLocalError(name)
                        : PyErrors.UnboundFreeVariable(name));
                }

                frame.Push(cell.Value);
                return false;
            }

            case OpCode.DeleteCell:
            {
                var name = code.CellNames[instruction.Operand];

                if (frame.TryGetCell(name, out var cell))
                {
                    cell.Value = null;
                }

                return false;
            }

            case OpCode.StoreCell:
            {
                var name = code.CellNames[instruction.Operand];

                if (!frame.TryGetCell(name, out var cell))
                {
                    cell = new PyCell();
                    frame.Cells[name] = cell;
                }

                cell.Value = frame.Pop();
                return false;
            }

            case OpCode.LoadAttr:
            {
                var target = frame.Pop();
                var name = code.Names[instruction.Operand];
                frame.Push(Attributes.Get(this, target, name));
                return false;
            }

            case OpCode.StoreAttr:
            {
                var target = frame.Pop();
                var value = frame.Pop();
                var name = code.Names[instruction.Operand];

                if (!target.SetAttribute(name, value))
                {
                    throw new PyRaise(PyErrors.AttributeError(target.TypeName, name));
                }

                return false;
            }

            case OpCode.LoadSubscript:
            {
                var index = frame.Pop();
                var target = frame.Pop();
                frame.Push(target.GetItem(index));
                return false;
            }

            case OpCode.StoreSubscript:
            {
                var index = frame.Pop();
                var target = frame.Pop();
                var value = frame.Pop();
                target.SetItem(index, value);
                return false;
            }

            case OpCode.DeleteSubscript:
            {
                var index = frame.Pop();
                frame.Pop().DeleteItem(index);
                return false;
            }

            case OpCode.BinaryOp:
            {
                var right = frame.Pop();
                var left = frame.Pop();
                frame.Push(Operators.Binary(code.Names[instruction.Operand], left, right));
                return false;
            }

            case OpCode.UnaryOp:
                frame.Push(Operators.Unary(code.Names[instruction.Operand], frame.Pop()));
                return false;

            case OpCode.CompareOp:
            {
                var right = frame.Pop();
                var left = frame.Pop();
                frame.Push(Operators.Compare(code.Names[instruction.Operand], left, right));
                return false;
            }

            case OpCode.BuildList:
                frame.Push(new PyList(PopMany(frame, instruction.Operand)));
                return false;

            case OpCode.BuildTuple:
            {
                // A -1 operand means "convert the list on top", used after unpacking.
                if (instruction.Operand < 0)
                {
                    var list = (PyList)frame.Pop();
                    frame.Push(new PyTuple(list.Items));
                    return false;
                }

                frame.Push(new PyTuple(PopMany(frame, instruction.Operand)));
                return false;
            }

            case OpCode.BuildSet:
                frame.Push(new PySet(PopMany(frame, instruction.Operand)));
                return false;

            case OpCode.BuildMap:
            {
                var pairs = PopMany(frame, instruction.Operand * 2);
                var dict = new PyDict();

                for (var i = 0; i < pairs.Count; i += 2)
                {
                    dict.Set(pairs[i], pairs[i + 1]);
                }

                frame.Push(dict);
                return false;
            }

            case OpCode.BuildSlice:
            {
                var step = frame.Pop();
                var stop = frame.Pop();
                var start = frame.Pop();
                frame.Push(new PySlice(start, stop, step));
                return false;
            }

            case OpCode.BuildString:
            {
                var pieces = PopMany(frame, instruction.Operand);
                var builder = new StringBuilder();

                foreach (var piece in pieces)
                {
                    builder.Append(piece.Display());
                }

                frame.Push(new PyStr(builder.ToString()));
                return false;
            }

            case OpCode.FormatValue:
            {
                var spec = frame.Pop();
                var value = frame.Pop();

                var converted = (char)instruction.Operand switch
                {
                    'r' => new PyStr(value.Repr()),
                    's' => new PyStr(value.Display()),
                    'a' => new PyStr(PyStr.Ascii(value.Repr())),
                    _ => value,
                };

                var specText = spec is PyNone ? string.Empty : spec.Display();
                frame.Push(new PyStr(StringFormatter.Format(converted, specText)));
                return false;
            }

            case OpCode.ListAppend:
            {
                var value = frame.Pop();
                ((PyList)frame.Peek(instruction.Operand - 1)).Items.Add(value);
                return false;
            }

            case OpCode.ListExtend:
            {
                var iterable = frame.Pop();
                var target = (PyList)frame.Peek(instruction.Operand - 1);

                // A list or tuple display words a non-iterable's refusal its own way; a set
                // display and a sequence unpack each word it differently again. A class that
                // opted out with `__iter__ = None` keeps the plain wording everywhere.
                var filled = iterable is PyInstance opted && opted.Dunder("__iter__") is PyNone;

                try
                {
                    target.Items.AddRange(RequireIterable(iterable));
                }
                catch (PyRaise raise)
                    when (!filled && raise.Exception.Message == $"'{iterable.TypeName}' object is not iterable")
                {
                    throw new PyRaise(PyErrors.TypeError(
                        $"Value after * must be an iterable, not {iterable.TypeName}"));
                }

                return false;
            }

            case OpCode.SetAdd:
            {
                var value = frame.Pop();
                ((PySet)frame.Peek(instruction.Operand - 1)).Add(value);
                return false;
            }

            case OpCode.SetUpdate:
            {
                var iterable = frame.Pop();
                var target = (PySet)frame.Peek(instruction.Operand - 1);

                foreach (var item in RequireIterable(iterable))
                {
                    target.Add(item);
                }

                return false;
            }

            case OpCode.MapPut:
            {
                var value = frame.Pop();
                var key = frame.Pop();
                ((PyDict)frame.Peek(instruction.Operand - 2)).Set(key, value);
                return false;
            }

            case OpCode.MapMerge:
            {
                var incoming = frame.Pop();
                var keywordTarget = (PyDict)frame.Peek(instruction.Operand - 1);

                // The callable sits under the positional list, which sits under the map. A
                // bound method is named with its receiver's type, as CPython names it:
                // `list.sort()`, not `sort()`.
                var name = frame.Peek(instruction.Operand + 1) switch
                {
                    PyBoundMethod method => $"{method.Receiver.TypeName}.{method.Name}",
                    PyCallable callee => callee.Name,
                    PyExceptionType type => type.Name,
                    _ => "function",
                };

                if (incoming is not PyDict merged)
                {
                    throw new PyRaise(PyErrors.TypeError(
                        $"{name}() argument after ** must be a mapping, not {incoming.TypeName}"));
                }

                foreach (var (key, value) in merged.Entries)
                {
                    if (keywordTarget.TryGetValue(key, out _))
                    {
                        throw new PyRaise(PyErrors.TypeError(
                            $"{name}() got multiple values for keyword argument '{key.Display()}'"));
                    }

                    keywordTarget.Set(key, value);
                }

                return false;
            }

            case OpCode.MapUpdate:
            {
                var mapping = frame.Pop();
                var target = (PyDict)frame.Peek(instruction.Operand - 1);

                // A dict display words this differently from a call, which names the
                // callable and the `**`.
                if (mapping is not PyDict source)
                {
                    throw new PyRaise(PyErrors.TypeError(
                        $"'{mapping.TypeName}' object is not a mapping"));
                }

                foreach (var (key, value) in source.Entries)
                {
                    target.Set(key, value);
                }

                return false;
            }

            case OpCode.Pop:
                frame.Pop();
                return false;

            case OpCode.Duplicate:
            {
                // A non-zero operand duplicates the top N values as a group.
                var count = Math.Max(1, instruction.Operand);
                var values = new PyObject[count];

                for (var i = 0; i < count; i++)
                {
                    values[i] = frame.Peek(count - 1 - i);
                }

                foreach (var value in values)
                {
                    frame.Push(value);
                }

                return false;
            }

            case OpCode.Swap:
            {
                var top = frame.Pop();
                var second = frame.Pop();
                frame.Push(top);
                frame.Push(second);
                return false;
            }

            case OpCode.RotateThree:
            {
                var top = frame.Pop();
                var second = frame.Pop();
                var third = frame.Pop();
                frame.Push(top);
                frame.Push(third);
                frame.Push(second);
                return false;
            }

            case OpCode.Jump:
                frame.InstructionPointer = instruction.Operand;
                return false;

            case OpCode.JumpIfFalse:
                if (!frame.Pop().IsTruthy())
                {
                    frame.InstructionPointer = instruction.Operand;
                }

                return false;

            case OpCode.JumpIfTrue:
                if (frame.Pop().IsTruthy())
                {
                    frame.InstructionPointer = instruction.Operand;
                }

                return false;

            case OpCode.JumpIfFalseOrPop:
                if (!frame.Peek().IsTruthy())
                {
                    frame.InstructionPointer = instruction.Operand;
                }
                else
                {
                    frame.Pop();
                }

                return false;

            case OpCode.JumpIfTrueOrPop:
                if (frame.Peek().IsTruthy())
                {
                    frame.InstructionPointer = instruction.Operand;
                }
                else
                {
                    frame.Pop();
                }

                return false;

            case OpCode.GetIterator:
            {
                var iterable = frame.Pop();
                frame.Push(iterable is PyIterator or PyGenerator ? iterable : new PyIterator(RequireIterable(iterable)));
                return false;
            }

            case OpCode.ForIterate:
            {
                var iterator = frame.Peek();
                var next = iterator switch
                {
                    PyIterator sequence => sequence.Next(),
                    PyGenerator generator => generator.Next(),
                    _ => throw new PyRaise(PyErrors.TypeError($"'{iterator.TypeName}' object is not an iterator")),
                };

                if (next is null)
                {
                    frame.Pop();
                    frame.InstructionPointer = instruction.Operand;
                    return false;
                }

                frame.Push(next);
                return false;
            }

            case OpCode.UnpackSequence:
            {
                var source = frame.Pop();
                var wanted = instruction.Operand;

                // One item past the target count is enough to detect a surplus, and stopping
                // there leaves the rest of an iterator available — which is what CPython does.
                var values = new List<PyObject>(wanted);

                using (var items = Unpackable(source).GetEnumerator())
                {
                    while (values.Count <= wanted && items.MoveNext())
                    {
                        values.Add(items.Current);
                    }
                }

                if (values.Count != wanted)
                {
                    // A list, a tuple and a dict are unpacked without the iterator protocol,
                    // so their length is known and the surplus can be counted; anything else
                    // stopped at the first extra item and never learned the total.
                    var total = source switch
                    {
                        PyList list => list.Items.Count,
                        PyDict dict => dict.Count,
                        PyTuple tuple => tuple.Items.Count,
                        _ => (int?)null,
                    };

                    throw new PyRaise(PyErrors.ValueError(values.Count < wanted
                        ? $"not enough values to unpack (expected {wanted}, got {values.Count})"
                        : total is { } known
                            ? $"too many values to unpack (expected {wanted}, got {known})"
                            : $"too many values to unpack (expected {wanted})"));
                }

                // Pushed in reverse so the first target pops first.
                for (var i = values.Count - 1; i >= 0; i--)
                {
                    frame.Push(values[i]);
                }

                return false;
            }

            case OpCode.UnpackStarred:
            {
                var starIndex = instruction.Operand >> 16;
                var total = instruction.Operand & 0xFFFF;
                var values = Unpackable(frame.Pop()).ToList();
                var after = total - starIndex - 1;

                if (values.Count < total - 1)
                {
                    throw new PyRaise(PyErrors.ValueError(
                        $"not enough values to unpack (expected at least {total - 1}, got {values.Count})"));
                }

                var unpacked = new List<PyObject>(total);
                unpacked.AddRange(values.Take(starIndex));
                unpacked.Add(new PyList(values.Skip(starIndex).Take(values.Count - starIndex - after).ToList()));
                unpacked.AddRange(values.Skip(values.Count - after));

                for (var i = unpacked.Count - 1; i >= 0; i--)
                {
                    frame.Push(unpacked[i]);
                }

                return false;
            }

            case OpCode.Call:
            {
                var arguments = PopMany(frame, instruction.Operand).ToArray();
                var callable = frame.Pop();
                frame.Push(Call(callable, arguments));
                return false;
            }

            case OpCode.CallKeyword:
            {
                var keywords = (PyDict)frame.Pop();
                var positional = (PyList)frame.Pop();
                var callable = frame.Pop();

                // Every key is checked before the call, so `f(**{1: 2})` reports the bad
                // key rather than whatever the callee makes of it.
                foreach (var (key, _) in keywords.Entries)
                {
                    if (key is not PyStr)
                    {
                        throw new PyRaise(PyErrors.TypeError("keywords must be strings"));
                    }
                }

                frame.Push(Call(callable, [.. positional.Items], keywords.Count > 0 ? keywords : null));
                return false;
            }

            case OpCode.Await:
                frame.Push(Await(frame.Pop()));
                return false;

            case OpCode.Return:
                result = frame.Pop();
                return true;

            case OpCode.Yield:
                throw new PyRaise(PyErrors.RuntimeError("yield outside a generator"));

            case OpCode.YieldFrom:
                throw new PyRaise(PyErrors.RuntimeError("yield from outside a generator"));

            case OpCode.MakeFunction:
            {
                var defaults = (PyDict)frame.Pop();
                var nested = code.NestedCode[instruction.Operand];
                frame.Push(new PyFunction(nested, frame.Cells, frame.Globals, defaults));
                return false;
            }

            case OpCode.MakeClass:
            {
                var body = (PyFunction)frame.Pop();
                var keywords = (PyDict)frame.Pop();
                var bases = (PyTuple)frame.Pop();
                var name = frame.Pop().Display();
                frame.Push(BuildClass(name, body, bases, keywords));
                return false;
            }

            case OpCode.LoadClassCell:
            {
                if (!frame.TryGetCell("__class__", out var cell) || cell.Value is not PyClass owner)
                {
                    throw new PyRaise(PyErrors.RuntimeError("super(): __class__ cell not found"));
                }

                frame.Push(owner);
                return false;
            }

            case OpCode.DeleteAttr:
            {
                var target = frame.Pop();
                var name = code.Names[instruction.Operand];

                if (!target.DeleteAttribute(name))
                {
                    throw new PyRaise(PyErrors.AttributeError(target.TypeName, name));
                }

                return false;
            }

            case OpCode.Raise:
                DoRaise(frame, instruction.Operand);
                return false;

            case OpCode.SetupExcept:
                frame.Blocks.Add(new Block(BlockKind.Except, instruction.Operand, frame.Stack.Count));
                return false;

            case OpCode.SetupFinally:
                frame.Blocks.Add(new Block(BlockKind.Finally, instruction.Operand, frame.Stack.Count));
                return false;

            case OpCode.PopBlock:
                if (frame.Blocks.Count > 0)
                {
                    frame.Blocks.RemoveAt(frame.Blocks.Count - 1);
                }

                return false;

            case OpCode.PushCurrentException:
                frame.Push(frame.CurrentException ?? (PyObject)PyNone.Instance);
                return false;

            case OpCode.MatchException:
            {
                var expected = frame.Pop();
                var actual = frame.CurrentException;

                if (actual is null)
                {
                    frame.Push(PyBool.False);
                    return false;
                }

                frame.Push(PyBool.Of(MatchesExceptionType(actual, expected)));
                return false;
            }

            case OpCode.EndHandler:
                frame.CurrentException = EndHandler(frame);
                return false;

            case OpCode.ReRaise:
                if (frame.CurrentException is { } pending)
                {
                    frame.CurrentException = EndHandler(frame);
                    throw new PyRaise(pending);
                }

                return false;

            case OpCode.SetupWith:
            {
                var manager = frame.Pop();

                // The protocol reads through the type, not the instance, so an instance
                // attribute of the same name does not shadow the method. `__exit__` is
                // checked first, so an object with neither is reported against it — which
                // is the half CPython names.
                var exit = Protocol(manager, "__exit__");
                var enter = Protocol(manager, "__enter__");

                // `__enter__` runs before the block is registered: a manager whose entry
                // raises was never entered, so its `__exit__` must not run.
                var entered = Call(enter, []);

                frame.Push(exit);
                frame.Blocks.Add(new Block(BlockKind.With, instruction.Operand, frame.Stack.Count));
                frame.Push(entered);
                return false;
            }

            case OpCode.ExitWithException:
            {
                // The exception is in flight and the manager's `__exit__` sits in the slot
                // its block recorded; a truthy return means the manager handled it.
                var index = frame.Blocks.FindLastIndex(static b => b.Kind == BlockKind.With);

                if (index < 0)
                {
                    return false;
                }

                var slot = frame.Blocks[index].StackDepth - 1;
                var exit = frame.Stack[slot];
                frame.Stack.RemoveAt(slot);
                frame.Blocks.RemoveAt(index);

                var raised = frame.CurrentException
                    ?? throw new PyRaise(PyErrors.RuntimeError("no active exception to exit"));

                var handled = Call(exit, [raised.ExceptionType, raised, PyNone.Instance]);

                if (handled.IsTruthy())
                {
                    frame.CurrentException = null;
                    frame.InstructionPointer = SkipHandlers(code, frame.InstructionPointer);
                }

                return false;
            }

            case OpCode.ExitWith:
            {
                // The exit is taken from the slot the block recorded, not from the top of
                // the stack: a `return` out of the body leaves its value above it.
                var index = frame.Blocks.FindLastIndex(static b => b.Kind == BlockKind.With);

                if (index < 0)
                {
                    return false;
                }

                var slot = frame.Blocks[index].StackDepth - 1;
                var exit = frame.Stack[slot];
                frame.Stack.RemoveAt(slot);
                frame.Blocks.RemoveAt(index);

                Call(exit, [PyNone.Instance, PyNone.Instance, PyNone.Instance]);
                return false;
            }

            case OpCode.ImportName:
            {
                var name = code.Names[instruction.Operand];

                if (!Modules.TryGetValue(name, out var module))
                {
                    module = ResolveModule(name) ?? throw new PyRaise(PyErrors.ModuleNotFound(name));
                }

                frame.Push(module);
                return false;
            }

            case OpCode.ImportFrom:
            {
                var module = frame.Pop();
                var name = code.Names[instruction.Operand];

                frame.Push(module.GetAttribute(name)
                    ?? throw new PyRaise(new PyException(
                        PyExceptionType.ImportError, $"cannot import name '{name}'")));

                return false;
            }

            case OpCode.MatchSequenceCheck:
            {
                var subject = frame.Peek();
                var count = instruction.Operand >> 1;
                var starred = (instruction.Operand & 1) == 1;
                var length = PatternSequenceLength(subject);

                frame.Push(PyBool.Of(length is { } known && (starred ? known >= count - 1 : known == count)));
                return false;
            }

            case OpCode.MatchMappingCheck:
                frame.Push(PyBool.Of(ClassModel.Unwrap(frame.Peek()) is PyDict));
                return false;

            case OpCode.MatchKeys:
            {
                var keys = ((PyTuple)frame.Pop()).Items;
                var mapping = (PyDict)ClassModel.Unwrap(frame.Pop());
                var values = new List<PyObject>(keys.Count);
                var seen = new PyDict();

                foreach (var key in keys)
                {
                    if (seen.Contains(key))
                    {
                        throw new PyRaise(PyErrors.ValueError($"mapping pattern checks duplicate key ({key.Repr()})"));
                    }

                    seen.Set(key, PyNone.Instance);

                    // A plain lookup: a defaultdict's factory must not run for a pattern.
                    if (!mapping.TryGetValue(key, out var value))
                    {
                        frame.Push(PyNone.Instance);
                        return false;
                    }

                    values.Add(value);
                }

                frame.Push(new PyTuple(values));
                return false;
            }

            case OpCode.DictWithoutKeys:
            {
                var keys = ((PyTuple)frame.Pop()).Items;
                var mapping = (PyDict)ClassModel.Unwrap(frame.Pop());
                var rest = new PyDict();

                foreach (var (key, value) in mapping.Entries)
                {
                    if (!keys.Any(k => SameOrEqualKey(k, key)))
                    {
                        rest.Set(key, value);
                    }
                }

                frame.Push(rest);
                return false;
            }

            case OpCode.MatchClass:
            {
                var names = ((PyTuple)frame.Pop()).Items;
                var type = frame.Pop();
                var subject = frame.Pop();
                frame.Push(MatchClassPattern(subject, type, instruction.Operand, names));
                return false;
            }

            case OpCode.ImportStar:
            {
                var module = frame.Pop();

                foreach (var (name, value) in PublicNames(module))
                {
                    frame.Globals.Set(new PyStr(name), value);
                }

                return false;
            }

            case OpCode.Assert:
            {
                var message = instruction.Operand == 1 ? frame.Pop().Display() : string.Empty;
                throw new PyRaise(PyErrors.AssertionError(message));
            }

            default:
                throw new PyRaise(PyErrors.RuntimeError($"unimplemented opcode {instruction.OpCode}"));
        }
    }

    /// <summary>
    /// The length of a value a sequence pattern can match, or null for one it cannot:
    /// strings and bytes are deliberately not sequences to <c>match</c>, nor are mappings.
    /// </summary>
    private static int? PatternSequenceLength(PyObject subject) => ClassModel.Unwrap(subject) switch
    {
        PyStr or PyBytes or PyByteArray or PyDict => null,
        PyList list => list.Items.Count,
        PyTuple tuple => tuple.Items.Count,
        PyRange range => range.Length(),
        PyDeque deque => deque.Length(),
        _ => null,
    };

    private static bool SameOrEqualKey(PyObject left, PyObject right) =>
        ReferenceEquals(left, right) || left.PyEquals(right);

    /// <summary>The types whose class pattern's single positional sub-pattern matches the subject itself.</summary>
    private static readonly HashSet<string> SelfMatchingTypes = new(StringComparer.Ordinal)
    {
        "bool", "bytearray", "bytes", "dict", "float", "frozenset", "int", "list", "set", "str", "tuple",
    };

    /// <summary>
    /// <c>case Cls(p1, p2, k=p3)</c>: the subject's attributes, positional ones named by
    /// <c>__match_args__</c>, or None when the subject is not an instance or lacks one.
    /// </summary>
    private PyObject MatchClassPattern(PyObject subject, PyObject type, int positional, IReadOnlyList<PyObject> names)
    {
        if (type is not (PyClass or PyType or PyExceptionType or PyNamedTupleType or Computerwelt.Emulation.Python.Modules.DatetimeModule.IPyClassLike))
        {
            throw new PyRaise(PyErrors.TypeError("called match pattern must be a class"));
        }

        if (!TypeRegistry.IsInstance(subject, type))
        {
            return PyNone.Instance;
        }

        var values = new List<PyObject>(positional + names.Count);

        if (positional > 0)
        {
            if (type is PyType builtin && SelfMatchingTypes.Contains(builtin.Name))
            {
                if (positional > 1)
                {
                    throw new PyRaise(PyErrors.TypeError(
                        $"{builtin.Name}() accepts 1 positional sub-pattern ({positional} given)"));
                }

                values.Add(subject);
            }
            else
            {
                var matchArgs = type switch
                {
                    PyNamedTupleType namedTuple => namedTuple.Fields.Select(static f => (PyObject)new PyStr(f)).ToList(),
                    _ => Attributes.TryGet(this, type, "__match_args__") is { } declared
                        ? declared is PyTuple tuple
                            ? tuple.Items.ToList()
                            : throw new PyRaise(PyErrors.TypeError(
                                $"{TypeName(type)}.__match_args__ must be a tuple (got {declared.TypeName})"))
                        : [],
                };

                if (positional > matchArgs.Count)
                {
                    throw new PyRaise(PyErrors.TypeError(
                        $"{TypeName(type)}() accepts {matchArgs.Count} positional sub-pattern{(matchArgs.Count == 1 ? string.Empty : "s")} ({positional} given)"));
                }

                for (var i = 0; i < positional; i++)
                {
                    if (Attributes.TryGet(this, subject, matchArgs[i].Display()) is not { } value)
                    {
                        return PyNone.Instance;
                    }

                    values.Add(value);
                }
            }
        }

        foreach (var name in names)
        {
            if (Attributes.TryGet(this, subject, name.Display()) is not { } value)
            {
                return PyNone.Instance;
            }

            values.Add(value);
        }

        return new PyTuple(values);

        static string TypeName(PyObject type) => type is PyCallable callable ? callable.Name : type.TypeName;
    }

    /// <summary>
    /// What <c>from m import *</c> binds: the names in <c>__all__</c> when the module
    /// declares it, otherwise every name not starting with an underscore.
    /// </summary>
    private static IEnumerable<(string Name, PyObject Value)> PublicNames(PyObject module)
    {
        if (module.GetAttribute("__all__") is { } declared)
        {
            foreach (var entry in RequireIterable(declared))
            {
                var name = entry.Display();

                yield return (name, module.GetAttribute(name)
                    ?? throw new PyRaise(PyErrors.AttributeError("module", name)));
            }

            yield break;
        }

        var names = module switch
        {
            Modules.PyModuleObject builtIn => builtIn.AttributeNames.ToList(),
            PySourceModule source => source.AttributeNames.ToList(),
            _ => throw new PyRaise(new PyException(
                PyExceptionType.ImportError, "import * is not supported for this object")),
        };

        foreach (var name in names)
        {
            if (!name.StartsWith('_') && module.GetAttribute(name) is { } value)
            {
                yield return (name, value);
            }
        }
    }

    /// <summary>
    /// Builds a class from its statement: resolves the bases, runs the body, and then does
    /// what <c>type.__new__</c> does once the class exists — fill the <c>__class__</c>
    /// cell, call <c>__set_name__</c> on descriptors and <c>__init_subclass__</c> on the
    /// parent.
    /// </summary>
    private PyObject BuildClass(string name, PyFunction body, PyTuple bases, PyDict keywords)
    {
        var resolved = new List<PyObject>();
        Modules.TypeAlias? special = null;

        foreach (var candidate in bases.Items)
        {
            switch (candidate)
            {
                // `Generic[T]` and `Protocol` exist for type checkers; at run time they add
                // nothing, so they are dropped from the bases as CPython's `__mro_entries__`
                // would reduce them to `Generic`.
                case Modules.TypeAlias alias when alias.Name is "Generic" or "Protocol":
                    break;

                case Modules.TypeAlias alias when alias.Name is "NamedTuple" or "TypedDict":
                    special = alias;
                    break;

                case PyClass or PyType or PyExceptionType:
                    resolved.Add(candidate);
                    break;

                default:
                    throw new PyRaise(PyErrors.TypeError(
                        $"bases must be types, not {candidate.TypeName}"));
            }
        }

        var metaclass = keywords.TryGetValue(new PyStr("metaclass"), out var declared) ? declared : null;

        if (metaclass is not null)
        {
            keywords.Remove(new PyStr("metaclass"));

            // `abc.ABCMeta` is the one metaclass a program may name: abstract-method
            // checking is built into instantiation here, so it needs no metaclass at all.
            if (!(metaclass is PyClass { Name: "ABCMeta" } || ReferenceEquals(metaclass, TypeRegistry.Type)))
            {
                throw new PyRaise(new PyException(
                    PyExceptionType.NotImplementedError, "custom metaclasses are not supported"));
            }
        }

        var classCell = new PyCell();
        var members = RunClassBody(body, classCell);

        if (special is not null)
        {
            return special.Name == "NamedTuple"
                ? BuildNamedTuple(name, members)
                : BuildTypedDict(name);
        }

        if (metaclass is PyClass { Name: "ABCMeta" } && !members.TryGetValue(new PyStr("__abstractmethods__"), out _))
        {
            members.Set(new PyStr("__abstractmethods__"), PyBool.True);
        }

        // `__new__` and `__init_subclass__` are implicitly static and class methods.
        if (members.TryGetValue(new PyStr("__init_subclass__"), out var hook) && hook is PyFunction initSubclass)
        {
            members.Set(new PyStr("__init_subclass__"), new PyClassMethod(initSubclass));
        }

        if (members.TryGetValue(new PyStr("__class_getitem__"), out var getter) && getter is PyFunction classGetItem)
        {
            members.Set(new PyStr("__class_getitem__"), new PyClassMethod(classGetItem));
        }

        var type = new PyClass(name, members, resolved, this);
        classCell.Value = type;

        foreach (var (key, value) in members.Entries)
        {
            switch (value)
            {
                case PyProperty property:
                    property.Name ??= key.Display();
                    break;

                case PyInstance descriptor when descriptor.Dunder("__set_name__") is { } setName:
                    descriptor.Invoke(setName, [type, key]);
                    break;
            }
        }

        type.Behaviour?.Created(this, type, keywords);

        // The parent's hook, not the new class's own: it is how a base registers or
        // validates its subclasses.
        if (type.Mro.Count > 1)
        {
            var parentHook = new PySuper(this, type, type).GetAttribute("__init_subclass__");

            if (parentHook is not null)
            {
                Call(parentHook, [], keywords.Count > 0 ? keywords : null);
            }
            else if (keywords.Count > 0)
            {
                throw new PyRaise(PyErrors.TypeError($"{name}.__init_subclass__() takes no keyword arguments"));
            }
        }
        else if (keywords.Count > 0)
        {
            throw new PyRaise(PyErrors.TypeError($"{name}.__init_subclass__() takes no keyword arguments"));
        }

        return type;
    }

    /// <summary><c>class P(NamedTuple)</c>: the annotations are the fields, assignments their defaults.</summary>
    private static PyObject BuildNamedTuple(string name, PyDict members)
    {
        var fields = new List<string>();
        var defaults = new List<PyObject>();

        if (members.TryGetValue(new PyStr("__annotations__"), out var annotations) && annotations is PyDict declared)
        {
            foreach (var (key, _) in declared.Entries)
            {
                var field = key.Display();
                fields.Add(field);

                if (members.TryGetValue(key, out var value))
                {
                    defaults.Add(value);
                }
                else if (defaults.Count > 0)
                {
                    throw new PyRaise(PyErrors.TypeError(
                        $"Non-default namedtuple field {field} cannot follow default field"));
                }
            }
        }

        var type = new PyNamedTupleType(name, fields, defaults);

        // Methods and class attributes in the body travel with the type.
        foreach (var (key, value) in members.Entries)
        {
            var member = key.Display();

            if (!fields.Contains(member) && member is not "__annotations__" and not "__module__" and not "__qualname__")
            {
                type.Methods[member] = value;
            }
        }

        return type;
    }

    /// <summary><c>class Movie(TypedDict)</c>: calling it builds a plain dict, as CPython's does.</summary>
    private static PyObject BuildTypedDict(string name) =>
        new PyType(name, static value => value is PyDict, static (arguments, keywords) =>
            TypeRegistry.Dict.Construct(arguments, keywords));

    private PyDict RunClassBody(PyFunction body, PyCell classCell)
    {
        var locals = new PyObject?[body.Code.LocalNames.Count];
        var cells = new Dictionary<string, PyCell>(body.Closure, StringComparer.Ordinal)
        {
            // The implicit cell zero-argument `super()` reads; filled once the class exists.
            ["__class__"] = classCell,
        };

        var frame = new Frame(body.Code, body.Globals, locals, cells);

        Execute(frame);

        var members = new PyDict();

        for (var i = 0; i < frame.Code.LocalNames.Count; i++)
        {
            if (frame.Locals[i] is { } value)
            {
                members.Set(new PyStr(frame.Code.LocalNames[i]), value);
            }
        }

        // A class body at module scope stores into globals, so pick those up too.
        foreach (var name in frame.Code.Names)
        {
            if (frame.Globals.TryGetValue(new PyStr(name), out var value) && !members.Contains(new PyStr(name)))
            {
                _ = value;
            }
        }

        return members;
    }

    /// <summary>
    /// Ends the innermost running handler, returning the exception state to put back.
    /// </summary>
    private static PyException? EndHandler(Frame frame)
    {
        if (frame.Blocks.Count == 0 || frame.Blocks[^1].Kind != BlockKind.Handler)
        {
            return null;
        }

        var saved = frame.Blocks[^1].Saved;
        frame.Blocks.RemoveAt(frame.Blocks.Count - 1);
        return saved;
    }

    /// <summary>Tests a caught exception against an <c>except</c> clause's type.</summary>
    /// <remarks>
    /// A tuple of types is allowed, but only one level deep: CPython does not descend into
    /// a nested tuple, it rejects it — and rejects it while evaluating the clause, so the
    /// error propagates past the remaining handlers of the same <c>try</c>.
    /// </remarks>
    private static bool MatchesExceptionType(PyException actual, PyObject expected) => expected switch
    {
        PyExceptionType type => actual.IsInstanceOf(type),
        // Every element is validated, not just the ones up to the first match: an invalid
        // element raises even when an earlier one already matched.
        PyTuple tuple => tuple.Items.Count(item => MatchesOneType(actual, item)) > 0,
        _ => MatchesOneType(actual, expected),
    };

    private static bool MatchesOneType(PyException actual, PyObject expected) => expected switch
    {
        PyExceptionType type => actual.IsInstanceOf(type),

        // A Python class is an exception type when it derives from one; any other class is
        // at least a class, and simply never matches.
        PyClass { ExceptionType: { } type } => actual.IsInstanceOf(type),
        PyClass => false,
        _ => throw new PyRaise(PyErrors.TypeError(
            "catching classes that do not inherit from BaseException is not allowed")),
    };

    private void DoRaise(Frame frame, int argumentCount)
    {
        if (argumentCount == 0)
        {
            throw new PyRaise(frame.CurrentException
                ?? PyErrors.RuntimeError("No active exception to reraise"));
        }

        PyException? cause = null;
        var suppress = false;

        if (argumentCount == 2)
        {
            // `from None` is the one cause that is not an exception: it hides the context.
            var given = frame.Pop();
            suppress = given is PyNone;
            cause = suppress ? null : ToException(given);
        }

        var exception = ToException(frame.Pop());
        exception.Cause = cause;
        exception.Context ??= frame.CurrentException;
        exception.SuppressContext = suppress || exception.SuppressContext;

        throw new PyRaise(exception);
    }

    private PyException ToException(PyObject value) => value switch
    {
        PyException exception => exception,
        PyExceptionType type => new PyException(type, string.Empty),
        PyClass { ExceptionType: not null } type => Call(type, []) as PyException
            ?? throw new PyRaise(PyErrors.TypeError("exceptions must derive from BaseException")),
        _ => throw new PyRaise(PyErrors.TypeError("exceptions must derive from BaseException")),
    };

    private static List<PyObject> PopMany(Frame frame, int count)
    {
        if (count <= 0)
        {
            return [];
        }

        var start = frame.Stack.Count - count;
        var values = frame.Stack.GetRange(start, count);
        frame.Stack.RemoveRange(start, count);
        return values;
    }

    /// <summary>
    /// The items of an unpacking source, wording a non-iterable's refusal the way
    /// unpacking does rather than the way <c>iter()</c> does.
    /// </summary>
    private static IEnumerable<PyObject> Unpackable(PyObject source)
    {
        // A class that sets `__iter__ = None` has the slot filled, so every site — unpacking
        // included — reports it plainly rather than in its own words.
        var optedOut = source is PyInstance instance && instance.Dunder("__iter__") is PyNone;
        IEnumerator<PyObject> items;

        try
        {
            items = RequireIterable(source).GetEnumerator();
        }
        catch (PyRaise raise)
            when (!optedOut && raise.Exception.Message == $"'{source.TypeName}' object is not iterable")
        {
            throw new PyRaise(PyErrors.TypeError($"cannot unpack non-iterable {source.TypeName} object"));
        }

        using (items)
        {
            while (items.MoveNext())
            {
                yield return items.Current;
            }
        }
    }

    /// <summary>
    /// Iterates <paramref name="value"/>, raising a <c>TypeError</c> when it is not
    /// iterable.
    /// </summary>
    public static IEnumerable<PyObject> RequireIterable(PyObject value) =>
        value.Iterate()
        ?? (value as PyGenerator)?.Iterate()
        ?? throw new PyRaise(PyErrors.TypeError($"'{value.TypeName}' object is not iterable"));

    /// <summary>
    /// A pull function over an iterable, returning null at the end.
    /// </summary>
    /// <remarks>
    /// This is not <c>RequireIterable().GetEnumerator()</c>: a C# iterator block that
    /// throws reports itself finished afterwards, so a Python source whose
    /// <c>__next__</c> raises would silently be skipped rather than raising again on the
    /// next call. Pulling from the object itself keeps the source in place.
    /// </remarks>
    public static Func<PyObject?> Puller(PyObject value)
    {
        switch (value)
        {
            case PyIterator iterator:
                return iterator.Next;

            case PyGenerator generator:
                return generator.Next;

            case PyInstance instance when instance.Dunder("__next__") is not null:
                return () =>
                {
                    try
                    {
                        return instance.Invoke(instance.Dunder("__next__")!, []);
                    }
                    catch (PyRaise raise)
                        when (raise.Exception.ExceptionType == PyExceptionType.StopIteration)
                    {
                        return null;
                    }
                };

            case PyInstance outer when outer.Dunder("__iter__") is { } start:
                return Puller(outer.Invoke(start, []));

            default:
            {
                var items = RequireIterable(value).GetEnumerator();
                return () => items.MoveNext() ? items.Current : null;
            }
        }
    }
}

/// <summary>Per-run resource caps.</summary>
public sealed record ExecutionLimits
{
    /// <summary>The defaults.</summary>
    public static ExecutionLimits Default { get; } = new();

    /// <summary>Maximum bytecode instructions executed. Default 50 million.</summary>
    public long MaxInstructions { get; init; } = 50_000_000;

    /// <summary>Maximum Python call depth. Default 200.</summary>
    public int MaxRecursionDepth { get; init; } = 200;

    /// <summary>Maximum characters written to stdout and stderr. Default 10 MB.</summary>
    public int MaxOutputCharacters { get; init; } = 10_000_000;

    /// <summary>
    /// Maximum path depth a tree walk will descend to. Default 64.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately the same default as the shell filesystem's own <c>MaxDepth</c>, which
    /// refuses to <i>create</i> a path deeper than this — so over that filesystem the cap
    /// can never be reached and costs nothing. It matters for a host that supplies its own
    /// <see cref="IPyFileSystem"/> over storage this sandbox did not build, where a tree can
    /// be arbitrarily deep or, with links, unbounded.
    /// </para>
    /// <para>
    /// Reaching it raises rather than truncating: a walk that quietly stopped part-way
    /// would report a subset of the tree as though it were all of it.
    /// </para>
    /// </remarks>
    public int MaxDirectoryDepth { get; init; } = 64;
}
