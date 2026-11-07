# Extension: `pprint` — not in upstream Monty.
# CPython 3.13's layout algorithm: where a container breaks, how a long string is split,
# what `compact` packs onto a line. Expected values produced by CPython 3.13.
import pprint
import dataclasses
from collections import defaultdict, Counter, deque
from io import StringIO

# === scalars and short containers stay on one line ===
assert (pprint.pformat(42)) == '42', 'pprint.pformat(42)'
assert (pprint.pformat('text')) == "'text'", "pprint.pformat('text')"
assert (pprint.pformat([1, 2, 3])) == '[1, 2, 3]', 'pprint.pformat([1, 2, 3])'
assert (pprint.pformat({'b': 1, 'a': 2})) == "{'a': 2, 'b': 1}", "pprint.pformat({'b': 1, 'a': 2})"
assert (pprint.pformat({'b': 1, 'a': 2}, sort_dicts=False)) == "{'b': 1, 'a': 2}", "pprint.pformat({'b': 1, 'a': 2}, sort_dicts=False)"
assert (pprint.pformat((1,))) == '(1,)', 'pprint.pformat((1,))'
assert (pprint.pformat(set())) == 'set()', 'pprint.pformat(set())'
assert (pprint.pformat({1, 2, 3})) == '{1, 2, 3}', 'pprint.pformat({1, 2, 3})'
assert (pprint.pformat(frozenset({1, 2, 3}))) == 'frozenset({1, 2, 3})', 'pprint.pformat(frozenset({1, 2, 3}))'
assert (pprint.pformat(None)) == 'None', 'pprint.pformat(None)'
assert (pprint.pformat(1234567, underscore_numbers=True)) == '1_234_567', 'pprint.pformat(1234567, underscore_numbers=True)'

# === nested data breaks at the width ===
data = {'name': 'computerwelt', 'tags': ['sandbox', 'python', 'bash', 'vfs'],
        'limits': {'instructions': 1000000, 'depth': 64, 'timeout': 30.5},
        'matrix': [[1, 2, 3], [4, 5, 6], [7, 8, 9]], 'flags': (True, False, None)}
