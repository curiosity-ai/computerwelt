using System.Text;
using Computerwelt.Emulation.Bash;
using Computerwelt.Emulation.Bash.Builtins;
using Computerwelt.Emulation.Python;
using Computerwelt.Emulation.Python.Runtime;

// Both halves have an `ExecutionLimits`, and the type `Computerwelt` below shadows the
// namespace of the same name inside it — so the Python one is reached by alias.
using PythonLimits = Computerwelt.Emulation.Python.Runtime.ExecutionLimits;

namespace Computerwelt;

/// <summary>
/// The shell's <c>python</c> command, backed by the embedded interpreter.
/// </summary>
/// <remarks>
/// <para>
/// The script runs against the <i>shell's</i> virtual filesystem and inside the shell's
/// execution budget, so <c>echo hi &gt; f.txt &amp;&amp; python -c "print(open('f.txt').read())"</c>
/// works and neither half can reach the host. That is the reason both interpreters were
/// ported into one library.
/// </para>
/// <para>
/// Python's exit status follows the shell convention: 0 on success, 1 on an uncaught
/// exception, and the traceback goes to stderr exactly as CPython prints it. A script that
/// calls <c>sys.exit(n)</c> exits with <c>n</c> and no traceback, so
/// <c>python check.py || echo failed</c> works.
/// </para>
/// </remarks>
public sealed class PythonBuiltin : IBuiltin
{
    private readonly PythonOptions _options;

    /// <summary>Creates the builtin under <paramref name="name"/>.</summary>
    public PythonBuiltin(string name = "python", PythonOptions? options = null)
    {
        Name = name;
        _options = options ?? new PythonOptions();
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string? LlmHint =>
        "python: Runs Python in-process against the same virtual filesystem. "
        + "Supports `python file.py` and `python -c 'code'`. A subset of the stdlib is available.";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var (source, scriptName, argv, error) = await ResolveSourceAsync(context, cancellationToken);

        if (error is not null)
        {
            return error;
        }

        // The filesystem modules close over the shell's state, so `os.getcwd()` follows a
        // `cd` that happened earlier in the same script, and `open` reads the same files
        // the surrounding shell commands wrote.
        var options = _options.MaxSleep is null ? _options with { MaxSleep = context.Budget.Limits.MaxSleep } : _options;

        var runner = options.CreateRunner(new ShellFileSystem(
            context.FileSystem,
            () => context.State.WorkingDirectory,
            () => context.State.ExportedEnvironment()));

        // A sleep is shortened to what is left of the shell's timeout, exactly as the shell's own
        // `sleep` is, so a program cannot hold the run past it.
        runner.SleepBudget = () => context.Budget.Limits.Timeout - context.Budget.Elapsed;

        // `sys.argv` is the invocation as written, so `python script.py --flag value` reads
        // its own options the way a script assumes it can.
        runner.Arguments.AddRange(argv);

        // Standard input is the program itself when it was piped in, so there is nothing
        // left for the program to read; otherwise it is the pipeline's, which is what makes
        // `cat data | python -c '…sys.stdin.read()…'` a usable stage.
        if (scriptName != "<stdin>" && context.Stdin is { IsEmpty: false })
        {
            runner.StandardInput = context.StdinText;
        }

        // The machine runs synchronously, so it gets a thread of its own: run inline, it would
        // hold this call until the program ended, and `timeout` - which starts its clock only
        // once the command hands back a task - could never interrupt it. The shell's
        // cancellation stops the program between instructions and in the middle of a sleep.
        var result = await Task.Run(() => runner.Run(source!, scriptName, cancellationToken), cancellationToken);

        if (result.Succeeded)
        {
            return new ExecResult
            {
                Stdout = StreamData.FromText(Echoed(result, scriptName)),
                Stderr = StreamData.FromText(result.Stderr),

                // Zero unless the script called `sys.exit(n)`.
                ExitCode = result.ExitCode,
            };
        }

        return new ExecResult
        {
            Stdout = StreamData.FromText(result.Stdout),
            Stderr = StreamData.FromText(result.Stderr + result.Traceback + "\n"),
            ExitCode = ExitCodes.Failure,
        };
    }

