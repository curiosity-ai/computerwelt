# Extension: `numpy` — reshaping, joining, splitting, sorting and searching. Upstream Monty
# has no numpy. Expected values are numpy 2's.
import numpy as np

a = np.arange(6)

# === reshape and friends ===
assert a.reshape(2, 3).tolist() == [[0, 1, 2], [3, 4, 5]]
assert a.reshape((3, -1)).shape == (3, 2)
assert a.reshape(-1, 1).shape == (6, 1)
assert np.reshape(a, (2, 3)).shape == (2, 3)
assert a.reshape(2, 3).ravel().tolist() == [0, 1, 2, 3, 4, 5]
assert a.reshape(2, 3).flatten().tolist() == [0, 1, 2, 3, 4, 5]
assert a.reshape(2, 3).T.tolist() == [[0, 3], [1, 4], [2, 5]]
assert a.reshape(2, 3).T.ravel().tolist() == [0, 3, 1, 4, 2, 5]
assert np.transpose(np.zeros((2, 3, 4))).shape == (4, 3, 2)
assert np.zeros((2, 3, 4)).transpose(1, 0, 2).shape == (3, 2, 4)
assert np.swapaxes(np.zeros((2, 3, 4)), 0, 2).shape == (4, 3, 2)
assert np.moveaxis(np.zeros((2, 3, 4)), 0, -1).shape == (3, 4, 2)
assert np.squeeze(np.zeros((1, 3, 1))).shape == (3,)
assert np.zeros((1, 3, 1)).squeeze(axis=0).shape == (3, 1)
assert np.expand_dims([1, 2], 0).shape == (1, 2)
assert np.expand_dims([1, 2], axis=1).shape == (2, 1)
assert np.atleast_2d([1, 2]).shape == (1, 2)
assert np.shape([[1, 2, 3]]) == (1, 3) and np.ndim([[1]]) == 2 and np.size([[1, 2], [3, 4]]) == 4
try:
    a.reshape(4)
    assert False
except ValueError as e:
    assert str(e) == 'cannot reshape array of size 6 into shape (4,)', str(e)

# === joining ===
x = np.array([[1, 2], [3, 4]])
assert np.concatenate([x, x]).tolist() == [[1, 2], [3, 4], [1, 2], [3, 4]]
assert np.concatenate([x, x], axis=1).tolist() == [[1, 2, 1, 2], [3, 4, 3, 4]]
assert np.concatenate([x, x], axis=None).tolist() == [1, 2, 3, 4, 1, 2, 3, 4]
assert np.concatenate(([1], [2.5])).tolist() == [1.0, 2.5]
assert np.stack([[1, 2], [3, 4]]).tolist() == [[1, 2], [3, 4]]
assert np.stack([[1, 2], [3, 4]], axis=1).tolist() == [[1, 3], [2, 4]]
assert np.vstack([[1, 2], [3, 4]]).tolist() == [[1, 2], [3, 4]]
assert np.vstack([x, [5, 6]]).shape == (3, 2)
assert np.hstack([[1, 2], [3]]).tolist() == [1, 2, 3]
assert np.hstack([x, x]).shape == (2, 4)
assert np.column_stack([[1, 2], [3, 4]]).tolist() == [[1, 3], [2, 4]]
assert np.append([1, 2], [3, 4]).tolist() == [1, 2, 3, 4]
assert np.append(x, [[5, 6]], axis=0).tolist() == [[1, 2], [3, 4], [5, 6]]
assert np.delete([1, 2, 3, 4], 1).tolist() == [1, 3, 4]
try:
    np.concatenate([np.zeros((2, 2)), np.zeros((2, 3))])
    assert False
except ValueError:
    pass

# === splitting ===
assert [p.tolist() for p in np.split(np.arange(6), 3)] == [[0, 1], [2, 3], [4, 5]]
assert [p.tolist() for p in np.split(np.arange(6), [2, 5])] == [[0, 1], [2, 3, 4], [5]]
assert [p.tolist() for p in np.array_split(np.arange(7), 3)] == [[0, 1, 2], [3, 4], [5, 6]]
assert [p.shape for p in np.hsplit(np.zeros((2, 4)), 2)] == [(2, 2), (2, 2)]
assert [p.shape for p in np.vsplit(np.zeros((4, 2)), 2)] == [(2, 2), (2, 2)]
try:
    np.split(np.arange(5), 2)
    assert False
except ValueError as e:
    assert str(e) == 'array split does not result in an equal division'

# === rearranging ===
assert np.flip([1, 2, 3]).tolist() == [3, 2, 1]
assert np.flip([[1, 2], [3, 4]], axis=1).tolist() == [[2, 1], [4, 3]]
assert np.fliplr([[1, 2], [3, 4]]).tolist() == [[2, 1], [4, 3]]
assert np.flipud([[1, 2], [3, 4]]).tolist() == [[3, 4], [1, 2]]
assert np.roll([1, 2, 3, 4], 1).tolist() == [4, 1, 2, 3]
assert np.roll([1, 2, 3, 4], -1).tolist() == [2, 3, 4, 1]
assert np.roll([[1, 2], [3, 4]], 1, axis=0).tolist() == [[3, 4], [1, 2]]
assert np.roll([[1, 2], [3, 4]], 1).tolist() == [[4, 1], [2, 3]]
assert np.take([10, 20, 30], [2, 0]).tolist() == [30, 10]
assert np.take([[1, 2], [3, 4]], [1], axis=1).tolist() == [[2], [4]]

