# Extension: `numpy` — how arrays print. Upstream Monty has no numpy. A notebook's output is
# mostly printed arrays, so `repr` and `str` follow numpy's arrayprint: the fewest digits
# that identify each float (at most 8 decimals), a shared width with the point kept (`2.`),
# numpy's switch to scientific notation, 75-column wrapping and `...` past 1000 elements.
# Every expected string is numpy 2's.
import numpy as np

# === integers and booleans ===
assert repr(np.array([1, 2, 3])) == 'array([1, 2, 3])'
assert str(np.array([1, 2, 3])) == '[1 2 3]'
assert repr(np.array([1, 10, 100])) == 'array([  1,  10, 100])'
assert repr(np.array([-1, 2])) == 'array([-1,  2])'
assert repr(np.arange(10)) == 'array([0, 1, 2, 3, 4, 5, 6, 7, 8, 9])'
assert repr(np.array([True, False])) == 'array([ True, False])'
assert str(np.array([True, False])) == '[ True False]'

# === floats ===
assert repr(np.array([1.5, 2.0, 3.0])) == 'array([1.5, 2. , 3. ])'
assert str(np.array([1.5, 2.0, 3.0])) == '[1.5 2.  3. ]'
assert repr(np.zeros(3)) == 'array([0., 0., 0.])'
assert repr(np.array([0.1, 0.25])) == 'array([0.1 , 0.25])'
assert repr(np.array([-1.5, 2.0])) == 'array([-1.5,  2. ])'
assert repr(np.array([1 / 3])) == 'array([0.33333333])'
assert repr(np.array([0.5, 100.0])) == 'array([  0.5, 100. ])'
assert repr(np.linspace(0, 1, 5)) == 'array([0.  , 0.25, 0.5 , 0.75, 1.  ])'
assert repr(np.array([1.0, np.nan])) == 'array([ 1., nan])'
assert repr(np.array([1.5, np.inf])) == 'array([1.5, inf])'
assert repr(np.array([np.nan, np.nan])) == 'array([nan, nan])'

# Scientific notation: a value of 1e8 or more, one under 1e-4, or a spread over 1000.
assert repr(np.array([1e-5, 1.0])) == 'array([1.e-05, 1.e+00])'
assert repr(np.array([1.5e-5, 1.0])) == 'array([1.5e-05, 1.0e+00])'
assert repr(np.array([1e10, 1.0])) == 'array([1.e+10, 1.e+00])'
assert repr(np.array([123456789.0])) == 'array([1.23456789e+08])'

# === more dimensions ===
assert repr(np.array([[1, 2], [3, 4]])) == 'array([[1, 2],\n       [3, 4]])'
assert str(np.array([[1, 2], [3, 4]])) == '[[1 2]\n [3 4]]'
assert repr(np.array([[1.5, 2.25], [3.0, 4.0]])) == 'array([[1.5 , 2.25],\n       [3.  , 4.  ]])'
assert repr(np.eye(2)) == 'array([[1., 0.],\n       [0., 1.]])'
assert repr(np.arange(8).reshape(2, 2, 2)) == (
    'array([[[0, 1],\n'
    '        [2, 3]],\n'
    '\n'
    '       [[4, 5],\n'
    '        [6, 7]]])'
)
assert str(np.arange(8).reshape(2, 2, 2)) == '[[[0 1]\n  [2 3]]\n\n [[4 5]\n  [6 7]]]'

# === zero dimensions and empty arrays ===
assert repr(np.array(5)) == 'array(5)'
assert repr(np.array(2.0)) == 'array(2.)'
assert str(np.array(5)) == '5'
assert repr(np.array([])) == 'array([], dtype=float64)'
assert repr(np.array([], dtype=int)) == 'array([], dtype=int64)'
assert repr(np.zeros((0, 3))) == 'array([], shape=(0, 3), dtype=float64)'
assert str(np.array([])) == '[]'

# === text and objects carry their dtype ===
assert repr(np.array(['a', 'bc'])) == "array(['a', 'bc'], dtype='<U2')"
assert str(np.array(['a', 'bc'])) == "['a' 'bc']"
assert repr(np.array([1, 'a', None], dtype=object)) == "array([1, 'a', None], dtype=object)"

# === wrapping and summarizing ===
assert repr(np.arange(2000)) == 'array([   0,    1,    2, ..., 1997, 1998, 1999])'
assert str(np.arange(2000)) == '[   0    1    2 ... 1997 1998 1999]'
assert repr(np.arange(30)) == (
    'array([ 0,  1,  2,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 14, 15, 16,\n'
    '       17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29])'
)

# === dtypes, and arrays inside other text ===
assert repr(np.dtype('float64')) == "dtype('float64')"
assert str(np.array([1]).dtype) == 'int64'
assert repr(np.array([1.0]).dtype) == "dtype('float64')"
assert f'{np.array([1, 2])}' == '[1 2]'
assert f'{np.array([2.5])[0]:.2f}' == '2.50'
assert str([np.array([1, 2])]) == '[array([1, 2])]'
