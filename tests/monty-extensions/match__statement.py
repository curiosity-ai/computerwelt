# Extension: the match statement (PEP 634) — literal, capture, wildcard, value,
# sequence, mapping, class, or- and as-patterns, guards. Upstream Monty rejects
# `match` at parse time (limitations/language.md). `match` stays a soft keyword.
def classify(v):
    match v:
        case 0 | 1:
            return 'bit'
        case int(n) if n < 0:
            return f'neg {n}'
        case int():
            return 'int'
        case str() as s:
            return 'str ' + s
        case [x, y]:
            return f'pair {x},{y}'
        case [first, *rest]:
            return f'seq {first} {rest}'
        case {'kind': 'pt', 'x': x, **others}:
            return f'pt {x} {others}'
        case None:
            return 'none'
        case _:
            return 'other'
assert classify(1) == 'bit' and classify(-3) == 'neg -3' and classify(9) == 'int'
assert classify('a') == 'str a' and classify([1, 2]) == 'pair 1,2' and classify((1, 2, 3)) == 'seq 1 [2, 3]'
assert classify({'kind': 'pt', 'x': 5, 'y': 6}) == "pt 5 {'y': 6}" and classify(None) == 'none' and classify(2.5) == 'other'
from dataclasses import dataclass
@dataclass
class Point:
    x: int
    y: int
class Color:
    RED = 'red'
def where(p):
    match p:
        case Point(x=0, y=0): return 'origin'
        case Point(0, y): return f'y-axis {y}'
        case Point(x, y) if x == y: return 'diag'
        case Point(): return 'point'
        case Color.RED: return 'red!'
    return 'nope'
assert where(Point(0, 0)) == 'origin' and where(Point(0, 4)) == 'y-axis 4' and where(Point(2, 2)) == 'diag'
assert where(Point(1, 2)) == 'point' and where('red') == 'red!' and where(3) == 'nope'
match (1, 'a'):
    case (1, 'b'): r = 1
    case (1, a) | (2, a): r = a
assert r == 'a'
match = [1]
assert match == [1]
for item in [[1, [2, 3]], 'x']:
    match item:
        case [a, [b, *c]]:
            assert (a, b, c) == (1, 2, [3])
        case _:
            break
