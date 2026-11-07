"""Times the workloads in Workloads/ under CPython, for comparison with the sandbox.

Two numbers per workload, matching the two the sandbox reports (`--vs-cpython`):

  fresh  a new `python3 -I <file>` process per run: interpreter start-up, imports and
         the program — what running untrusted code in a CPython subprocess costs.
  warm   `exec(compile(source))` in a fresh namespace inside one long-lived interpreter,
         modules already imported — what a notebook cell costs in a Jupyter kernel.

Usage:  python3 cpython_compare.py [--out results.json] [--fresh-runs N]
numpy/pandas workloads are skipped when those packages are not importable.
"""

import json
import os
import statistics
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
WORKLOADS = os.path.join(HERE, 'Workloads')


def warm(source, name, budget=1.0):
    code = compile(source, name, 'exec')
    exec(code, {'__name__': '__main__'})  # warm-up: imports, caches
    samples = []
    deadline = time.perf_counter() + budget
    while time.perf_counter() < deadline or len(samples) < 5:
        start = time.perf_counter()
        exec(compile(source, name, 'exec'), {'__name__': '__main__'})
        samples.append(time.perf_counter() - start)
    return statistics.median(samples) * 1e6


def fresh(path, runs):
    subprocess.run([sys.executable, '-I', path], check=True, capture_output=True)
    samples = []
    for _ in range(runs):
        start = time.perf_counter()
        subprocess.run([sys.executable, '-I', path], check=True, capture_output=True)
        samples.append(time.perf_counter() - start)
    return statistics.median(samples) * 1e6


def main():
    out = None
    runs = 15
    args = sys.argv[1:]
    if '--out' in args:
        out = args[args.index('--out') + 1]
    if '--fresh-runs' in args:
        runs = int(args[args.index('--fresh-runs') + 1])

    results = {'python': sys.version.split()[0], 'workloads': {}}
    for file in sorted(os.listdir(WORKLOADS)):
        if not file.endswith('.py'):
            continue
        path = os.path.join(WORKLOADS, file)
        source = open(path).read()
        name = file[:-3]
        try:
            w = warm(source, file)
        except ImportError as error:
            print(f'{name:20} skipped ({error})')
            continue
        f = fresh(path, runs)
        results['workloads'][name] = {'fresh_us': f, 'warm_us': w}
        print(f'{name:20} fresh {f / 1000:9.2f} ms   warm {w:10.1f} us')

    if out:
        with open(out, 'w') as handle:
            json.dump(results, handle, indent=2)


if __name__ == '__main__':
    main()
