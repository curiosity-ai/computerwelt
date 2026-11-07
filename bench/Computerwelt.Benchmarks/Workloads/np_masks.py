import numpy as np
a = np.arange(1_000_000, dtype=float) % 1000.0
mask = (a > 250.0) & (a < 750.0)
picked = a[mask]
w = np.where(a > 500.0, a, -a)
count = int(np.count_nonzero(a == 3.0))