assert (pprint.pformat(data)) == "{'flags': (True, False, None),\n 'limits': {'depth': 64, 'instructions': 1000000, 'timeout': 30.5},\n 'matrix': [[1, 2, 3], [4, 5, 6], [7, 8, 9]],\n 'name': 'computerwelt',\n 'tags': ['sandbox', 'python', 'bash', 'vfs']}", 'pprint.pformat(data)'
assert (pprint.pformat(data, width=40)) == "{'flags': (True, False, None),\n 'limits': {'depth': 64,\n            'instructions': 1000000,\n            'timeout': 30.5},\n 'matrix': [[1, 2, 3],\n            [4, 5, 6],\n            [7, 8, 9]],\n 'name': 'computerwelt',\n 'tags': ['sandbox',\n          'python',\n          'bash',\n          'vfs']}", 'pprint.pformat(data, width=40)'
assert (pprint.pformat(data, indent=4, width=50)) == "{   'flags': (True, False, None),\n    'limits': {   'depth': 64,\n                  'instructions': 1000000,\n                  'timeout': 30.5},\n    'matrix': [[1, 2, 3], [4, 5, 6], [7, 8, 9]],\n    'name': 'computerwelt',\n    'tags': ['sandbox', 'python', 'bash', 'vfs']}", 'pprint.pformat(data, indent=4, width=50)'
assert (pprint.pformat(data, depth=1)) == "{'flags': (...),\n 'limits': {...},\n 'matrix': [...],\n 'name': 'computerwelt',\n 'tags': [...]}", 'pprint.pformat(data, depth=1)'
assert (pprint.pformat(data, depth=2, width=30)) == "{'flags': (True, False, None),\n 'limits': {'depth': 64,\n            'instructions': 1000000,\n            'timeout': 30.5},\n 'matrix': [[...],\n            [...],\n            [...]],\n 'name': 'computerwelt',\n 'tags': ['sandbox',\n          'python',\n          'bash',\n          'vfs']}", 'pprint.pformat(data, depth=2, width=30)'
assert (pprint.pformat(data, sort_dicts=False, width=60)) == "{'name': 'computerwelt',\n 'tags': ['sandbox', 'python', 'bash', 'vfs'],\n 'limits': {'instructions': 1000000,\n            'depth': 64,\n            'timeout': 30.5},\n 'matrix': [[1, 2, 3], [4, 5, 6], [7, 8, 9]],\n 'flags': (True, False, None)}", 'pprint.pformat(data, sort_dicts=False, width=60)'
assert (pprint.pformat(list(range(40)), width=40)) == '[0,\n 1,\n 2,\n 3,\n 4,\n 5,\n 6,\n 7,\n 8,\n 9,\n 10,\n 11,\n 12,\n 13,\n 14,\n 15,\n 16,\n 17,\n 18,\n 19,\n 20,\n 21,\n 22,\n 23,\n 24,\n 25,\n 26,\n 27,\n 28,\n 29,\n 30,\n 31,\n 32,\n 33,\n 34,\n 35,\n 36,\n 37,\n 38,\n 39]', 'pprint.pformat(list(range(40)), width=40)'
assert (pprint.pformat(list(range(40)), width=40, compact=True)) == '[0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11,\n 12, 13, 14, 15, 16, 17, 18, 19, 20, 21,\n 22, 23, 24, 25, 26, 27, 28, 29, 30, 31,\n 32, 33, 34, 35, 36, 37, 38, 39]', 'pprint.pformat(list(range(40)), width=40, compact=True)'
assert (pprint.pformat([['a' * 10] * 3] * 3, width=30, compact=True)) == "[['aaaaaaaaaa', 'aaaaaaaaaa',\n  'aaaaaaaaaa'],\n ['aaaaaaaaaa', 'aaaaaaaaaa',\n  'aaaaaaaaaa'],\n ['aaaaaaaaaa', 'aaaaaaaaaa',\n  'aaaaaaaaaa']]", "pprint.pformat([['a' * 10] * 3] * 3, width=30, compact=True)"
assert (pprint.pformat(tuple(range(12)), width=20)) == '(0,\n 1,\n 2,\n 3,\n 4,\n 5,\n 6,\n 7,\n 8,\n 9,\n 10,\n 11)', 'pprint.pformat(tuple(range(12)), width=20)'
assert (pprint.pformat({1: [1, 2], 'x': 'y', (1, 2): None}, width=10)) == "{1: [1,\n     2],\n 'x': 'y',\n (1, 2): None}", "pprint.pformat({1: [1, 2], 'x': 'y', (1, 2): None}, width=10)"
assert (pprint.pformat(set(range(30)), width=40)) == '{0,\n 1,\n 2,\n 3,\n 4,\n 5,\n 6,\n 7,\n 8,\n 9,\n 10,\n 11,\n 12,\n 13,\n 14,\n 15,\n 16,\n 17,\n 18,\n 19,\n 20,\n 21,\n 22,\n 23,\n 24,\n 25,\n 26,\n 27,\n 28,\n 29}', 'pprint.pformat(set(range(30)), width=40)'
assert (pprint.pformat(frozenset(range(15)), width=30)) == 'frozenset({0,\n           1,\n           2,\n           3,\n           4,\n           5,\n           6,\n           7,\n           8,\n           9,\n           10,\n           11,\n           12,\n           13,\n           14})', 'pprint.pformat(frozenset(range(15)), width=30)'

# === long strings and bytes are split ===
text = 'The quick brown fox jumps over the lazy dog. ' * 4
assert (pprint.pformat(text)) == "('The quick brown fox jumps over the lazy dog. The quick brown fox jumps over '\n 'the lazy dog. The quick brown fox jumps over the lazy dog. The quick brown '\n 'fox jumps over the lazy dog. ')", 'pprint.pformat(text)'
assert (pprint.pformat([text], width=50)) == "['The quick brown fox jumps over the lazy dog. '\n 'The quick brown fox jumps over the lazy dog. '\n 'The quick brown fox jumps over the lazy dog. '\n 'The quick brown fox jumps over the lazy dog. ']", 'pprint.pformat([text], width=50)'
assert (pprint.pformat({'k': 'line one\nline two\nline three is a good deal longer than the others'}, width=40)) == "{'k': 'line one\\n'\n      'line two\\n'\n      'line three is a good deal '\n      'longer than the others'}", "pprint.pformat({'k': 'line one\\nline two\\nline three is a good deal longer than the others'}, width=40)"
assert (pprint.pformat(b'0123456789' * 10, width=40)) == "(b'012345678901234567890123456789012345'\n b'678901234567890123456789012345678901'\n b'2345678901234567890123456789')", "pprint.pformat(b'0123456789' * 10, width=40)"
assert (pprint.pformat(b'abc')) == "b'abc'", "pprint.pformat(b'abc')"

