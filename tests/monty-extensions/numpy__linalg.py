# Extension: `numpy` — matrix products and `numpy.linalg`. Upstream Monty has no numpy.
# numpy calls LAPACK; the port uses textbook managed numerics, so results agree to rounding
# and are checked with `allclose` wherever the last digit is LAPACK's business. Eigenvector
# signs are arbitrary in both, so eigenpairs are checked by `A @ v == w * v`.
import numpy as np

A = np.array([[1, 2], [3, 4]])

# === products ===
assert np.dot([1, 2, 3], [4, 5, 6]) == 32
assert (A @ A).tolist() == [[7, 10], [15, 22]]
assert A.dot(A).tolist() == [[7, 10], [15, 22]]
assert np.matmul(A, [1, 1]).tolist() == [3, 7]
assert ([1, 1] @ A).tolist() == [4, 6]
assert np.dot(A, np.eye(2)).tolist() == [[1.0, 2.0], [3.0, 4.0]]
assert np.dot(2, A).tolist() == [[2, 4], [6, 8]]
assert (np.ones((2, 3)) @ np.ones((3, 4))).shape == (2, 4)
assert (np.ones((5, 2, 3)) @ np.ones((3, 4))).shape == (5, 2, 4)
assert np.dot(np.ones((2, 3)), np.ones(3)).tolist() == [3.0, 3.0]
assert np.vdot([1, 2], [3, 4]) == 11
assert np.trace(A) == 5 and A.trace() == 5
try:
    np.ones((2, 3)) @ np.ones((2, 3))
    assert False
except ValueError as e:
    assert str(e).startswith('matmul: Input operand 1 has a mismatch in its core dimension 0'), str(e)
try:
    np.dot([1, 2, 3], [1, 2])
    assert False
except ValueError as e:
    assert str(e) == 'shapes (3,) and (2,) not aligned: 3 (dim 0) != 2 (dim 0)', str(e)

# === determinant, inverse, solve ===
assert abs(np.linalg.det(A) - -2.0) < 1e-12
assert abs(np.linalg.det(np.eye(3)) - 1.0) < 1e-12
assert np.linalg.det([[1, 2], [2, 4]]) == 0.0
inverse = np.linalg.inv(A)
assert np.allclose(inverse, [[-2.0, 1.0], [1.5, -0.5]])
assert np.allclose(A @ inverse, np.eye(2))
assert np.allclose(np.linalg.solve([[3, 1], [1, 2]], [9, 8]), [2.0, 3.0])
assert np.linalg.solve([[3, 1], [1, 2]], [[9, 1], [8, 2]]).shape == (2, 2)
try:
    np.linalg.inv([[1, 2], [2, 4]])
    assert False
except np.linalg.LinAlgError as e:
    assert str(e) == 'Singular matrix'

# === norms and rank ===
assert np.linalg.norm([3, 4]) == 5.0
assert np.linalg.norm([3, 4], 1) == 7.0
assert np.linalg.norm([1, -2], np.inf) == 2.0
assert abs(np.linalg.norm(A) - 30 ** 0.5) < 1e-12
assert np.linalg.norm(A, 'fro') == np.linalg.norm(A)
assert np.linalg.norm(A, 1) == 6.0
assert np.linalg.norm(A, np.inf) == 7.0
assert np.linalg.norm([[3, 4], [6, 8]], axis=1).tolist() == [5.0, 10.0]
assert np.linalg.matrix_rank([[1, 2], [2, 4]]) == 1
assert np.linalg.matrix_rank(np.eye(3)) == 3

# === symmetric eigenproblems and Cholesky ===
S = np.array([[2.0, 1.0], [1.0, 2.0]])
w, v = np.linalg.eigh(S)
assert np.allclose(w, [1.0, 3.0])
assert np.allclose(S @ v, v * w)
assert np.allclose(v.T @ v, np.eye(2))
assert np.allclose(np.linalg.eigvalsh([[4, 0], [0, 9]]), [4, 9])
L = np.linalg.cholesky([[4, 2], [2, 3]])
assert np.allclose(L, [[2, 0], [1, 2 ** 0.5]])
assert np.allclose(L @ L.T, [[4, 2], [2, 3]])
try:
    np.linalg.cholesky([[1, 2], [2, 1]])
    assert False
except np.linalg.LinAlgError:
    pass

# === least squares and polynomials ===
solution, residuals, rank, singular = np.linalg.lstsq([[0, 1], [1, 1], [2, 1]], [1, 3, 5], rcond=None)
assert np.allclose(solution, [2.0, 1.0]) and rank == 2 and len(singular) == 2
assert np.allclose(np.polyfit([0, 1, 2], [1, 3, 5], 1), [2.0, 1.0])
assert np.allclose(np.polyfit([-1, 0, 1, 2], [1, 0, 1, 4], 2), [1.0, 0.0, 0.0])
assert np.polyval([2, 1], 3) == 7
assert np.polyval([1, 0, -1], [0, 1, 2]).tolist() == [-1, 0, 3]

# === submodule import forms ===
import numpy.linalg
from numpy.linalg import norm, inv
assert norm([6, 8]) == 10.0
assert numpy.linalg is np.linalg
