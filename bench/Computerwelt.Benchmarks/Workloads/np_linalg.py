import numpy as np
n = 120
a = np.arange(n * n, dtype=float).reshape(n, n) % 17.0 + np.eye(n) * 50.0
inv = np.linalg.inv(a)
x = np.linalg.solve(a, np.ones(n))
det = np.linalg.det(a[:20, :20])
