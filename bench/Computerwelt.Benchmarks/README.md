# Computerwelt.Benchmarks

The port's performance instrument. It carries bashkit's own benchmark corpus —
`crates/bashkit-bench/src/cases.rs`, all 96 cases in their twelve categories, case for case
— so a number here can be put beside a number from upstream's tables, and adds
micro-benchmarks for the parts that corpus only measures in aggregate.

## Running it

```bash
# The fast loop: every case once through a stopwatch and an allocation counter.
dotnet run -c Release --project bench/Computerwelt.Benchmarks -- --report

# One case, or a family of them.
dotnet run -c Release --project bench/Computerwelt.Benchmarks -- --report --filter fibonacci

# The instrument you publish from. `--job short` trades precision for a run you can wait for.
dotnet run -c Release --project bench/Computerwelt.Benchmarks -- --filter '*SessionBenchmarks*'
dotnet run -c Release --project bench/Computerwelt.Benchmarks -- --filter '*ShellCategory*' --job short

# A loop for an external sampling profiler to watch.
dotnet run -c Release --project bench/Computerwelt.Benchmarks -- --soak --filter fibonacci --seconds 30
```

```bash
# Numbers that do not move with the JIT's tiering decisions, for a before/after.
DOTNET_TieredCompilation=0 dotnet run -c Release --project bench/Computerwelt.Benchmarks -- --report
```

`--report` is the one to reach for while changing code: it prints µs/op and bytes/op per
case, sorted by cost, and it checks every case against the output upstream recorded — a
case that stopped producing the right answer is not a faster case, and the run exits
non-zero when one does. BenchmarkDotNet is the one to reach for before believing a number.

One caution about comparing runs: tiered compilation's second tier keeps objects that do
not escape off the heap, so a case measured half-warm reports allocations the same code
does not make once it is hot. The report warms each case forty times before timing it,
which is enough for most, and `DOTNET_TieredCompilation=0` removes the question entirely
at the cost of a slower, less representative run.

## What is measured

| Class | Rows | What it answers |
|---|---|---|
| `ShellCorpusBenchmarks` | 96 | upstream's corpus, one row per case |
| `ShellCategoryBenchmarks` | 12 | the same corpus by category, for a run that fits in a coffee break |
| `SessionBenchmarks` | 4 | what building a sandbox costs, apart from running a script in it |
| `ParsingBenchmarks` | 5 | lexing and parsing with no interpreter attached |
| `FileSystemBenchmarks` | 7 | the virtual filesystem, and the builtins that walk it |
| `PythonBenchmarks` | 7 | parse, compile and run, and each stage on its own |
| `NotebookBenchmarks` | 7 | classes, dataclasses, enums, `match`, numpy, pandas, and one cell of a live session |
| `--vs-cpython` | 13 | the shared `Workloads/*.py`, fresh and warm, beside CPython — see below |

Every class carries `[MemoryDiagnoser]`, because allocation is the number that moves first:
a sandbox is built per call in the corpus benchmarks, exactly as upstream's in-process
runner builds one per run, so what a session costs to stand up is part of every row.

## Reading the corpus rows

A case is upstream's script, unedited, and its expected output is bash's. The categories
are upstream's too:

| Category | Cases | Category | Cases |
|---|--:|---|--:|
| `startup` | 4 | `tools` | 21 |
| `variables` | 8 | `complex` | 7 |
| `arithmetic` | 6 | `large` | 9 |
| `control` | 9 | `subshell` | 6 |
| `strings` | 8 | `io` | 6 |
| `arrays` | 6 | `pipes` | 6 |

Nothing here is a ratchet: these are measurements, not acceptance criteria, and a machine's
numbers only compare with its own. The suites under `tests/` decide whether the port is
correct; this decides whether it is quick.

## Compared with CPython

`Workloads/` holds thirteen small programs that run unchanged under both interpreters — the
same bytes, so the comparison is of interpreters and not of ports of a benchmark. Each is
timed two ways on each side, because "how fast is it" depends on what you would otherwise
be doing:

| | sandbox | CPython |
|---|---|---|
| **fresh** | a new `PythonRunner.Run` per execution: new machine, nothing imported, parse and compile included | a new `python3 -I file.py` process per execution: what running untrusted code in a subprocess costs |
| **warm** | a cell of a live `PythonSession` whose imports are already done | `exec(compile(source))` in a fresh namespace in a long-lived interpreter: a Jupyter kernel's cell |

