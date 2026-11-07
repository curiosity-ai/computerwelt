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

`Workloads/` holds twenty small programs that run unchanged under both interpreters — the
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

CPython's numpy uses OpenBLAS, which spreads a matrix product over every core unless told
otherwise; the sandbox is single-threaded by design. The table below is the default
(multi-threaded BLAS); to compare one core with one core, pin it:

```bash
OPENBLAS_NUM_THREADS=1 OMP_NUM_THREADS=1 MKL_NUM_THREADS=1 python3 bench/Computerwelt.Benchmarks/cpython_compare.py --out cpython-1t.json
```

Only the BLAS-backed rows move: numpy's `np_matmul` goes from 459 µs to 867 µs and
`np_linalg` from 706 to 757 µs; element-wise ufuncs and reductions are single-threaded in
numpy either way. Against one BLAS thread the sandbox's `np_matmul` is 1.6×, against all
four 3.0×.

Both sides take the median of runs repeated for about a second after a warm-up (twenty runs
on the .NET side, which also lets the JIT tier up); CPython's fresh column is the median of
fifteen processes. A stopwatch, not BenchmarkDotNet — good for ratios, not for believing a
microsecond — and the runs below were on a shared 4-vCPU cloud container (Xeon @ 2.1 GHz),
where repeating a run moves a row by 10–30%.

### Results  (CPython 3.13.18, numpy 2.5.4, pandas 3.1.1; .NET 10, Release)

| workload | sandbox fresh | CPython fresh process | sandbox warm | CPython warm | warm ratio |
|---|--:|--:|--:|--:|--:|
| `hello` (`x = 1`) | 10 µs | 11.38 ms | 2 µs | 4 µs | 0.4× |
| `calls` (2,000 calls) | 1.20 ms | 12.35 ms | 1.26 ms | 160 µs | 7.9× |
| `fib` (`fib(18)`, recursive) | 2.64 ms | 11.78 ms | 2.48 ms | 288 µs | 8.6× |
| `numeric_loop` (5,000 iterations) | 1.82 ms | 12.46 ms | 1.95 ms | 334 µs | 5.8× |
| `classes` (inheritance, `super`, a property) | 2.23 ms | 12.96 ms | 1.98 ms | 370 µs | 5.4× |
| `match_patterns` | 951 µs | 12.24 ms | 958 µs | 185 µs | 5.2× |
| `text` (f-strings, split, dict counting) | 858 µs | 15.35 ms | 944 µs | 276 µs | 3.4× |
| `regex_scan` | 1.76 ms | 26.22 ms | 1.17 ms | 490 µs | 2.4× |
| `json_roundtrip` | 920 µs | 20.44 ms | 928 µs | 468 µs | 2.0× |
| `enums` | 1.50 ms | 16.68 ms | 1.55 ms | 578 µs | 2.7× |
| `dataclasses_sort` | 1.61 ms | 34.45 ms | 1.56 ms | 1.23 ms | 1.3× |
| `numpy_array` (100×100, ufuncs, mask, matmul) | 458 µs | 85.04 ms | 309 µs | 170 µs | 1.8× |
| `np_elementwise` (1M floats, chained ufuncs) | 51.18 ms | 112.12 ms | 34.72 ms | 24.07 ms | 1.4× |
| `np_int_math` (1M int64) | 10.17 ms | 98.90 ms | 9.53 ms | 6.40 ms | 1.5× |
| `np_masks` (1M, compare, `&`, `a[m]`, `where`) | 34.37 ms | 108.90 ms | 37.45 ms | 19.96 ms | 1.9× |
| `np_reductions` (1M, whole and per-axis) | 19.39 ms | 96.37 ms | 18.25 ms | 9.49 ms | 1.9× |
| `np_matmul` (200×200 products) | 1.50 ms | 84.62 ms | 1.37 ms | 459 µs | 3.0× |
| `np_linalg` (`inv`, `solve`, `det` at 120×120) | 1.53 ms | 85.18 ms | 1.18 ms | 706 µs | 1.7× |
| `pandas_groupby` (2,000 rows) | 1.14 ms | 308.95 ms | 945 µs | 2.37 ms | 0.4× |
| `pd_numeric` (100,000 rows) | 106.91 ms | 458.93 ms | 108.59 ms | 30.63 ms | 3.5× |

### Reading it

- **Started fresh, the sandbox wins by one to two orders of magnitude**, because there is no
  process: CPython spends about 11 ms starting before it runs a line, and importing numpy
  (85 ms) or pandas (309 ms) dwarfs every workload here. That is the comparison that matters
  when the alternative to an in-process sandbox is a subprocess per untrusted program.
- **Warm, pure-Python bytecode runs 1.3–9× slower than CPython 3.13.** The gap is widest
  where the work is the interpreter loop itself — calls (`fib`, `calls`), arithmetic on
  boxed integers (`numeric_loop`) — and narrows where most of the time is in library code
  written in C on CPython's side and in C# on ours (`json`, `enum`, `dataclasses`). CPython
  3.13 has an adaptive specializing interpreter and decades of tuning; this VM is a
  straightforward one.
- **numpy on a million elements is 1.4–1.9× behind real numpy**, both running vectorised
  kernels; what is left is mostly allocation (every intermediate is a fresh 8 MB array the
  GC has to clear and collect) and the first calls of each kernel, below. **Matrix products
  are 3.0× behind a multi-threaded BLAS and 1.6× behind a single-threaded one**; the kernel
  alone reaches about 50 GFLOPS on one core here against OpenBLAS' 55–58, and the rest is
  the operations around it. This sandbox will not spread one tenant's program over the
  host's cores.
