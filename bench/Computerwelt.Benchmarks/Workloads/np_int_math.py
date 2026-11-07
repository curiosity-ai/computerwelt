import numpy as np
a = np.arange(1_000_000)
b = a * 3 + 7
c = (b - a) * 2
s = int(c.sum())
m = int(b.max())
