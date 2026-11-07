# Extension: `numpy` — indexing, slicing, views and assignment. Upstream Monty has no numpy.
# Basic indexing (integers, slices, `...`, `np.newaxis`) returns a view that shares memory
# with the original, as numpy's does; masks and index arrays return copies. Expected values
# are numpy 2's.
import numpy as np

a = np.arange(12).reshape(3, 4)

# === integers and slices ===
assert a[1].tolist() == [4, 5, 6, 7]
assert a[1, 2] == 6 and a[1][2] == 6
assert a[-1, -1] == 11
assert a[:, 1].tolist() == [1, 5, 9]
assert a[1:, ::2].tolist() == [[4, 6], [8, 10]]
assert a[::-1, 0].tolist() == [8, 4, 0]
assert a[0, ::-1].tolist() == [3, 2, 1, 0]
assert a[5:].tolist() == [] and a[5:].shape == (0, 4)
assert a[..., 0].tolist() == [0, 4, 8]
assert a[1, ...].tolist() == [4, 5, 6, 7]
assert a[:, np.newaxis].shape == (3, 1, 4)
assert a[None].shape == (1, 3, 4)
assert a[:, :, None].shape == (3, 4, 1)

# === masks ===
assert a[a > 5].tolist() == [6, 7, 8, 9, 10, 11]
assert a[(a % 2 == 0) & (a < 6)].tolist() == [0, 2, 4]
assert a[np.array([True, False, True])].tolist() == [[0, 1, 2, 3], [8, 9, 10, 11]]

# === index arrays ===
assert a[[0, 2]].tolist() == [[0, 1, 2, 3], [8, 9, 10, 11]]
assert a[[0, 1], [1, 2]].tolist() == [1, 6]
assert a[[-1]].tolist() == [[8, 9, 10, 11]]
assert a[:, [0, 3]].tolist() == [[0, 3], [4, 7], [8, 11]]
assert a[np.array([[0, 1], [1, 2]])].shape == (2, 2, 4)
v = np.array([10, 20, 30, 40])
assert v[[3, 0, 0]].tolist() == [40, 10, 10]

# Advanced indices separated by a slice put their dimension first — numpy's rule.
b = np.arange(24).reshape(2, 3, 4)
assert b[0, :, [1, 2]].shape == (2, 3)
assert b[:, [0, 1]].shape == (2, 2, 4)
assert b[[0, 1], :, [0, 1]].shape == (2, 3)

# === views share memory ===
row = a[0]
row[0] = 100
assert a[0, 0] == 100
evens = v[::2]
evens[:] = 0
assert v.tolist() == [0, 20, 0, 40]
t = a.T
t[0, 1] = -4
assert a[1, 0] == -4
r = np.arange(6)
r.reshape(2, 3)[1, 1] = 99
assert r[4] == 99
# ...and advanced indexing copies.
copied = a[[0]]
copied[0, 0] = -5
assert a[0, 0] == 100

# === assignment, with broadcasting ===
z = np.zeros((2, 3))
z[:] = [1, 2, 3]
z[:, 0] = 9
assert z.tolist() == [[9.0, 2.0, 3.0], [9.0, 2.0, 3.0]]
z[z > 2] = -1
assert z.tolist() == [[-1.0, 2.0, -1.0], [-1.0, 2.0, -1.0]]
q = np.arange(5)
q[q % 2 == 0] = [10, 20, 30]
assert q.tolist() == [10, 1, 20, 3, 30]
q[[1, 3]] = 0
assert q.tolist() == [10, 0, 20, 0, 30]
q[1:4] = 7
assert q.tolist() == [10, 7, 7, 7, 30]
q[-1] = 9.7          # an int array truncates what is stored into it
assert q[-1] == 9
m = np.zeros((3, 3), dtype=int)
m[1] = 5
m[:, 2] = [1, 2, 3]
assert m.tolist() == [[0, 0, 1], [5, 5, 2], [0, 0, 3]]
s = np.arange(5)
s[1:] = s[:-1]       # overlapping source and target
assert s.tolist() == [0, 0, 1, 2, 3]
f = np.zeros(4)
f.fill(2.5)
assert f.tolist() == [2.5, 2.5, 2.5, 2.5]

# === iteration and conversion ===
assert [r.tolist() for r in np.arange(4).reshape(2, 2)] == [[0, 1], [2, 3]]
assert list(np.array([3, 1, 2])) == [3, 1, 2]
assert sorted(np.array([3, 1, 2])) == [1, 2, 3]
assert sum(np.array([1, 2, 3])) == 6
assert max(np.array([1, 5, 2])) == 5
assert 3 in np.array([1, 2, 3]) and 7 not in np.array([1, 2, 3])
assert list(np.arange(4).reshape(2, 2).flat) == [0, 1, 2, 3]
assert np.arange(12).reshape(3, 4).flat[5] == 5
assert np.arange(6).reshape(2, 3).item(4) == 4
assert np.array([[7]]).item() == 7
assert float(np.array([2.5])) == 2.5 and int(np.array([3])) == 3
assert bool(np.array([0])) is False
assert np.arange(6).reshape(2, 3).tolist() == [[0, 1, 2], [3, 4, 5]]

# === errors ===
try:
    np.arange(3)[3]
    assert False
except IndexError as e:
    assert str(e) == 'index 3 is out of bounds for axis 0 with size 3', str(e)

try:
    np.arange(3)[0, 0]
    assert False
except IndexError as e:
    assert str(e) == 'too many indices for array: array is 1-dimensional, but 2 were indexed', str(e)

try:
    np.arange(3)[np.array([True, False])]
    assert False
except IndexError:
    pass

try:
    np.arange(3)[1.5]
    assert False
except IndexError:
    pass

try:
    bool(np.array([1, 2]))
    assert False
except ValueError as e:
    assert str(e) == 'The truth value of an array with more than one element is ambiguous. Use a.any() or a.all()'

try:
    len(np.array(5))
    assert False
except TypeError as e:
    assert str(e) == 'len() of unsized object'

try:
    z[:] = [1, 2]
    assert False
except ValueError as e:
    assert str(e) == 'could not broadcast input array from shape (2,) into shape (2,3)', str(e)
