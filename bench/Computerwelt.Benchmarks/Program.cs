using System.Reflection;
using BenchmarkDotNet.Running;
using Computerwelt.Benchmarks;

// Four ways in. `--vs-cpython` times the shared workloads in `Workloads/` for comparison
// with `cpython_compare.py`. `--report` is the fast one: it runs the corpus once through a stopwatch and
// an allocation counter and prints a table, which is what you want while changing code.
// `--soak` runs cases in a loop for a while, which is what an external sampling profiler
// needs to see anything at all. Anything else goes to BenchmarkDotNet, which is what you
// want before believing a number.
if (args.Length > 0 && args[0] == "--report")
{
    return await CorpusReport.RunAsync(args.AsSpan(1).ToArray());
}

if (args.Length > 0 && args[0] == "--vs-cpython")
{
    return CPythonComparison.Run(args.AsSpan(1).ToArray());
}

if (args.Length > 0 && args[0] == "--soak")
{
    return await CorpusReport.SoakAsync(args.AsSpan(1).ToArray());
}

BenchmarkSwitcher.FromAssembly(Assembly.GetExecutingAssembly()).Run(args);
return 0;
