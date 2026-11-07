# Extension: `numpy.random`. Upstream Monty has no numpy. The legacy global generator
# (`np.random.seed`, `rand`, `randn`, `randint`, `permutation` …) is a Mersenne Twister
# seeded and drawn from as numpy's RandomState does, so the vectors below are numpy's own.
# `default_rng` is deterministic for a seed but does NOT reproduce numpy's PCG64 stream,
# so its tests check behaviour, never values.
import numpy as np

# === numpy's legacy vectors ===
np.random.seed(0)
assert np.random.rand() == 0.5488135039273248
np.random.seed(0)
assert np.allclose(np.random.randn(5), [1.76405235, 0.40015721, 0.97873798, 2.2408932, 1.86755799])
np.random.seed(0)
assert np.random.randint(0, 10, 5).tolist() == [5, 0, 3, 3, 7]
np.random.seed(0)
assert np.random.permutation(10).tolist() == [2, 8, 4, 9, 1, 6, 7, 3, 0, 5]
np.random.seed(42)
assert np.allclose(np.random.rand(3), [0.37454012, 0.95071431, 0.73199394])
np.random.seed(42)
assert abs(np.random.randn() - 0.4967141530112327) < 1e-15

# === determinism ===
np.random.seed(7)
first = np.random.random(4).tolist()
np.random.seed(7)
assert np.random.random(4).tolist() == first
state = np.random.RandomState(7)
assert state.random_sample(4).tolist() == first

# === shapes and ranges ===
np.random.seed(1)
assert np.random.rand(2, 3).shape == (2, 3)
assert np.random.randn(4).shape == (4,)
assert isinstance(np.random.rand(), float)
draws = np.random.randint(5, size=1000)
assert draws.min() >= 0 and draws.max() <= 4 and set(draws.tolist()) == {0, 1, 2, 3, 4}
assert np.random.randint(3, 6, size=(2, 2)).shape == (2, 2)
u = np.random.uniform(-1, 1, size=1000)
assert u.min() >= -1 and u.max() < 1
n = np.random.normal(10, 2, size=5000)
assert abs(n.mean() - 10) < 0.2 and abs(n.std() - 2) < 0.2
assert np.random.normal(size=(3, 2)).shape == (3, 2)
assert np.random.exponential(2.0, size=3).shape == (3,)
assert np.random.poisson(3.0, size=4).dtype == np.int64
assert 0 <= np.random.binomial(10, 0.5) <= 10
try:
    np.random.randint(5, 5)
    assert False
except ValueError as e:
    assert str(e) == 'high <= low'

# === choice, shuffle, permutation ===
pick = np.random.choice([10, 20, 30])
assert pick in (10, 20, 30)
sample = np.random.choice(5, 3, replace=False)
assert len(set(sample.tolist())) == 3 and all(0 <= s < 5 for s in sample)
assert np.random.choice(['a', 'b'], size=4).shape == (4,)
weighted = np.random.choice(3, size=200, p=[0.0, 0.0, 1.0])
assert set(weighted.tolist()) == {2}
try:
    np.random.choice(3, 5, replace=False)
    assert False
except ValueError:
    pass
try:
    np.random.choice(3, p=[0.5, 0.5, 0.5])
    assert False
except ValueError as e:
    assert str(e) == 'probabilities do not sum to 1'
values = np.arange(10)
np.random.shuffle(values)
assert sorted(values.tolist()) == list(range(10))
items = [1, 2, 3, 4]
np.random.shuffle(items)
assert sorted(items) == [1, 2, 3, 4]
rows = np.arange(6).reshape(3, 2)
np.random.shuffle(rows)
assert sorted(r.tolist() for r in rows) == [[0, 1], [2, 3], [4, 5]]
p = np.random.permutation([1, 2, 3])
assert sorted(p.tolist()) == [1, 2, 3]

# === default_rng ===
g1 = np.random.default_rng(123)
g2 = np.random.default_rng(123)
assert g1.random(5).tolist() == g2.random(5).tolist()
assert g1.integers(0, 10, size=8).tolist() == g2.integers(0, 10, size=8).tolist()
rng = np.random.default_rng(2024)
ints = rng.integers(0, 3, size=500)
assert set(ints.tolist()) == {0, 1, 2}
assert rng.integers(1, 3, endpoint=True, size=300).max() == 3
r = rng.random(1000)
assert r.min() >= 0 and r.max() < 1
assert rng.normal(0, 1, size=(2, 3)).shape == (2, 3)
assert rng.uniform(5, 6) >= 5
assert rng.choice([1, 2, 3]) in (1, 2, 3)
assert len(set(rng.choice(10, 10, replace=False).tolist())) == 10
arr = np.arange(5)
rng.shuffle(arr)
assert sorted(arr.tolist()) == [0, 1, 2, 3, 4]
assert sorted(rng.permutation(5).tolist()) == [0, 1, 2, 3, 4]
assert isinstance(rng.random(), float)

from numpy.random import default_rng
assert default_rng(1).random() == np.random.default_rng(1).random()
