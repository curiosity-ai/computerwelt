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

# Masks: selection, assignment through a mask, where and counting.
w = np.where(a > 0.0, a, -a)
assert w.tolist() == [x if x > 0.0 else -x for x in xs]
assert np.where(k % 2 == 0, k, 0).tolist() == [i if i % 2 == 0 else 0 for i in range(N)]
assert int(np.count_nonzero(mask)) == sum(1 for x in xs if -50.0 < x < 50.0)
assert int(np.count_nonzero(k)) == N - 1
assert int(mask.sum()) == int(np.count_nonzero(mask))
assert k[k % 3 == 0].tolist() == list(range(0, N, 3))
d = a.copy()
d[d < 0.0] = 0.0
assert d.tolist() == [max(x, 0.0) for x in xs]
e = k.copy()
e[e > 900] = -1
assert e.tolist() == [i if i <= 900 else -1 for i in range(N)]
assert m[m > 0.0].tolist() == [x for x in xs if x > 0.0]

# Floor division and remainder keep Python's sign rules on the typed loops.
assert (a % 7.0).tolist() == [x % 7.0 for x in xs]
assert (a // 7.0).tolist() == [x // 7.0 for x in xs]
assert ((k - 500) % 7).tolist() == [(i - 500) % 7 for i in range(N)]
assert ((k - 500) // -7).tolist() == [(i - 500) // -7 for i in range(N)]
assert (k / 4).tolist() == [i / 4 for i in range(N)]
assert (k * -3).tolist() == [i * -3 for i in range(N)]

# A non-special exponent goes through exp and log: within a few ulps, and pow's own
# answers at zero, negatives and infinities.
all_close(b ** 1.5, [y ** 1.5 for y in ys], rel=1e-13)
edge = np.array([0.0, -2.0, float('inf'), 4.0] * 20) ** 1.5
assert edge[0] == 0.0 and math.isnan(edge[1]) and edge[2] == float('inf') and edge[3] == 8.0

# A product big enough for the blocked kernel, with edges that are not whole blocks.
p2 = np.array([[((i * 13 + j * 5) % 9) - 4.0 for j in range(300)] for i in range(37)])
q2 = np.array([[((i * 3 + j * 11) % 7) - 3.0 for j in range(29)] for i in range(300)])
pl2, ql2 = p2.tolist(), q2.tolist()
assert (p2 @ q2).tolist() == [[math.fsum(pl2[i][r] * ql2[r][j] for r in range(300)) for j in range(29)] for i in range(37)]
ip, iq = p2.astype(np.int64), q2.astype(np.int64)
assert (ip @ iq).tolist() == [[sum(int(pl2[i][r]) * int(ql2[r][j]) for r in range(300)) for j in range(29)] for i in range(37)]
col = q2[:, :1].copy()
assert (p2 @ col).tolist() == [[math.fsum(pl2[i][r] * ql2[r][0] for r in range(300))] for i in range(37)]

# LU-based solves agree with the matrix they came from.
sq = np.array([[((i * 7 + j * 3) % 10) + (40.0 if i == j else 0.0) for j in range(70)] for i in range(70)])
inv = np.linalg.inv(sq)
assert float(np.abs(inv @ sq - np.eye(70)).max()) < 1e-12
sol = np.linalg.solve(sq, np.arange(70.0))
assert float(np.abs(sq @ sol - np.arange(70.0)).max()) < 1e-10

# Trailing-axis reductions read each output's run in place.
all_close(m.mean(axis=1), [math.fsum(row) / 20 for row in rows], rel=1e-12)
row_means = [math.fsum(row) / 20 for row in rows]
all_close(m.std(axis=1), [math.sqrt(math.fsum((v - row_means[r]) ** 2 for v in row) / 20) for r, row in enumerate(rows)], rel=1e-11)
assert m.max(axis=1).tolist() == [max(row) for row in rows] and m.min(axis=-1).tolist() == [min(row) for row in rows]
cube = a.reshape(10, 5, 20)
all_close(cube.sum(axis=(1, 2)), [math.fsum(xs[i * 100:(i + 1) * 100]) for i in range(10)], rel=1e-12)
assert cube.sum(axis=2, keepdims=True).shape == (10, 5, 1)
assert close(float(np.linalg.norm(k)), math.sqrt(sum(i * i for i in range(N))), rel=1e-12)
