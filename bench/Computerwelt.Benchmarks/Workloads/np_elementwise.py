import numpy as np
a = np.arange(1_000_000, dtype=float)
b = a * 2.5 + 1.0
c = np.sqrt(b) * np.sin(a) - b / 3.0
d = np.exp(-a / 500000.0) + np.abs(c)
e = np.maximum(c, 0.0)