# === collections ===
dd = defaultdict(list, {'alpha': [1, 2, 3, 4, 5], 'beta': [6, 7, 8, 9, 10], 'gamma': list(range(10))})
assert (pprint.pformat(dd, width=40)) == "defaultdict(<class 'list'>,\n            {'alpha': [1, 2, 3, 4, 5],\n             'beta': [6, 7, 8, 9, 10],\n             'gamma': [0,\n                       1,\n                       2,\n                       3,\n                       4,\n                       5,\n                       6,\n                       7,\n                       8,\n                       9]})", 'pprint.pformat(dd, width=40)'
assert (pprint.pformat(Counter('abracadabra' * 3), width=30)) == "Counter({'a': 15,\n         'b': 6,\n         'r': 6,\n         'c': 3,\n         'd': 3})", "pprint.pformat(Counter('abracadabra' * 3), width=30)"
assert (pprint.pformat(deque(range(20)), width=30)) == 'deque([0,\n       1,\n       2,\n       3,\n       4,\n       5,\n       6,\n       7,\n       8,\n       9,\n       10,\n       11,\n       12,\n       13,\n       14,\n       15,\n       16,\n       17,\n       18,\n       19])', 'pprint.pformat(deque(range(20)), width=30)'
assert (pprint.pformat(deque(range(20), maxlen=25), width=30)) == 'deque([0,\n       1,\n       2,\n       3,\n       4,\n       5,\n       6,\n       7,\n       8,\n       9,\n       10,\n       11,\n       12,\n       13,\n       14,\n       15,\n       16,\n       17,\n       18,\n       19],\n      maxlen=25)', 'pprint.pformat(deque(range(20), maxlen=25), width=30)'
assert (pprint.pformat(defaultdict(int))) == "defaultdict(<class 'int'>, {})", 'pprint.pformat(defaultdict(int))'

# === dataclasses ===
@dataclasses.dataclass
class Point:
    x: int
    y: int
    label: str = 'origin of the coordinate system'

assert (pprint.pformat(Point(1, 2))) == "Point(x=1, y=2, label='origin of the coordinate system')", 'pprint.pformat(Point(1, 2))'
assert (pprint.pformat([Point(1, 2), Point(3, 4, 'a second point with a long label')], width=60)) == "[Point(x=1, y=2, label='origin of the coordinate system'),\n Point(x=3, y=4, label='a second point with a long label')]", "pprint.pformat([Point(1, 2), Point(3, 4, 'a second point with a long label')], width=60)"

# === recursion, saferepr, isreadable ===
loop = [1, 2]
loop.append(loop)
assert (pprint.isrecursive(loop)) == True, 'pprint.isrecursive(loop)'
assert (pprint.isreadable(loop)) == False, 'pprint.isreadable(loop)'
assert (pprint.isreadable({'a': [1, 2]})) == True, "pprint.isreadable({'a': [1, 2]})"
assert (pprint.saferepr({'b': [1, 2], 'a': (3,)})) == "{'a': (3,), 'b': [1, 2]}", "pprint.saferepr({'b': [1, 2], 'a': (3,)})"
assert (pprint.isreadable(len)) == False, 'pprint.isreadable(len)'

# === pprint, pp and PrettyPrinter write to a stream ===
out = StringIO()
pprint.pprint({'z': 1, 'a': [1, 2]}, stream=out)
pprint.pp({'z': 1, 'a': [1, 2]}, stream=out)
pprint.PrettyPrinter(width=20, stream=out).pprint(list(range(10)))
assert (out.getvalue()) == "{'a': [1, 2], 'z': 1}\n{'z': 1, 'a': [1, 2]}\n[0,\n 1,\n 2,\n 3,\n 4,\n 5,\n 6,\n 7,\n 8,\n 9]\n", 'out.getvalue()'
printer = pprint.PrettyPrinter(indent=2, width=30)
assert (printer.pformat({'key': list(range(10))})) == "{ 'key': [ 0,\n           1,\n           2,\n           3,\n           4,\n           5,\n           6,\n           7,\n           8,\n           9]}", "printer.pformat({'key': list(range(10))})"
assert (printer.isreadable([1, 'a'])) == True, "printer.isreadable([1, 'a'])"
try:
    pprint.PrettyPrinter(indent=-1)
    assert False, 'pprint.PrettyPrinter(indent=-1)'
except ValueError:
    pass
try:
    pprint.PrettyPrinter(width=0)
    assert False, 'pprint.PrettyPrinter(width=0)'
except ValueError:
    pass
try:
    pprint.PrettyPrinter(depth=0)
    assert False, 'pprint.PrettyPrinter(depth=0)'
except ValueError:
    pass
