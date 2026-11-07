import numpy as np
a = np.arange(40000, dtype=float).reshape(200, 200) / 40000.0
b = a.T + 1.0
c = a @ b
d = np.dot(c, a)
