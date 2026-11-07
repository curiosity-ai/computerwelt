# Extension: `numpy` — element-wise arithmetic, broadcasting, comparisons and ufuncs.
# Upstream Monty has no numpy. Integer arithmetic is int64; mixing in a float or dividing
# promotes to float64, as numpy's type promotion does. Expected values are numpy 2's.
import numpy as np

a = np.array([1, 2, 3])

# === operators, scalars on either side ===
assert (a + 1).tolist() == [2, 3, 4] and (a + 1).dtype == np.int64
assert (1 + a).tolist() == [2, 3, 4]
assert (10 - a).tolist() == [9, 8, 7]
assert (a * 2.5).tolist() == [2.5, 5.0, 7.5] and (a * 2.5).dtype == np.float64
assert (a / 2).tolist() == [0.5, 1.0, 1.5] and (a / 1).dtype == np.float64
assert (1 / np.array([2.0, 4.0])).tolist() == [0.5, 0.25]
assert (a // 2).tolist() == [0, 1, 1]
assert (a % 2).tolist() == [1, 0, 1]
assert (a ** 2).tolist() == [1, 4, 9]
assert (2 ** a).tolist() == [2, 4, 8]
assert (-a).tolist() == [-1, -2, -3] and (+a).tolist() == [1, 2, 3]
assert abs(np.array([-1, 2])).tolist() == [1, 2]
assert (a + [10, 20, 30]).tolist() == [11, 22, 33]
assert ([10, 20, 30] - a).tolist() == [9, 18, 27]
assert (a + a).tolist() == [2, 4, 6]

# Floor division and remainder take the sign of the divisor, like Python's.
assert (np.array([-7]) // 2).tolist() == [-4]
assert (np.array([-7]) % 2).tolist() == [1]
assert (np.array([-7.5]) % 2).tolist() == [0.5]
assert np.mod([-1, 5], 3).tolist() == [2, 2]
assert np.fmod([-7], 2).tolist() == [-1]
# Integer division by zero is 0 in numpy (it warns); float division gives inf.
assert (np.array([1]) // 0).tolist() == [0]
assert (np.array([1.0]) / 0).tolist() == [float('inf')]
assert np.isnan(np.array([0.0]) / 0)[0]

# Integers wrap at 64 bits.
assert (np.array([2 ** 62]) * 4).tolist() == [0]

try:
    np.array([2]) ** -1
    assert False
except ValueError as e:
    assert str(e) == 'Integers to negative integer powers are not allowed.'

# === booleans ===
t = np.array([True, False])
assert (t + np.array([True, True])).tolist() == [True, True]
assert (t * 1).tolist() == [1, 0]
assert np.array([True, True, False]).sum() == 2
try:
    -t
    assert False
except TypeError:
    pass

# === broadcasting ===
assert (np.ones((2, 3)) + np.array([1, 2, 3])).tolist() == [[2.0, 3.0, 4.0], [2.0, 3.0, 4.0]]
assert (np.arange(3).reshape(3, 1) + np.arange(2)).tolist() == [[0, 1], [1, 2], [2, 3]]
assert (np.array([1, 2]) + np.array([[10], [20]])).tolist() == [[11, 12], [21, 22]]
assert (np.arange(6).reshape(2, 3) * np.array([[1], [-1]])).tolist() == [[0, 1, 2], [-3, -4, -5]]
try:
    np.array([1, 2]) + np.array([1, 2, 3])
    assert False
except ValueError as e:
    assert str(e) == 'operands could not be broadcast together with shapes (2,) (3,) ', repr(str(e))
try:
    np.ones((2, 3)) + np.ones((3, 2))
    assert False
except ValueError as e:
    assert str(e) == 'operands could not be broadcast together with shapes (2,3) (3,2) ', repr(str(e))

# === in place ===
x = np.arange(5)
y = x
y += 1
assert x.tolist() == [1, 2, 3, 4, 5]
x *= 2
assert x.tolist() == [2, 4, 6, 8, 10]
part = x[1:3]
part -= 100
assert x.tolist() == [2, -96, -94, 8, 10]
f = np.zeros(3)
f += [1, 2, 3]
f /= 2
assert f.tolist() == [0.5, 1.0, 1.5]
try:
    x += 0.5
    assert False
except TypeError:
    pass

# === comparisons ===
assert (a == 2).tolist() == [False, True, False]
assert (a != 2).tolist() == [True, False, True]
assert (a > 1).tolist() == [False, True, True]
assert (a <= 2).tolist() == [True, True, False]
assert (2 < a).tolist() == [False, False, True]
assert (a == [1, 5, 3]).tolist() == [True, False, True]
assert ((a > 1) & (a < 3)).tolist() == [False, True, False]
assert ((a < 2) | (a > 2)).tolist() == [True, False, True]
assert (~(a > 1)).tolist() == [True, False, False]
assert ((a > 1) ^ (a > 2)).tolist() == [False, True, False]
assert (np.array(['a', 'b']) == 'a').tolist() == [True, False]
assert (a == np.array([[1], [3]])).tolist() == [[True, False, False], [False, False, True]]

# === ufuncs ===
assert np.sqrt([1, 4, 9]).tolist() == [1.0, 2.0, 3.0]
assert np.sqrt(16) == 4.0
assert np.exp(0) == 1.0 and np.log(np.e) == 1.0
assert np.log10(1000) == 3.0 and np.log2(8) == 3.0
assert np.log1p(0) == 0.0 and np.expm1(0) == 0.0
assert np.sin(0) == 0.0 and np.cos(0) == 1.0 and np.tan(0) == 0.0
assert np.arcsin(1) == np.pi / 2 and np.arccos(1) == 0.0 and np.arctan(0) == 0.0
assert np.arctan2(1, 1) == np.pi / 4
assert np.sinh(0) == 0.0 and np.cosh(0) == 1.0 and np.tanh(0) == 0.0
assert np.hypot(3, 4) == 5.0
assert np.degrees(np.pi) == 180.0 and np.radians(180) == np.pi
assert np.abs([-1.5, 2]).tolist() == [1.5, 2.0]
assert np.absolute([-3]).tolist() == [3]
assert np.sign([-2, 0, 3]).tolist() == [-1, 0, 1]
assert np.sign([-2.5, 0.0]).tolist() == [-1.0, 0.0]
assert np.floor([1.5, -1.5]).tolist() == [1.0, -2.0]
assert np.ceil([1.2, -1.2]).tolist() == [2.0, -1.0]
assert np.trunc([1.7, -1.7]).tolist() == [1.0, -1.0]
assert np.rint([0.5, 1.5, 2.5]).tolist() == [0.0, 2.0, 2.0]
assert np.round([0.5, 1.5, 2.5]).tolist() == [0.0, 2.0, 2.0]
assert np.round([1.234, 5.678], 1).tolist() == [1.2, 5.7]
assert np.around(1.2345, 2) == 1.23
assert np.round([1234, 5678], -2).tolist() == [1200, 5700]
assert np.array([1.25, 2.5]).round(1).tolist() == [1.2, 2.5]
assert round(np.array([1.5, 2.5])).tolist() == [2.0, 2.0]
assert np.square([2, 3]).tolist() == [4, 9]
assert np.negative([1, -2]).tolist() == [-1, 2]
assert np.power([2, 3], 2).tolist() == [4, 9]
assert np.maximum([1, 5], [3, 2]).tolist() == [3, 5]
assert np.minimum([1, 5], 3).tolist() == [1, 3]
assert np.isnan(np.maximum([1.0], [np.nan]))[0]
assert np.fmax([1.0, np.nan], [np.nan, 2.0]).tolist() == [1.0, 2.0]
assert np.add([1, 2], [3, 4]).tolist() == [4, 6]
assert np.subtract(5, [1, 2]).tolist() == [4, 3]
assert np.multiply([1, 2], 3).tolist() == [3, 6]
assert np.divide([1, 2], 4).tolist() == [0.25, 0.5]
assert np.floor_divide([7, -7], 2).tolist() == [3, -4]
assert np.isnan([1, np.nan]).tolist() == [False, True]
assert np.isinf([1, np.inf, -np.inf]).tolist() == [False, True, True]
assert np.isfinite([1, np.inf, np.nan]).tolist() == [True, False, False]
assert np.logical_and([True, True], [True, False]).tolist() == [True, False]
assert np.logical_or([0, 0], [0, 1]).tolist() == [False, True]
assert np.logical_not([0, 2]).tolist() == [True, False]
assert np.clip([1, 5, 10], 2, 8).tolist() == [2, 5, 8]
assert np.clip([1, 5, 10], None, 4).tolist() == [1, 4, 4]
assert np.array([-3, 0, 3]).clip(-1, 1).tolist() == [-1, 0, 1]
assert np.nan_to_num([np.nan, 1.0, np.inf])[:2].tolist() == [0.0, 1.0]

# === where ===
assert np.where([True, False, True], 1, -1).tolist() == [1, -1, 1]
assert np.where(np.arange(5) > 2, np.arange(5), 0).tolist() == [0, 0, 0, 3, 4]
assert np.where(a > 1, a, 0.5).tolist() == [0.5, 2.0, 3.0]
assert [i.tolist() for i in np.where([0, 3, 0, 4])] == [[1, 3]]
rows, cols = np.where(np.array([[0, 1], [1, 0]]))
assert rows.tolist() == [0, 1] and cols.tolist() == [1, 0]
assert np.select([a < 2, a > 2], [a, a * 10], default=0).tolist() == [1, 0, 30]

# === scalar conversion ===
assert float(np.array(2.5)) == 2.5
assert int(np.array([[4]])) == 4
try:
    float(np.array([1.0, 2.0]))
    assert False
except TypeError:
    pass

# === vectorize ===
double = np.vectorize(lambda v: v * 2)
assert double([1, 2, 3]).tolist() == [2, 4, 6]
assert np.vectorize(lambda p, q: p + q)([1, 2], 10).tolist() == [11, 12]

with np.errstate(divide='ignore', invalid='ignore'):
    assert (np.array([1.0, -1.0]) / 0).tolist() == [float('inf'), float('-inf')]
