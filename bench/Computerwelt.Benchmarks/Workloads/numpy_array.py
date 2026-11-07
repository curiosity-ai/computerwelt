import numpy as np
a = np.arange(10000, dtype=float).reshape(100, 100)
b = (a * 2.0 + 1.0) / 3.0
m = b.mean(axis=0).sum() + (b[b > 50].std())
c = a @ a.T
