using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Computerwelt.Emulation.Python;

namespace Computerwelt.Benchmarks;

/// <summary>
/// The workloads in <c>Workloads/</c>, timed the way <c>cpython_compare.py</c> times them
/// under CPython, and — given that script's JSON — the two side by side.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>fresh</b>: a new <see cref="PythonRunner"/> run per execution — new machine,
/// nothing imported, parse and compile included. The sandbox's equivalent of CPython's fresh
/// <c>python3</c> process, minus the process.</item>
/// <item><b>warm</b>: a cell of a live <see cref="PythonSession"/> whose imports are already
/// done — the equivalent of <c>exec</c> in a long-lived interpreter (a Jupyter kernel).</item>
/// </list>
/// A stopwatch median over about a second per number, after a warm-up that also lets the JIT
/// settle. Coarser than BenchmarkDotNet, and meant for ratios against CPython, not for
/// believing a microsecond.
/// </remarks>
internal static class CPythonComparison
{
    public static int Run(string[] args)
    {
        var directory = Locate();
        Dictionary<string, (double Fresh, double Warm)>? cpython = null;
        string? version = null;

        if (args.Length > 0)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(args[0]));
            version = document.RootElement.GetProperty("python").GetString();
            cpython = document.RootElement.GetProperty("workloads").EnumerateObject().ToDictionary(
                static w => w.Name,
                static w => (w.Value.GetProperty("fresh_us").GetDouble(), w.Value.GetProperty("warm_us").GetDouble()));
        }

        var rows = new List<(string Name, double Fresh, double Warm)>();

        foreach (var file in Directory.GetFiles(directory, "*.py").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var source = File.ReadAllText(file);
            var fresh = Measure(() => Check(new PythonRunner().Run(source, name + ".py").Succeeded, name));

            using var session = new PythonRunner().StartSession();
            Check(session.Run(source).Succeeded, name);
            var warm = Measure(() => Check(session.Run(source).Succeeded, name));

            rows.Add((name, fresh, warm));
            Console.Error.WriteLine($"{name,-20} fresh {fresh,10:F1} µs   warm {warm,10:F1} µs");
        }

        Console.WriteLine();

        if (cpython is null)
        {
            Console.WriteLine("| workload | sandbox fresh | sandbox warm |");
            Console.WriteLine("|---|--:|--:|");

            foreach (var (name, fresh, warm) in rows)
            {
                Console.WriteLine($"| `{name}` | {Time(fresh)} | {Time(warm)} |");
            }

            return 0;
        }

        Console.WriteLine($"| workload | sandbox fresh | CPython {version} fresh process | sandbox warm | CPython warm | warm ratio |");
        Console.WriteLine("|---|--:|--:|--:|--:|--:|");

        foreach (var (name, fresh, warm) in rows)
        {
            if (!cpython.TryGetValue(name, out var native))
            {
                Console.WriteLine($"| `{name}` | {Time(fresh)} | — | {Time(warm)} | — | — |");
                continue;
            }

            Console.WriteLine(
                $"| `{name}` | {Time(fresh)} | {Time(native.Fresh)} | {Time(warm)} | {Time(native.Warm)} | {(warm / native.Warm).ToString("F1", CultureInfo.InvariantCulture)}× |");
        }

        return 0;
    }

    private static void Check(bool succeeded, string name)
    {
        if (!succeeded)
        {
            throw new InvalidOperationException($"workload {name} failed in the sandbox");
        }
    }

    /// <summary>The median, in microseconds, of runs repeated for about a second.</summary>
    private static double Measure(Action run)
    {
        for (var i = 0; i < 20; i++)
        {
            run();
        }

        var samples = new List<double>();
        var budget = Stopwatch.StartNew();

        while (budget.Elapsed < TimeSpan.FromSeconds(1) || samples.Count < 5)
        {
            var start = Stopwatch.GetTimestamp();
            run();
            samples.Add(Stopwatch.GetElapsedTime(start).TotalMicroseconds);
        }

        samples.Sort();
        return samples[samples.Count / 2];
    }

    private static string Time(double microseconds) =>
        microseconds >= 1000
            ? (microseconds / 1000).ToString("F2", CultureInfo.InvariantCulture) + " ms"
            : microseconds.ToString("F0", CultureInfo.InvariantCulture) + " µs";

    private static string Locate()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "bench", "Computerwelt.Benchmarks", "Workloads");

            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("could not locate bench/Computerwelt.Benchmarks/Workloads");
    }
}
