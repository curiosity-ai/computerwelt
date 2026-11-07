# Extension: `numpy` — array creation and dtypes. Upstream Monty has no numpy at all; this
# port carries a managed subset (int64, float64, bool, str and object arrays). Every expected
# value is what numpy 2 produces. Elements come back as Python int/float/bool, so these
# assertions compare through `tolist()` and `==` rather than through numpy scalar reprs.
import numpy as np

# === array / asarray and dtype inference ===
a = np.array([1, 2, 3])
assert a.dtype == np.int64 and a.dtype == 'int64' and str(a.dtype) == 'int64'
assert a.shape == (3,) and a.ndim == 1 and a.size == 3 and len(a) == 3
assert a.tolist() == [1, 2, 3]
assert np.array([1, 2.5]).dtype == np.float64
assert np.array([True, 2]).dtype == np.int64
assert np.array([True, False]).dtype == np.bool_
assert np.array([1, 2], dtype=float).tolist() == [1.0, 2.0]
assert np.array((1, 2)).tolist() == [1, 2]
assert np.array(range(4)).tolist() == [0, 1, 2, 3]
assert np.array([[1, 2], [3, 4]]).shape == (2, 2)
assert np.array(7).shape == () and np.array(7).ndim == 0
assert np.array([1, 2], ndmin=2).shape == (1, 2)
s = np.array(['a', 'bc'])
assert s.dtype == '<U2' and s.tolist() == ['a', 'bc']
assert np.array([1, 'a', None], dtype=object).tolist() == [1, 'a', None]

try:
    np.array([[1, 2], [3]])
    assert False, 'ragged nested lists must not build an array'
except ValueError as e:
    assert str(e).startswith('setting an array element with a sequence'), str(e)

# asarray hands back the same array when nothing needs converting; array copies.
assert np.asarray(a) is a
assert np.array(a) is not a
c = np.array(a)
c[0] = 99
assert a[0] == 1

# === constructors ===
assert np.zeros(3).tolist() == [0.0, 0.0, 0.0]
assert np.zeros((2, 2), dtype=int).tolist() == [[0, 0], [0, 0]]
assert np.zeros(2, dtype=int).dtype == np.int64
assert np.ones(2).tolist() == [1.0, 1.0]
assert np.ones((1, 2), dtype=bool).tolist() == [[True, True]]
assert np.empty((2, 3)).shape == (2, 3)
assert np.full(3, 7).tolist() == [7, 7, 7] and np.full(3, 7).dtype == np.int64
assert np.full((2,), 1.5).tolist() == [1.5, 1.5]
assert np.zeros_like([[1, 2], [3, 4]]).tolist() == [[0, 0], [0, 0]]
assert np.ones_like([1.5, 2.5]).tolist() == [1.0, 1.0]
assert np.full_like([1, 2], 9).tolist() == [9, 9]
assert np.zeros_like([1, 2], dtype=float).dtype == np.float64

# === ranges ===
assert np.arange(5).tolist() == [0, 1, 2, 3, 4]
assert np.arange(2, 10, 3).tolist() == [2, 5, 8]
assert np.arange(5, 0, -1).tolist() == [5, 4, 3, 2, 1]
assert np.arange(0, 1, 0.25).tolist() == [0.0, 0.25, 0.5, 0.75]
assert np.arange(3.0).dtype == np.float64
assert np.arange(3, dtype=float).tolist() == [0.0, 1.0, 2.0]
assert np.linspace(0, 1, 5).tolist() == [0.0, 0.25, 0.5, 0.75, 1.0]
assert np.linspace(0, 1, 4, endpoint=False).tolist() == [0.0, 0.25, 0.5, 0.75]
values, step = np.linspace(0, 10, 5, retstep=True)
assert step == 2.5 and values.tolist() == [0.0, 2.5, 5.0, 7.5, 10.0]
assert np.logspace(0, 2, 3).tolist() == [1.0, 10.0, 100.0]

# === matrices ===
assert np.eye(2).tolist() == [[1.0, 0.0], [0.0, 1.0]]
assert np.eye(2, 3, k=1).tolist() == [[0.0, 1.0, 0.0], [0.0, 0.0, 1.0]]
assert np.identity(3).sum() == 3.0
assert np.diag([1, 2, 3]).tolist() == [[1, 0, 0], [0, 2, 0], [0, 0, 3]]
assert np.diag(np.arange(9).reshape(3, 3)).tolist() == [0, 4, 8]
assert np.diag([1, 2], k=1).tolist() == [[0, 1, 0], [0, 0, 2], [0, 0, 0]]
assert np.triu([[1, 2], [3, 4]]).tolist() == [[1, 2], [0, 4]]
assert np.tril([[1, 2], [3, 4]]).tolist() == [[1, 0], [3, 4]]

xs, ys = np.meshgrid([1, 2, 3], [4, 5])
assert xs.shape == (2, 3)
assert xs.tolist() == [[1, 2, 3], [1, 2, 3]]
assert ys.tolist() == [[4, 4, 4], [5, 5, 5]]
ii, jj = np.meshgrid([1, 2], [3, 4, 5], indexing='ij')
assert ii.shape == (2, 3) and jj.tolist() == [[3, 4, 5], [3, 4, 5]]

assert np.tile([1, 2], 2).tolist() == [1, 2, 1, 2]
assert np.tile([[1, 2]], (2, 1)).tolist() == [[1, 2], [1, 2]]
assert np.repeat([1, 2], 2).tolist() == [1, 1, 2, 2]
assert np.repeat([[1, 2], [3, 4]], 2, axis=0).tolist() == [[1, 2], [1, 2], [3, 4], [3, 4]]
assert np.repeat([1, 2, 3], [0, 1, 2]).tolist() == [2, 3, 3]

# === astype and the scalar types ===
assert np.array([1.7, -2.7]).astype(int).tolist() == [1, -2]
assert np.array([1, 0, 2]).astype(bool).tolist() == [True, False, True]
assert np.array([1, 2]).astype(np.float64).tolist() == [1.0, 2.0]
assert np.array([1, 2]).astype(str).tolist() == ['1', '2']
assert np.float64(3) == 3.0 and isinstance(np.float64(3), float)
assert np.int64(2.7) == 2
assert np.dtype('float64') == np.float64
assert repr(np.dtype(int)) == "dtype('int64')"
assert np.issubdtype(np.array([1]).dtype, np.integer)
assert np.issubdtype(np.array([1.0]).dtype, np.floating)
assert not np.issubdtype(np.array([1.0]).dtype, np.integer)
assert type(a) is np.ndarray and isinstance(a, np.ndarray)

# === constants ===
assert np.pi == 3.141592653589793 and np.e == 2.718281828459045
assert np.inf > 10 ** 300 and np.isnan(np.nan) and np.newaxis is None

# === the sandbox's cap on one allocation ===
try:
    np.zeros(10 ** 9)
    assert False, 'a billion-element array must be refused'
except MemoryError:
    pass

try:
    np.ones((100000, 100000))
    assert False
except MemoryError:
    pass