```bash
# CPython (needs numpy and pandas installed for those two rows; they are skipped otherwise)
python3 bench/Computerwelt.Benchmarks/cpython_compare.py --out cpython.json

# The sandbox, and the two side by side
dotnet run -c Release --project bench/Computerwelt.Benchmarks -- --vs-cpython cpython.json
```

Both sides take the median of runs repeated for about a second after a warm-up (twenty runs
on the .NET side, which also lets the JIT tier up); CPython's fresh column is the median of
fifteen processes. A stopwatch, not BenchmarkDotNet — good for ratios, not for believing a
microsecond — and the runs below were on a shared 4-vCPU cloud container (Xeon @ 2.1 GHz),
where repeating a run moves a row by 10–30%.

### Results  (CPython 3.13.18, numpy 2.5.4, pandas 3.1.1; .NET 10, Release)

| workload | sandbox fresh | CPython fresh process | sandbox warm | CPython warm | warm ratio |
|---|--:|--:|--:|--:|--:|
| `hello` (`x = 1`) | 10 µs | 11.12 ms | 2 µs | 5 µs | 0.3× |
| `calls` (2,000 calls) | 1.16 ms | 13.21 ms | 850 µs | 159 µs | 5.4× |
| `fib` (`fib(18)`, recursive) | 4.50 ms | 11.82 ms | 3.40 ms | 275 µs | 12.4× |
| `numeric_loop` (5,000 iterations) | 1.84 ms | 12.27 ms | 2.06 ms | 340 µs | 6.1× |
| `classes` (inheritance, `super`, a property) | 2.35 ms | 13.95 ms | 2.46 ms | 369 µs | 6.7× |
| `match_patterns` | 1.15 ms | 11.66 ms | 1.06 ms | 186 µs | 5.7× |
| `text` (f-strings, split, dict counting) | 902 µs | 12.28 ms | 974 µs | 224 µs | 4.3× |
| `regex_scan` | 1.81 ms | 19.28 ms | 1.27 ms | 393 µs | 3.2× |
| `json_roundtrip` | 863 µs | 20.74 ms | 866 µs | 375 µs | 2.3× |
| `enums` | 1.40 ms | 17.16 ms | 1.47 ms | 706 µs | 2.1× |
| `dataclasses_sort` | 1.66 ms | 34.77 ms | 1.61 ms | 1.21 ms | 1.3× |
| `numpy_array` (100×100, ufuncs, mask, matmul) | 2.29 ms | 89.72 ms | 2.32 ms | 175 µs | 13.2× |
| `pandas_groupby` (2,000 rows) | 1.90 ms | 386.10 ms | 1.89 ms | 2.28 ms | 0.8× |

### Reading it

- **Started fresh, the sandbox wins by one to two orders of magnitude**, because there is no
  process: CPython spends about 11 ms starting before it runs a line, and importing numpy
  (90 ms) or pandas (386 ms) dwarfs every workload here. That is the comparison that matters
  when the alternative to an in-process sandbox is a subprocess per untrusted program.
- **Warm, pure-Python bytecode runs 2–12× slower than CPython 3.13.** The gap is widest
  where the work is the interpreter loop itself — calls (`fib`, `calls`), arithmetic on
  boxed integers (`numeric_loop`) — and narrows to 1.3–2.3× where most of the time is in
  library code written in C on CPython's side and in C# on ours (`json`, `enum`,
  `dataclasses`). CPython 3.13 has an adaptive specializing interpreter and decades of
  tuning; this VM is a straightforward one, and it is the thing to make faster next.
- **numpy is 13× slower warm**: CPython's numpy is vectorised C, this one is scalar managed
  loops over typed arrays, with per-call overhead that dominates at this size. **pandas is a
  little faster than real pandas** on a small frame, because real pandas' per-call overhead
  (index alignment, dtype dispatch, block management) is large next to 2,000 rows; on large
  frames real pandas' vectorised kernels would win.
- `hello` shows the floor: a cell or a run that does nothing costs microseconds, which is
  what makes a sandbox per request affordable.

None of this is a ratchet. Re-run both commands when the interpreter changes, and update the
table with the numbers you measured.