    /// <summary>
    /// Standard output, with the value of a bare trailing expression appended when the
    /// program printed nothing itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what makes <c>python -c "2 + 3"</c> answer <c>5</c> rather than nothing, the
    /// way typing it at a prompt would. It applies only to a silent program: one that
    /// printed its own output is not second-guessed.
    /// </para>
    /// <para>
    /// And only to <c>-c</c>. A script file or a heredoc is a program, not an expression
    /// typed at a prompt, and one ending in <c>open(p, 'w').write(text)</c> — which is what
    /// a patch script looks like — would otherwise emit a stray byte count into whatever
    /// reads its output.
    /// </para>
    /// </remarks>
    private static string Echoed(RunResult result, string scriptName) =>
        scriptName == "<string>" && result.Stdout.Length == 0 && result.Value is not PyNone
            ? result.Value.Repr() + "\n"
            : result.Stdout;

    /// <summary>
    /// Works out what to run, what to call it in a traceback, and what <c>sys.argv</c>
    /// should be — which, as in CPython, starts with how the program was named
    /// (<c>-c</c>, <c>-</c> for standard input, or the script's path).
    /// </summary>
    private static async ValueTask<(string? Source, string ScriptName, List<string> Argv, ExecResult? Error)>
        ResolveSourceAsync(BuiltinContext context, CancellationToken cancellationToken)
    {
        var index = 0;

        while (index < context.Arguments.Count)
        {
            var argument = context.Arguments[index];

            if (argument == "-c")
            {
                if (index + 1 >= context.Arguments.Count)
                {
                    return (null, string.Empty, [], ExecResult.Usage("python", "argument to -c is missing"));
                }

                return (context.Arguments[index + 1], "<string>",
                    ["-c", .. context.Arguments.Skip(index + 2)], null);
            }

            // Flags that change nothing here are accepted and ignored, so that a script
            // written for CPython still runs.
            if (argument is "-u" or "-B" or "-E" or "-s" or "-S" or "-O" or "-I")
            {
                index++;
                continue;
            }

            if (argument == "--version" || argument == "-V")
            {
                return (null, string.Empty, [], ExecResult.Ok("Python 3.12.0 (monty)\n"));
            }

            break;
        }

        // No script, or an explicit `-`, means read the program from standard input —
        // which is what `python - <<'EOF'` in a shell script relies on.
        if (index >= context.Arguments.Count || context.Arguments[index] == "-")
        {
            return (context.StdinText, "<stdin>",
                ["-", .. context.Arguments.Skip(Math.Min(index + 1, context.Arguments.Count))], null);
        }

        var path = context.ResolvePath(context.Arguments[index]);

        try
        {
            var bytes = await context.FileSystem.ReadFileAsync(path, cancellationToken);

            return (Encoding.UTF8.GetString(bytes), context.Arguments[index],
                [.. context.Arguments.Skip(index)], null);
        }
        catch (FileSystemException)
        {
            return (null, string.Empty, [], ExecResult.Error(
                $"python: can't open file '{context.Arguments[index]}': No such file or directory\n",
                ExitCodes.Usage));
        }
    }
}

/// <summary>How the <c>python</c> builtin is configured.</summary>
public sealed record PythonOptions
{
    /// <summary>Maximum bytecode instructions per invocation.</summary>
    public long MaxInstructions { get; init; } = 20_000_000;

    /// <summary>Maximum Python call depth.</summary>
    public int MaxRecursionDepth { get; init; } = 200;

    /// <summary>
    /// Maximum path depth a tree walk will descend to.
    /// </summary>
    /// <remarks>
    /// The default matches <c>FsLimits.MaxDepth</c>, which is what the shell's filesystem
    /// will let a script <i>create</i> — so over that filesystem this can never be reached
    /// and costs nothing. Raise the two together, or a walk will refuse to enter a
    /// directory the shell was allowed to make.
    /// </remarks>
    public int MaxDirectoryDepth { get; init; } = 64;

