# Extension: `itertools.groupby`, `starmap`, `tee`, `batched`, `combinations_with_replacement`,
# `collections.ChainMap` and `math.dist` — not in upstream Monty.
# Lazy where CPython is lazy. One difference: a `groupby` group is gathered when it is handed
# out, so a group read after the outer iterator moved on still has its items (see todo.md).
# Also pins `yield a, b`, which yields the tuple. Expected values produced by CPython 3.13.
import math
from itertools import groupby, starmap, tee, batched, combinations_with_replacement, count, islice
from collections import ChainMap

# === groupby ===
assert ([(k, list(g)) for k, g in groupby('AAAABBBCCDAABBB')]) == [('A', ['A', 'A', 'A', 'A']), ('B', ['B', 'B', 'B']), ('C', ['C', 'C']), ('D', ['D']), ('A', ['A', 'A']), ('B', ['B', 'B', 'B'])], "[(k, list(g)) for k, g in groupby('AAAABBBCCDAABBB')]"
assert ([k for k, _ in groupby('AAAABBBCCD')]) == ['A', 'B', 'C', 'D'], "[k for k, _ in groupby('AAAABBBCCD')]"
assert ([(k, list(g)) for k, g in groupby([1, 3, 5, 2, 4, 7], key=lambda n: n % 2)]) == [(1, [1, 3, 5]), (0, [2, 4]), (1, [7])], '[(k, list(g)) for k, g in groupby([1, 3, 5, 2, 4, 7], key=lambda n: n % 2)]'
assert ([(k, [w for w in g]) for k, g in groupby(sorted(['apple', 'avocado', 'banana', 'blueberry', 'cherry']), key=lambda w: w[0])]) == [('a', ['apple', 'avocado']), ('b', ['banana', 'blueberry']), ('c', ['cherry'])], "[(k, [w for w in g]) for k, g in groupby(sorted(['apple', 'avocado', 'banana', 'blueberry', 'cherry']), key=lambda w: w[0])]"
assert (list(groupby([]))) == [], 'list(groupby([]))'
assert ([(k, len(list(g))) for k, g in groupby(islice(count(), 10), key=lambda n: n // 4)]) == [(0, 4), (1, 4), (2, 2)], '[(k, len(list(g))) for k, g in groupby(islice(count(), 10), key=lambda n: n // 4)]'

# === starmap ===
assert (list(starmap(pow, [(2, 5), (3, 2), (10, 3)]))) == [32, 9, 1000], 'list(starmap(pow, [(2, 5), (3, 2), (10, 3)]))'
assert (list(starmap(lambda a, b: a + b, zip('abc', 'xyz')))) == ['ax', 'by', 'cz'], "list(starmap(lambda a, b: a + b, zip('abc', 'xyz')))"
assert (next(starmap(max, [(1, 9, 4)]))) == 9, 'next(starmap(max, [(1, 9, 4)]))'

# === tee ===
a, b = tee(iter([1, 2, 3, 4]))
assert (next(a), next(a)) == (1, 2), 'next(a), next(a)'
assert (list(b)) == [1, 2, 3, 4], 'list(b)'
assert (list(a)) == [3, 4], 'list(a)'
x, y, z = tee('xyz', 3)
assert (list(x) + list(y) + list(z)) == ['x', 'y', 'z', 'x', 'y', 'z', 'x', 'y', 'z'], 'list(x) + list(y) + list(z)'
assert (len(tee([1], 0))) == 0, 'len(tee([1], 0))'
first, second = tee(count())
assert ([next(first) for _ in range(5)], next(second)) == ([0, 1, 2, 3, 4], 0), '[next(first) for _ in range(5)], next(second)'

# === batched ===
assert (list(batched('ABCDEFG', 3))) == [('A', 'B', 'C'), ('D', 'E', 'F'), ('G',)], "list(batched('ABCDEFG', 3))"
assert (list(batched(range(6), 2))) == [(0, 1), (2, 3), (4, 5)], 'list(batched(range(6), 2))'
assert (list(batched([], 4))) == [], 'list(batched([], 4))'
assert (list(batched('AB', 5))) == [('A', 'B')], "list(batched('AB', 5))"
try:
    list(batched('ABC', 0))
    assert False, "list(batched('ABC', 0))"
except ValueError:
    pass
try:
    list(batched('ABCDE', 2, strict=True))
    assert False, "list(batched('ABCDE', 2, strict=True))"
except ValueError:
    pass
assert (list(islice(batched(count(), 2), 3))) == [(0, 1), (2, 3), (4, 5)], 'list(islice(batched(count(), 2), 3))'

# === combinations_with_replacement ===
assert (list(combinations_with_replacement('ABC', 2))) == [('A', 'A'), ('A', 'B'), ('A', 'C'), ('B', 'B'), ('B', 'C'), ('C', 'C')], "list(combinations_with_replacement('ABC', 2))"
assert (list(combinations_with_replacement(range(3), 3))) == [(0, 0, 0), (0, 0, 1), (0, 0, 2), (0, 1, 1), (0, 1, 2), (0, 2, 2), (1, 1, 1), (1, 1, 2), (1, 2, 2), (2, 2, 2)], 'list(combinations_with_replacement(range(3), 3))'
assert (list(combinations_with_replacement('AB', 0))) == [()], "list(combinations_with_replacement('AB', 0))"
assert (list(combinations_with_replacement('', 2))) == [], "list(combinations_with_replacement('', 2))"
assert (len(list(combinations_with_replacement(range(5), 3)))) == 35, 'len(list(combinations_with_replacement(range(5), 3)))'
try:
    list(combinations_with_replacement('AB', -1))
    assert False, "list(combinations_with_replacement('AB', -1))"
except ValueError:
    pass

# === yield of a bare tuple ===
def pairs():
    yield 1, 2
    yield 'a', 'b', 'c'
assert (list(pairs())) == [(1, 2), ('a', 'b', 'c')], 'list(pairs())'

# === ChainMap ===
defaults = {'color': 'red', 'user': 'guest', 'size': 10}
overrides = {'user': 'admin'}
cm = ChainMap(overrides, defaults)
assert (cm['user'], cm['color']) == ('admin', 'red'), "cm['user'], cm['color']"
assert (list(cm)) == ['color', 'user', 'size'], 'list(cm)'
assert (sorted(cm.items())) == [('color', 'red'), ('size', 10), ('user', 'admin')], 'sorted(cm.items())'
assert (list(cm.keys()), list(cm.values())) == (['color', 'user', 'size'], ['red', 'admin', 10]), 'list(cm.keys()), list(cm.values())'
assert (len(cm), 'size' in cm, 'missing' in cm) == (3, True, False), "len(cm), 'size' in cm, 'missing' in cm"
assert (cm.get('missing', 'n/a'), cm.get('size')) == ('n/a', 10), "cm.get('missing', 'n/a'), cm.get('size')"
assert repr(cm) == "ChainMap({'user': 'admin'}, {'color': 'red', 'user': 'guest', 'size': 10})", 'cm'
cm['color'] = 'blue'
assert (overrides, defaults['color']) == ({'user': 'admin', 'color': 'blue'}, 'red'), "overrides, defaults['color']"
try:
    cm['missing']
    assert False, "cm['missing']"
except KeyError:
    pass
child = cm.new_child({'user': 'root'})
assert (child['user'], child.parents['user'], len(child.maps)) == ('root', 'admin', 3), "child['user'], child.parents['user'], len(child.maps)"
assert repr(child.new_child()) == "ChainMap({}, {'user': 'root'}, {'user': 'admin', 'color': 'blue'}, {'color': 'red', 'user': 'guest', 'size': 10})", 'child.new_child()'
assert (dict(ChainMap({'a': 1}, {'a': 2, 'b': 3}))) == {'a': 1, 'b': 3}, "dict(ChainMap({'a': 1}, {'a': 2, 'b': 3}))"
assert (ChainMap({'a': 1}) == {'a': 1}) == True, "ChainMap({'a': 1}) == {'a': 1}"
assert (ChainMap({'a': 1}, {'b': 2}) == ChainMap({'b': 2, 'a': 1})) == True, "ChainMap({'a': 1}, {'b': 2}) == ChainMap({'b': 2, 'a': 1})"
assert (cm.pop('color'), overrides) == ('blue', {'user': 'admin'}), "cm.pop('color'), overrides"
try:
    ChainMap({}, {'a': 1}).pop('a')
    assert False, "ChainMap({}, {'a': 1}).pop('a')"
except KeyError:
    pass
del child['user']
assert (child['user']) == 'admin', "child['user']"
assert repr(ChainMap()) == 'ChainMap({})', 'ChainMap()'
assert (bool(ChainMap({}, {})), bool(ChainMap({}, {'a': 1}))) == (False, True), "bool(ChainMap({}, {})), bool(ChainMap({}, {'a': 1}))"
copy = cm.copy()
copy['new'] = 1
assert ('new' in cm, 'new' in copy) == (False, True), "'new' in cm, 'new' in copy"
assert (cm.setdefault('user', 'x'), cm.setdefault('fresh', 'y'), overrides) == ('admin', 'y', {'user': 'admin', 'fresh': 'y'}), "cm.setdefault('user', 'x'), cm.setdefault('fresh', 'y'), overrides"
cm.update({'z': 26})
assert (overrides['z']) == 26, "overrides['z']"
assert (cm.maps[0] is overrides) == True, 'cm.maps[0] is overrides'

# === math.dist ===
assert (math.dist((0, 0), (3, 4))) == 5.0, 'math.dist((0, 0), (3, 4))'
assert (math.dist([1, 2, 3], [4, 5, 6])) == 5.196152422706632, 'math.dist([1, 2, 3], [4, 5, 6])'
assert (math.dist((1.5,), (-2.25,))) == 3.75, 'math.dist((1.5,), (-2.25,))'
assert (math.dist((), ())) == 0.0, 'math.dist((), ())'
assert (math.dist((0.1, 0.2, 0.3), (0.4, 0.5, 0.6))) == 0.5196152422706632, 'math.dist((0.1, 0.2, 0.3), (0.4, 0.5, 0.6))'
assert (math.dist((1e200, 1e200), (0, 0))) == 1.414213562373095e+200, 'math.dist((1e200, 1e200), (0, 0))'
assert (math.dist((1e-200, 0), (0, 1e-200))) == 1.414213562373095e-200, 'math.dist((1e-200, 0), (0, 1e-200))'
try:
    math.dist((1, 2), (1, 2, 3))
    assert False, 'math.dist((1, 2), (1, 2, 3))'
except ValueError:
    pass
try:
    math.dist(1, 2)
    assert False, 'math.dist(1, 2)'
except TypeError:
    pass
