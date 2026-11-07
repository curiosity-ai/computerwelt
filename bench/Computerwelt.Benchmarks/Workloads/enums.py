from enum import Enum, auto
class Color(Enum):
    RED = auto()
    GREEN = auto()
    BLUE = auto()
counts = {}
for i in range(1500):
    c = Color((i % 3) + 1)
    counts[c] = counts.get(c, 0) + 1
