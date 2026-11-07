# Extension: pandas over unboxed numeric columns. Columns large enough to take the
# vectorised operators, reductions, masks and typed grouping, each checked against a
# pure-Python computation, plus the writes that must leave the typed copy behind.
# The same file passes under CPython with real pandas.
import math
import pandas as pd

N = 600
ks = [i % 7 for i in range(N)]
xs = [i * 0.25 - 40.0 for i in range(N)]
ys = [float((i * 31) % 53) for i in range(N)]
df = pd.DataFrame({'k': ks, 'x': xs, 'y': ys})
x, y, k = df['x'], df['y'], df['k']

# Arithmetic: exact, with a scalar on either side, int and float, int wrapping to float on /.
assert (x + y).tolist() == [a + b for a, b in zip(xs, ys)]
assert (x * 2.5 - y).tolist() == [a * 2.5 - b for a, b in zip(xs, ys)]
assert (3.0 / (y + 1.0)).tolist() == [3.0 / (b + 1.0) for b in ys]
assert (k * 3 + 1).tolist() == [i * 3 + 1 for i in ks]
assert (k - k * k).tolist() == [i - i * i for i in ks]
assert (k / 2).tolist() == [i / 2 for i in ks]
assert (k + x).tolist() == [i + a for i, a in zip(ks, xs)]
assert str((k * 3).dtype) == 'int64' and str((k / 2).dtype) == 'float64'

# Comparisons and masks.
assert (x > 0.0).tolist() == [a > 0.0 for a in xs]
assert (k == 3).tolist() == [i == 3 for i in ks]
assert (x <= y).tolist() == [a <= b for a, b in zip(xs, ys)]
both = (x > -10.0) & (y < 20.0)
assert both.tolist() == [a > -10.0 and b < 20.0 for a, b in zip(xs, ys)]
assert ((k == 1) | (k == 2)).sum() == sum(1 for i in ks if i in (1, 2))
picked = df[both]
assert picked['x'].tolist() == [a for a, b in zip(xs, ys) if a > -10.0 and b < 20.0]
assert picked.index.tolist() == [i for i, (a, b) in enumerate(zip(xs, ys)) if a > -10.0 and b < 20.0]

# Reductions: the pairwise sums pandas uses, and int sums that stay ints.
assert math.isclose(x.sum(), math.fsum(xs), rel_tol=1e-15)
assert math.isclose(y.mean(), math.fsum(ys) / N, rel_tol=1e-15)
mean = math.fsum(ys) / N
assert math.isclose(y.std(), math.sqrt(math.fsum((b - mean) ** 2 for b in ys) / (N - 1)), rel_tol=1e-13)
assert math.isclose(y.var(ddof=0), math.fsum((b - mean) ** 2 for b in ys) / N, rel_tol=1e-13)
assert x.max() == max(xs) and x.min() == min(xs)
assert k.sum() == sum(ks) and k.max() == 6 and k.min() == 0
assert math.isclose(k.mean(), sum(ks) / N, rel_tol=1e-15)

# A missing value is skipped by the reductions and compares false.
z = x.copy()
z.iloc[10] = float('nan')
assert math.isclose(z.sum(), math.fsum(a for i, a in enumerate(xs) if i != 10), rel_tol=1e-15)
assert z.count() == N - 1
assert not (z > -1000.0).iloc[10] and (z != 0.0).iloc[10]
assert z.max() == max(xs)

# Writing into a typed column, then reading it both ways.
w = df['y'].copy()
w.iloc[0] = 99.5
w.iloc[1] = 7
assert w.iloc[0] == 99.5 and w.iloc[1] == 7.0 and w.sum() == math.fsum([99.5, 7.0] + ys[2:])
assert (w * 2.0).tolist()[:2] == [199.0, 14.0]
v = k.copy()
v.iloc[0] = 100
assert v.sum() == sum(ks) + 100 and (v > 50).sum() == 1

# Grouping on an int key and on a float key, with NaN dropped.
g = df.groupby('k')['x'].mean()
assert g.index.tolist() == list(range(7))
for key in range(7):
    members = [a for i, a in zip(ks, xs) if i == key]
    assert math.isclose(g[key], math.fsum(members) / len(members), rel_tol=1e-14)
assert df.groupby('k')['y'].sum().tolist() == [math.fsum(b for i, b in zip(ks, ys) if i == key) for key in range(7)]
fk = pd.DataFrame({'f': [float(i % 3) if i % 10 else float('nan') for i in range(N)], 'v': list(range(N))})
counts = fk.groupby('f')['v'].count()
assert counts.index.tolist() == [0.0, 1.0, 2.0]
assert counts.tolist() == [sum(1 for i in range(N) if i % 10 and i % 3 == c) for c in range(3)]
zeros = pd.DataFrame({'f': [0.0, -0.0] * 40, 'v': [1] * 80}).groupby('f')['v'].sum()
assert zeros.tolist() == [80]

# A default index survives selection and arithmetic as labels.
assert (x + y)[picked.index].tolist() == [a + b for a, b in zip(picked['x'].tolist(), picked['y'].tolist())]
