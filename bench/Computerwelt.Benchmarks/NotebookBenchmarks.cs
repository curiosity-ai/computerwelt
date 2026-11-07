using BenchmarkDotNet.Attributes;
using Computerwelt.Emulation.Python;

namespace Computerwelt.Benchmarks;

/// <summary>
/// What a notebook runs: classes and their methods, dataclasses, enums, and the numpy and
/// pandas subsets — each a whole run, so module construction is part of the cost.
/// </summary>
[MemoryDiagnoser]
public class NotebookBenchmarks
{
    private const string Methods = """
        class Base:
            def __init__(self, x):
                self.x = x
            def get(self):
                return self.x
        class Point(Base):
            def __init__(self, x, y):
                super().__init__(x)
                self.y = y
            @property
            def total(self):
                return self.get() + self.y
        points = [Point(i, i) for i in range(500)]
        s = 0
        for _ in range(4):
            for p in points:
                s += p.total
        """;

    private const string Dataclasses = """
        from dataclasses import dataclass, field
        @dataclass(order=True)
        class Item:
            name: str
            qty: int = 0
            tags: list = field(default_factory=list)
        items = sorted(Item(f'n{i % 50}', i) for i in range(500))
        same = sum(1 for a, b in zip(items, items[1:]) if a == b)
        """;

    private const string Enums = """
        from enum import Enum, auto
        class Color(Enum):
            RED = auto()
            GREEN = auto()
            BLUE = auto()
        counts = {}
        for i in range(1500):
            c = Color((i % 3) + 1)
            counts[c] = counts.get(c, 0) + 1
        """;

    private const string Numpy = """
        import numpy as np
        a = np.arange(10000, dtype=float).reshape(100, 100)
        b = (a * 2.0 + 1.0) / 3.0
        m = b.mean(axis=0).sum() + (b[b > 50].std())
        c = a @ a.T
        """;

    private const string Pandas = """
        import pandas as pd
        df = pd.DataFrame({'k': [i % 10 for i in range(2000)], 'v': list(range(2000))})
        g = df.groupby('k')['v'].agg(['sum', 'mean'])
        f = df[df.v > 100].sort_values('v', ascending=False).head(20)
        text = repr(g)
        """;

    private const string Match = """
        def kind(v):
            match v:
                case int(n) if n < 0: return 'neg'
                case [x, y]: return 'pair'
                case {'k': k}: return 'map'
                case _: return 'other'
        values = [-1, [1, 2], {'k': 1}, 'x'] * 250
        out = [kind(v) for v in values]
        """;

    private PythonSession? _session;

    [GlobalSetup(Target = nameof(SessionCell))]
    public void StartSession()
    {
        _session = new PythonRunner().StartSession();
        _session.Run("data = list(range(200))");
    }

    [GlobalCleanup(Target = nameof(SessionCell))]
    public void EndSession() => _session?.Dispose();

    [Benchmark]
    public bool RunMethods() => new PythonRunner().Run(Methods).Succeeded;

    [Benchmark]
    public bool RunDataclasses() => new PythonRunner().Run(Dataclasses).Succeeded;

    [Benchmark]
    public bool RunEnums() => new PythonRunner().Run(Enums).Succeeded;

    [Benchmark]
    public bool RunMatch() => new PythonRunner().Run(Match).Succeeded;

    [Benchmark]
    public bool RunNumpy() => new PythonRunner().Run(Numpy).Succeeded;

    [Benchmark]
    public bool RunPandas() => new PythonRunner().Run(Pandas).Succeeded;

    /// <summary>One cell of a live session: the per-cell overhead plus a small computation.</summary>
    [Benchmark]
    public bool SessionCell() => _session!.Run("sum(x * x for x in data)").Succeeded;
}
