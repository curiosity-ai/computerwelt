import numpy as np
a = np.arange(1_000_000, dtype=float).reshape(1000, 1000) / 7.0
r = (a.sum(), a.mean(), a.std(), a.min(), a.max(), int(a.argmax()))
cols = a.sum(axis=0)
rows = a.mean(axis=1)
v = np.arange(1_000_000, dtype=float)
dot = float(v @ v)
n = float(np.linalg.norm(v))
