"""Per-operation timings for numpy and pandas, runnable unchanged under both interpreters.

Prints `operation|milliseconds` (best of N after a warm-up), one line per operation:

  python3 bench/Computerwelt.Benchmarks/numeric_micro.py                    # real numpy/pandas
  OPENBLAS_NUM_THREADS=1 OMP_NUM_THREADS=1 python3 bench/Computerwelt.Benchmarks/numeric_micro.py
  dotnet run -c Release --project src/Computerwelt.Emulation.Python.Cli -- bench/Computerwelt.Benchmarks/numeric_micro.py

`paste -d'|' sandbox.txt cpython.txt` lines the two outputs up.
"""
import numpy as np, pandas as pd, time
def t(label, f, n=100):
    for _ in range(5): f()
    best = 1e9
    for _ in range(n):
        s = time.perf_counter(); f(); best = min(best, time.perf_counter() - s)
    print(f'{label}|{best*1000:.3f}')
a = np.arange(1_000_000, dtype=float)
k = np.arange(1_000_000)
m = (a > 250000.0) & (a < 750000.0)
b = -a / 500000.0
t('a * 2.5', lambda: a * 2.5)
t('a + a', lambda: a + a)
t('np.sqrt(a)', lambda: np.sqrt(a))
t('np.exp(b)', lambda: np.exp(b))
t('np.sin(b)', lambda: np.sin(b))
t('np.log(a + 1.0)', lambda: np.log(a + 1.0))
t('a ** 1.5', lambda: a ** 1.5)
t('a > 5.0', lambda: a > 5.0)
t('(a > x) & (a < y)', lambda: (a > 250000.0) & (a < 750000.0))
t('a[mask]', lambda: a[m])
t('np.where(c, a, -a)', lambda: np.where(m, a, -a))
t('np.count_nonzero(mask)', lambda: np.count_nonzero(m))
t('a % 1000.0', lambda: a % 1000.0)
t('k * 3 + 7 (int64)', lambda: k * 3 + 7)
t('k // 7 (int64)', lambda: k // 7)
t('a.sum()', lambda: a.sum())
t('a.std()', lambda: a.std())
t('a.max()', lambda: a.max())
t('a.argmax()', lambda: a.argmax())
x = a.reshape(1000, 1000)
mu = x.mean(axis=0)
t('x.sum(axis=0)', lambda: x.sum(axis=0))
t('x.mean(axis=1)', lambda: x.mean(axis=1))
t('x - x.mean(axis=0) (row broadcast)', lambda: x - mu)
t('x.T * 2.0 (transpose)', lambda: x.T * 2.0)
t('np.linalg.norm(a)', lambda: np.linalg.norm(a))
rng = np.random.default_rng(1)
for n in (200, 500):
    A = rng.random((n, n)); B = rng.random((n, n))
    t(f'{n}x{n} @ {n}x{n}', lambda: A @ B, 40)
M = rng.random((500, 500)); v = rng.random(500)
t('500x500 @ vector', lambda: M @ v)
S2 = np.arange(14400, dtype=float).reshape(120, 120) % 17.0 + np.eye(120) * 50.0
t('np.linalg.inv 120x120', lambda: np.linalg.inv(S2))
t('np.linalg.solve 120x120', lambda: np.linalg.solve(S2, np.ones(120)))
N = 100_000
df = pd.DataFrame({'k': [i % 20 for i in range(N)], 'x': [i * 0.5 for i in range(N)], 'y': [float(i % 97) for i in range(N)]})
sx, sy = df['x'], df['y']
z = sx * 2.0 + sy
mask = sy > 50.0
t('pd: x * 2.0 (100k)', lambda: sx * 2.0)
t('pd: x + y', lambda: sx + sy)
t('pd: z.sum()', lambda: z.sum())
t('pd: z.std()', lambda: z.std())
t('pd: y > 50.0', lambda: sy > 50.0)
t('pd: df[mask]', lambda: df[mask])
t('pd: groupby(k)[z].mean()', lambda: df.assign(z=z).groupby('k')['z'].mean(), 40)