    /// <summary>
    /// The longest a single <c>time.sleep</c> may wait, or <see langword="null"/> for the shell's.
    /// </summary>
    /// <remarks>
    /// Left null, the <c>python</c> command uses the shell's <c>ExecutionLimits.MaxSleep</c>, so
    /// one setting bounds <c>sleep</c> and <c>time.sleep</c> alike; a session opened with
    /// <see cref="StartSession"/>, which has no shell, gets the Python default of one hour.
    /// </remarks>
    public TimeSpan? MaxSleep { get; init; }

    /// <summary>
    /// Extra libraries the host makes importable from Python.
    /// </summary>
    /// <remarks>
    /// The route to add a Python library to the sandbox: each is built afresh for every
    /// <c>python</c> invocation and only if that invocation imports it, so a library may hold
    /// module-level state without one command's state leaking into the next. See
    /// <see cref="PythonLibrary"/>.
    /// </remarks>
    public IReadOnlyList<PythonLibrary> Libraries { get; init; } = [];

    /// <summary>
    /// Extra modules the host makes importable from Python.
    /// </summary>
    /// <remarks>
    /// One object, shared by every invocation of the command. Use it for a module with no
    /// state of its own; anything else belongs in <see cref="Libraries"/>, or one script's
    /// data becomes the next one's.
    /// </remarks>
    public Dictionary<string, PyObject> AdditionalModules { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Extra functions the host exposes to Python.</summary>
    public Dictionary<string, Func<PyObject[], PyObject>> ExternalFunctions { get; init; } =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Extra functions the host exposes to Python, each receiving the sandbox environment.
    /// </summary>
    /// <remarks>
    /// The context's filesystem is the shell's, and its working directory and environment are
    /// the shell's as they stand when the call is made — so host C# invoked from a Python
    /// program reads and writes exactly the files the surrounding script does. See
    /// <see cref="PythonHostContext"/>.
    /// </remarks>
    public Dictionary<string, Func<PythonHostContext, PyObject[], PyObject>> HostFunctions { get; init; } =
        new(StringComparer.Ordinal);

    /// <summary>Builds a runner configured from these options, over <paramref name="fileSystem"/>.</summary>
    public PythonRunner CreateRunner(IPyFileSystem? fileSystem)
    {
        var runner = new PythonRunner(new PythonLimits
        {
            MaxInstructions = MaxInstructions,
            MaxRecursionDepth = MaxRecursionDepth,
            MaxDirectoryDepth = MaxDirectoryDepth,
            MaxSleep = MaxSleep ?? PythonLimits.Default.MaxSleep,
        })
        {
            FileSystem = fileSystem,
        };

        foreach (var (name, module) in AdditionalModules)
        {
            runner.Modules[name] = module;
        }

        runner.Libraries.AddRange(Libraries);

        foreach (var (name, function) in ExternalFunctions)
        {
            runner.ExternalFunctions[name] = function;
        }

        foreach (var (name, function) in HostFunctions)
        {
            runner.HostFunctions[name] = function;
        }

        return runner;
    }

    /// <summary>
    /// Opens a notebook-style session — cells sharing one namespace — over a virtual
    /// filesystem, typically the one a <c>Bash</c> instance uses, so a notebook and the shell
    /// see the same files.
    /// </summary>
    /// <param name="fileSystem">The virtual filesystem.</param>
    /// <param name="workingDirectory">Where relative paths resolve.</param>
    /// <param name="environment">What <c>os.environ</c> holds.</param>
    public PythonSession StartSession(
        IFileSystem fileSystem,
        string workingDirectory = "/",
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        var cwd = VPath.Parse(workingDirectory);
        var variables = environment ?? new Dictionary<string, string>(StringComparer.Ordinal);

        return CreateRunner(new ShellFileSystem(fileSystem, () => cwd, () => variables)).StartSession();
    }
}

/// <summary>Convenience entry point for a session with both interpreters.</summary>
public static class Computerwelt
{
    /// <summary>
    /// Builds a shell whose <c>python</c> command runs against the same virtual filesystem.
    /// </summary>
    public static BashBuilder WithPython(this BashBuilder builder, PythonOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder
            .WithBuiltin(new PythonBuiltin("python", options))
            .WithBuiltin(new PythonBuiltin("python3", options));
    }
}