- **pandas is faster than real pandas on a small frame** (per-call overhead dominates at
  2,000 rows) and 3.5× behind on 100,000 rows — where most of the sandbox's time is the
  workload building its three columns as Python list comprehensions, which is the
  interpreter, not pandas.
- `hello` shows the floor: a cell or a run that does nothing costs microseconds, which is
  what makes a sandbox per request affordable.

## The numeric kernels

The numpy and pandas halves were rewritten for speed in one round; the warm ratios against
the real packages, on the workloads above:

| workload | before | after |
|---|--:|--:|
| `np_elementwise` | 7.3× | 1.4× |
| `np_int_math` | 6.8× | 1.5× |
| `np_masks` | 5.1× | 1.9× |
| `np_reductions` | 8.1× | 1.9× |
| `np_matmul` | 31.9× | 3.0× (1.6× against one BLAS thread) |
| `np_linalg` | 6.9× | 1.7× |
| `numpy_array` | 13.2× | 1.8× |
| `pd_numeric` | 4.1× | 3.5× |

What does the work:

- **Element-wise ufuncs** on contiguous operands of 64 elements or more (or one operand a
  scalar) go to `System.Numerics.Tensors.TensorPrimitives` — AVX2/AVX-512 on x64, AdvSimd
  on Arm — and comparisons to a `Vector256` loop that writes four bools per store
  (`Modules/Numpy/NdSimd.cs`). `//`, `%` and `fmod` are typed loops over the exact scalar
  rule; int64 wraps as numpy's does.
- **Masks** — `a[m]`, `a[m] = v`, `np.where`, `count_nonzero` — read the mask eight bytes
  at a time, skipping clear words and copying full ones (`NdSimd.Masks.cs`).
- **Reductions** over the whole array, the leading axes (row-wise vector adds, as numpy does)
  or the trailing axes (each output one contiguous run) read the buffer in place
  (`Reductions.Simd.cs`).
- **Matrix products** above 2^14 multiply-adds use a packed GEMM (`LinAlg.Gemm.cs`): an
  8×16 block of the result in sixteen `Vector512` FMA accumulators where AVX-512 is
  accelerated, a 4×8 block in eight `Vector256` ones elsewhere. The inner loops are kept free
  of calls: with one after the loop, the JIT kept the 512-bit accumulators on the stack and
  reloaded them every iteration, which ran at 20 GFLOPS instead of 50. `inv` and `solve`
  update whole rows.
- **Strided and broadcast operands** — a row or a column spread over a matrix
  (`x - x.mean(axis=0)`), a transpose, a stepped slice — no longer fall to the per-element
  loop: a row or column is applied row by row without a copy, a transposed array is
  processed in memory order and its result keeps that layout (numpy's default order does
  the same), and anything else is gathered once.
- **pandas columns** hold `double[]`/`long[]`/`bool[]` and box only when something reads
  them one object at a time, so column arithmetic, comparison, masks, reductions and
  single-key groupby never touch a `PyObject` (`Modules/Pandas/PdVector.cs`).

### Accuracy

Small arrays — under 64 elements, and products under 2^14 multiply-adds — take the exact
paths they always took, so the small results a program prints are unchanged. Above that:

| operation | difference from the scalar result |
|---|---|
| `+ - * /`, comparisons, `sqrt`, `abs`, `floor`, min/max, masks, `where`, int64 anything | none: single IEEE operations either way |
| `exp`, `log`, `sin`, `tanh`, `arctan2`, … | the vectorised implementations' few ulps (fixture: within 1e-12 relative) |
| `sum`, `mean`, `var`, `std` | a different association than numpy's eight-way pairwise blocks: last digits |
| matrix products | sums in FMA over 128- or 256-deep blocks, as BLAS does: last digits; integer-valued data exact |
| pandas reductions | none: numpy's pairwise sum, now as two vector accumulators, bit for bit |
| `x ** y` | none: `TensorPrimitives.Pow` is exact. `exp(y·log x)` was 1.8× faster and rejected, because it printed `4.0 ** 1.5` as `7.999999999999998` |

`tests/monty-extensions/numpy__vectorised_paths.py` and `pandas__unboxed_columns.py` check
every path at sizes that take it against a pure-Python computation, and pass under CPython
with the real packages too.

### The first calls of a kernel

.NET compiles a method at a low optimisation tier first and recompiles it once it is hot;
ReadyToRun cannot precompile `TensorPrimitives`' AVX paths, so its methods also start at that
tier. In a busy interpreter promotion can take a while: a 1M-element `sum` timed 40 times
took 1.5 ms best, 200 times 0.29 ms. The port's own kernels carry
`MethodImplOptions.AggressiveOptimization` and are full speed from their first call (the
pandas pairwise sum ran 80× slower without it). For `TensorPrimitives` the choice is the
host's: `<TieredCompilationQuickJitForLoops>false</TieredCompilationQuickJitForLoops>` (or
`DOTNET_TC_QuickJitForLoops=0`) makes every method with a loop compile optimised at once,
which brought the 40-run `sum` to 0.32 ms and cost 60–150 ms of process start-up here — worth
it for a long-lived notebook host doing numeric work, not for a process per short script.
The CLIs and the benchmarks keep the runtime's default.

None of this is a ratchet. Re-run both commands when the interpreter changes, and update the
table with the numbers you measured.
