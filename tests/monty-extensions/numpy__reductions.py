# Extension: `numpy` — reductions and statistics, over everything or along an axis.
# Upstream Monty has no numpy. Float sums use numpy's pairwise summation, so
# `np.sum([0.1] * 10)` is 1.0 here as in numpy (Python's own `sum` gives 0.9999999999999999).
# Expected values are numpy 2's.
import numpy as np

x = np.arange(12).reshape(3, 4)

# === sum, prod, mean ===
assert x.sum() == 66 and np.sum(x) == 66
assert x.sum(axis=0).tolist() == [12, 15, 18, 21]
assert x.sum(axis=1).tolist() == [6, 22, 38]
assert x.sum(axis=-1).tolist() == [6, 22, 38]
assert x.sum(axis=(0, 1)) == 66
assert x.sum(axis=1, keepdims=True).shape == (3, 1)
assert x.sum(axis=1, keepdims=True).tolist() == [[6], [22], [38]]
assert np.sum([0.1] * 10) == 1.0
assert np.sum([]) == 0.0
assert np.sum([[1, 2], [3, 4]], axis=0).tolist() == [4, 6]
assert np.prod([1, 2, 3, 4]) == 24
assert x.prod(axis=1).tolist() == [0, 840, 7920]
assert x.mean() == 5.5
assert x.mean(axis=0).tolist() == [4.0, 5.0, 6.0, 7.0]
assert np.mean([[1, 2], [3, 4]], axis=1, keepdims=True).tolist() == [[1.5], [3.5]]
assert np.mean([1, 2, 3, 4]) == 2.5

# === spread ===
assert np.var([1, 2, 3, 4]) == 1.25
assert np.var([1, 2, 3, 4], ddof=1) == 5 / 3
assert abs(np.std([1, 2, 3, 4]) - 1.118033988749895) < 1e-15
assert np.std([2, 2, 2]) == 0.0
assert np.std([[1, 2], [3, 4]], axis=0).tolist() == [1.0, 1.0]
assert np.ptp([1, 5, 3]) == 4

# === extremes ===
assert x.min() == 0 and x.max() == 11
assert x.max(axis=1).tolist() == [3, 7, 11]
assert x.min(axis=0).tolist() == [0, 1, 2, 3]
assert np.amax([1, 9]) == 9 and np.amin([[4, 2], [3, 8]]) == 2
assert np.max([[1, 2], [0, 5]], axis=0).tolist() == [1, 5]
assert x.argmax() == 11 and x.argmin() == 0
assert x.argmax(axis=0).tolist() == [2, 2, 2, 2]
assert x.argmin(axis=1).tolist() == [0, 0, 0]
assert np.argmax([3, 7, 7, 1]) == 1
assert np.argmin([3, 1, 1]) == 1
assert np.isnan(np.max([1.0, np.nan]))
assert np.argmax([1, np.nan, 3]) == 1
try:
    np.min([])
    assert False
except ValueError as e:
    assert str(e) == 'zero-size array to reduction operation minimum which has no identity'

# === median, percentile, quantile ===
assert np.median([3, 1, 2]) == 2.0
assert np.median([1, 2, 3, 4]) == 2.5
assert np.median([[1, 2, 3], [4, 5, 6]], axis=0).tolist() == [2.5, 3.5, 4.5]
assert np.median([[1, 2, 3], [4, 5, 6]], axis=1).tolist() == [2.0, 5.0]
assert np.percentile([1, 2, 3, 4], 50) == 2.5
assert np.percentile([1, 2, 3, 4], 25) == 1.75
assert np.percentile([1, 2, 3, 4], [0, 100]).tolist() == [1.0, 4.0]
assert np.quantile([1, 2, 3, 4], [0.25, 0.75]).tolist() == [1.75, 3.25]
assert abs(np.percentile(np.arange(10), 90) - 8.1) < 1e-12
assert np.percentile([[1, 2], [3, 4]], 50, axis=0).tolist() == [2.0, 3.0]
try:
    np.percentile([1, 2], 150)
    assert False
except ValueError as e:
    assert str(e) == 'Percentiles must be in the range [0, 100]'

# === average ===
assert np.average([1, 2, 3, 4]) == 2.5
assert np.average([1, 2, 3], weights=[3, 2, 1]) == 10 / 6
assert np.average([[1, 2], [3, 4]], axis=1, weights=[1, 3]).tolist() == [1.75, 3.75]

# === cumulative ===
assert np.cumsum([1, 2, 3]).tolist() == [1, 3, 6]
assert np.cumprod([1, 2, 3, 4]).tolist() == [1, 2, 6, 24]
assert np.cumsum([[1, 2], [3, 4]]).tolist() == [1, 3, 6, 10]
assert np.cumsum([[1, 2], [3, 4]], axis=0).tolist() == [[1, 2], [4, 6]]
assert x.cumsum(axis=1).tolist() == [[0, 1, 3, 6], [4, 9, 15, 22], [8, 17, 27, 38]]
assert np.cumsum([0.5, 0.25]).tolist() == [0.5, 0.75]

# === truth ===
assert np.any([0, 0, 1]) and not np.all([1, 1, 0])
assert np.all([]) and not np.any([])
assert (x > 5).any(axis=1).tolist() == [False, True, True]
assert (x > 0).all(axis=0).tolist() == [False, True, True, True]
assert np.count_nonzero([0, 1, 2, 0]) == 2
assert np.count_nonzero(x, axis=0).tolist() == [2, 3, 3, 3]

# === NaN-aware ===
n = np.array([1.0, np.nan, 3.0])
assert np.isnan(np.sum(n)) and np.isnan(np.mean(n))
assert np.nansum(n) == 4.0
assert np.nanmean(n) == 2.0
assert np.nanmax(n) == 3.0 and np.nanmin(n) == 1.0
assert np.nanstd([1.0, np.nan, 3.0]) == 1.0
assert np.nanmedian([1.0, np.nan, 3.0, 10.0]) == 3.0
assert np.nanargmax([np.nan, 2.0, 1.0]) == 1

# === bool and int inputs ===
assert np.mean([True, False, True, True]) == 0.75
assert np.sum(np.array([True, True])) == 2
assert np.mean(np.array([1, 2], dtype=int)) == 1.5

# === statistics over pairs ===
assert np.cov([1, 2, 3], [1, 2, 3]).tolist() == [[1.0, 1.0], [1.0, 1.0]]
assert np.cov([[1, 2, 3], [3, 2, 1]]).tolist() == [[1.0, -1.0], [-1.0, 1.0]]
assert np.cov([1, 2, 3, 4], bias=True) == 1.25
c = np.corrcoef([1, 2, 3], [2, 4, 7])
assert c.shape == (2, 2) and c[0, 0] == 1.0 and abs(c[0, 1] - 0.9933992677987828) < 1e-12
assert np.allclose(np.corrcoef([1, 2, 3], [3, 2, 1]), [[1, -1], [-1, 1]])