# === sorting ===
assert np.sort([3, 1, 2]).tolist() == [1, 2, 3]
assert np.sort([[3, 1], [2, 4]]).tolist() == [[1, 3], [2, 4]]
assert np.sort([[3, 1], [2, 4]], axis=0).tolist() == [[2, 1], [3, 4]]
assert np.sort([[3, 1], [2, 4]], axis=None).tolist() == [1, 2, 3, 4]
assert np.sort([2.0, np.nan, 1.0])[:2].tolist() == [1.0, 2.0]
assert np.isnan(np.sort([2.0, np.nan, 1.0])[2])
assert np.sort(['b', 'a']).tolist() == ['a', 'b']
assert np.argsort([3, 1, 2]).tolist() == [1, 2, 0]
assert np.argsort([[3, 1], [2, 4]], axis=1).tolist() == [[1, 0], [0, 1]]
s = np.array([3, 1, 2])
s.sort()
assert s.tolist() == [1, 2, 3]
assert np.sort([3, 1, 2])[::-1].tolist() == [3, 2, 1]

# === uniqueness and sets ===
assert np.unique([3, 1, 2, 3, 1]).tolist() == [1, 2, 3]
values, counts = np.unique([3, 1, 2, 3, 1], return_counts=True)
assert values.tolist() == [1, 2, 3] and counts.tolist() == [2, 1, 2]
values, first = np.unique(['b', 'a', 'b'], return_index=True)
assert values.tolist() == ['a', 'b'] and first.tolist() == [1, 0]
values, inverse = np.unique([3, 1, 3], return_inverse=True)
assert values[inverse].tolist() == [3, 1, 3]
assert np.isin([1, 2, 3], [2, 3]).tolist() == [False, True, True]
assert np.isin([1, 2, 3], [2], invert=True).tolist() == [True, False, True]
assert np.union1d([1, 3], [2, 3]).tolist() == [1, 2, 3]
assert np.intersect1d([1, 2, 3], [2, 3, 4]).tolist() == [2, 3]
assert np.setdiff1d([1, 2, 3], [2]).tolist() == [1, 3]

# === searching and counting ===
assert np.searchsorted([1, 2, 2, 3], 2) == 1
assert np.searchsorted([1, 2, 2, 3], 2, side='right') == 3
assert np.searchsorted([1, 2, 3], [0, 2.5, 9]).tolist() == [0, 2, 3]
assert np.nonzero([0, 3, 0, 4])[0].tolist() == [1, 3]
assert np.flatnonzero([[0, 1], [1, 0]]).tolist() == [1, 2]
assert np.argwhere([[0, 1], [1, 0]]).tolist() == [[0, 1], [1, 0]]
assert np.bincount([0, 1, 1, 3]).tolist() == [1, 2, 0, 1]
assert np.bincount([1], minlength=3).tolist() == [0, 1, 0]
assert np.bincount([0, 1, 1], weights=[0.5, 1.0, 2.0]).tolist() == [0.5, 3.0]
assert np.digitize([0.5, 1, 1.5, 2.5], [1, 2]).tolist() == [0, 1, 1, 2]
assert np.digitize([0.5, 1, 1.5, 2.5], [1, 2], right=True).tolist() == [0, 0, 1, 2]
counts, edges = np.histogram([1, 2, 2, 3, 3, 3], bins=3)
assert counts.tolist() == [1, 2, 3]
assert edges[0] == 1.0 and edges[-1] == 3.0 and len(edges) == 4
counts, edges = np.histogram([1, 2, 2, 3, 3, 3, 4], bins=3, range=(1, 4))
assert counts.tolist() == [1, 2, 4] and edges.tolist() == [1.0, 2.0, 3.0, 4.0]
counts, _ = np.histogram([0.5, 1.5, 1.7], bins=[0, 1, 2])
assert counts.tolist() == [1, 2]

# === differences and interpolation ===
assert np.diff([1, 4, 9, 16]).tolist() == [3, 5, 7]
assert np.diff([1, 4, 9, 16], n=2).tolist() == [2, 2]
assert np.diff([[1, 3, 6], [0, 0, 1]]).tolist() == [[2, 3], [0, 1]]
assert np.diff([[1, 3], [0, 5]], axis=0).tolist() == [[-1, 2]]
assert np.interp(2.5, [1, 2, 3], [10, 20, 30]) == 25.0
assert np.interp([0, 1.5, 5], [1, 2, 3], [10, 20, 30]).tolist() == [10.0, 15.0, 30.0]
assert np.interp([0, 5], [1, 2], [1, 2], left=-1, right=99).tolist() == [-1.0, 99.0]

# === comparison of whole arrays ===
assert np.array_equal([1, 2], [1, 2]) and not np.array_equal([1, 2], [1, 2, 3])
assert np.array_equal([1.0, np.nan], [1.0, np.nan], equal_nan=True)
assert not np.array_equal([1.0, np.nan], [1.0, np.nan])
assert np.allclose([1, 2], [1, 2.0000001])
assert not np.allclose([1, 2], [1, 2.1])
assert np.isclose([1, 2], [1, 3]).tolist() == [True, False]
assert np.allclose([np.nan], [np.nan], equal_nan=True)

# === products of vectors ===
assert np.outer([1, 2], [3, 4]).tolist() == [[3, 4], [6, 8]]
assert np.cross([1, 0, 0], [0, 1, 0]).tolist() == [0, 0, 1]
assert np.inner([1, 2], [3, 4]) == 11
assert np.convolve([1, 2, 3], [0, 1, 0.5]).tolist() == [0.0, 1.0, 2.5, 4.0, 1.5]
assert np.convolve([1, 2, 3], [1, 1], mode='valid').tolist() == [3, 5]

# === apply_along_axis ===
assert np.apply_along_axis(sum, 1, [[1, 2], [3, 4]]).tolist() == [3, 7]
assert np.apply_along_axis(lambda r: r * 2, 0, [[1, 2], [3, 4]]).tolist() == [[2, 4], [6, 8]]
