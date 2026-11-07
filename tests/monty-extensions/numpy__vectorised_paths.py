# Extension: numpy's vectorised (SIMD) paths. Arrays here are large enough (>= 64 elements)
# to take the TensorPrimitives / Vector256 loops, and every result is checked against a
# pure-Python computation of the same thing — exact where the operation is exact in IEEE
# arithmetic, within a relative tolerance where it is transcendental or a reordered sum.
# The same file passes under CPython with real numpy.
import math
import numpy as np

N = 1000
xs = [i * 0.37 - 150.0 for i in range(N)]
ys = [((i * 7919) % 211) * 0.5 + 1.0 for i in range(N)]
a = np.array(xs)
b = np.array(ys)


def close(u, v, rel=1e-12, abs_=1e-12):
    return math.isclose(u, v, rel_tol=rel, abs_tol=abs_) or (math.isnan(u) and math.isnan(v))


def all_close(arr, expected, rel=1e-12, abs_=1e-12):
    got = arr.tolist()
    assert len(got) == len(expected)
    for i, (g, e) in enumerate(zip(got, expected)):
        assert close(g, e, rel, abs_), (i, g, e)


# Exact arithmetic, array-array and with a scalar on either side.
assert (a + b).tolist() == [x + y for x, y in zip(xs, ys)]
assert (a - b).tolist() == [x - y for x, y in zip(xs, ys)]
assert (a * b).tolist() == [x * y for x, y in zip(xs, ys)]
assert (a / b).tolist() == [x / y for x, y in zip(xs, ys)]
assert (a * 2.5).tolist() == [x * 2.5 for x in xs]
assert (3.0 - a).tolist() == [3.0 - x for x in xs]
assert (1.0 / b).tolist() == [1.0 / y for y in ys]
assert (a ** 2).tolist() == [x * x for x in xs]
assert np.maximum(a, 0.0).tolist() == [max(x, 0.0) for x in xs]
assert np.minimum(a, b).tolist() == [min(x, y) for x, y in zip(xs, ys)]
assert np.sqrt(b).tolist() == [math.sqrt(y) for y in ys]
assert np.abs(a).tolist() == [abs(x) for x in xs]
assert (-a).tolist() == [-x for x in xs]
assert np.floor(a).tolist() == [float(math.floor(x)) for x in xs]

# Transcendentals: vectorised implementations, so within a few ulps.
all_close(np.exp(a / 100.0), [math.exp(x / 100.0) for x in xs], rel=1e-13)
all_close(np.sin(a), [math.sin(x) for x in xs], rel=1e-12, abs_=1e-14)
all_close(np.cos(a), [math.cos(x) for x in xs], rel=1e-12, abs_=1e-14)
all_close(np.log(b), [math.log(y) for y in ys], rel=1e-13)
all_close(np.tanh(a / 50.0), [math.tanh(x / 50.0) for x in xs], rel=1e-13)
all_close(np.arctan2(a, b), [math.atan2(x, y) for x, y in zip(xs, ys)], rel=1e-13)
all_close(b ** 0.5, [y ** 0.5 for y in ys], rel=1e-14)

# NaN propagates through maximum and min/max reductions; fmax and nanmax skip it.
c = a.copy()
c[500] = float('nan')
assert math.isnan(np.maximum(c, 0.0)[500]) and np.fmax(c, 0.0)[500] == 0.0
assert math.isnan(c.max()) and math.isnan(c.min())
assert np.nanmax(c) == max(x for i, x in enumerate(xs) if i != 500)
assert int(np.argmax(c)) == 500 and int(np.argmin(c)) == 500

# Comparisons, masks and their combination.
assert (a > 0.0).tolist() == [x > 0.0 for x in xs]
assert (a <= b).tolist() == [x <= y for x, y in zip(xs, ys)]
assert (a == a).all() and not (c == c).all() and (c != c).sum() == 1
mask = (a > -50.0) & (a < 50.0)
assert mask.tolist() == [-50.0 < x < 50.0 for x in xs]
assert ((a < -100.0) | (a > 100.0)).sum() == sum(1 for x in xs if x < -100.0 or x > 100.0)
assert a[mask].tolist() == [x for x in xs if -50.0 < x < 50.0]

# Reductions: whole array, leading axis and trailing axis.
assert close(a.sum(), math.fsum(xs), rel=1e-12)
assert close(a.mean(), math.fsum(xs) / N, rel=1e-12)
mean = math.fsum(xs) / N
assert close(a.var(), math.fsum((x - mean) ** 2 for x in xs) / N, rel=1e-12)
assert close(a.std(ddof=1), math.sqrt(math.fsum((x - mean) ** 2 for x in xs) / (N - 1)), rel=1e-12)
assert a.max() == max(xs) and a.min() == min(xs)
assert int(a.argmax()) == xs.index(max(xs)) and int(b.argmin()) == ys.index(min(ys))
m = a.reshape(50, 20)
rows = [xs[r * 20:(r + 1) * 20] for r in range(50)]
all_close(m.sum(axis=0), [math.fsum(row[c] for row in rows) for c in range(20)], rel=1e-12)
all_close(m.sum(axis=1), [math.fsum(row) for row in rows], rel=1e-12)
all_close(m.mean(axis=0), [math.fsum(row[c] for row in rows) / 50 for c in range(20)], rel=1e-12)
col_means = [math.fsum(row[c] for row in rows) / 50 for c in range(20)]
all_close(m.std(axis=0), [math.sqrt(math.fsum((row[c] - col_means[c]) ** 2 for row in rows) / 50) for c in range(20)], rel=1e-11)
assert m.max(axis=0).tolist() == [max(row[c] for row in rows) for c in range(20)]

# Integers: SIMD add/multiply wrap exactly as int64 does.
k = np.arange(N, dtype=np.int64)
assert (k * 3 + 7).tolist() == [i * 3 + 7 for i in range(N)]
assert int((k * k).sum()) == sum(i * i for i in range(N))
assert int(k.max()) == N - 1 and int(np.argmax(k)) == N - 1
big = np.full(N, 2**62, dtype=np.int64)
assert int((big + big)[0]) == -(2**63)
assert (k > 500).sum() == N - 501
assert (k // 7).tolist() == [i // 7 for i in range(N)]

# Mixed int and float promote to float.
assert (k + 0.5).tolist() == [i + 0.5 for i in range(N)]

# A strided view takes the general path and must agree.
s = a[::3]
assert (s * 2.0).tolist() == [x * 2.0 for x in xs[::3]]
assert close(s.sum(), math.fsum(xs[::3]), rel=1e-12)
t = m.T
assert (t + 1.0).tolist() == [[row[c] + 1.0 for row in rows] for c in range(20)]

# Matrix products against a pure-Python product.
p = np.array([[((i * 31 + j * 17) % 13) - 6.0 for j in range(70)] for i in range(80)])
q = np.array([[((i * 7 + j * 3) % 11) - 5.0 for j in range(90)] for i in range(70)])
pl, ql = p.tolist(), q.tolist()
expected = [[math.fsum(pl[i][r] * ql[r][j] for r in range(70)) for j in range(90)] for i in range(80)]
assert (p @ q).tolist() == expected
v = np.array(xs[:70])
assert close(float(v @ v), math.fsum(x * x for x in xs[:70]), rel=1e-12)
assert close(float(np.linalg.norm(a)), math.sqrt(math.fsum(x * x for x in xs)), rel=1e-12)
