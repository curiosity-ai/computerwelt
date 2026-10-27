# Extension: the `operator` module. Upstream Monty has none. Expected output is CPython 3.13's.
_lines = []

def _log(*parts):
    _lines.append(' '.join(str(p) for p in parts))


import operator
from operator import itemgetter, attrgetter, methodcaller
_log(operator.add(1, 2), operator.mul('a', 3), operator.sub(5, 3), operator.truediv(1, 4), operator.floordiv(7, 2), operator.mod(7, 3), operator.pow(2, 8))
_log(operator.neg(3), operator.pos(-3), operator.abs(-4), operator.inv(5), operator.not_(0), operator.truth([]), operator.and_(6, 3), operator.or_(6, 3), operator.xor(6, 3), operator.lshift(1, 4), operator.rshift(16, 2))
_log(operator.eq(1, 1), operator.ne(1, 1), operator.lt(1, 2), operator.le(2, 2), operator.gt(1, 2), operator.ge(3, 2), operator.is_(None, None), operator.is_not(1, None))
_log(operator.contains([1, 2], 2), operator.getitem([5, 6], 1), operator.countOf([1, 2, 1], 1), operator.indexOf([4, 5], 5), operator.concat([1], [2]), operator.index(5))
l = [1, 2, 3]; operator.setitem(l, 0, 9); operator.delitem(l, 1); _log(l)
_log(operator.iadd([1], [2]), operator.imul(3, 4), operator.length_hint([1, 2]), operator.length_hint(5, 7))
_log(itemgetter(1)([5, 6, 7]), itemgetter(0, 2)([5, 6, 7]), itemgetter('a')({'a': 1}))
class P:
    def __init__(self, n, inner=None): self.n = n; self.inner = inner
    def double(self, k=2): return self.n * k
p = P(3, P(4))
_log(attrgetter('n')(p), attrgetter('n', 'inner.n')(p), methodcaller('double')(p), methodcaller('double', k=5)(p))
_log(sorted([(2, 'b'), (1, 'z'), (1, 'a')], key=itemgetter(0, 1)))
_log(sorted(['bb', 'a', 'ccc'], key=methodcaller('count', 'c')))
_log(max({'x': 3, 'y': 9}.items(), key=itemgetter(1)))
_log(itemgetter(1), attrgetter('a.b'), methodcaller('f', 1, z=2))
from functools import reduce
_log(reduce(operator.mul, range(1, 6)))
try: operator.index('a')
except TypeError as e: _log(e)
try: operator.itemgetter(5)([1])
except IndexError as e: _log(e)


# === compare against what CPython printed ===
EXPECTED = ['3 aaa 2 0.25 3 1 256', '-3 -3 4 -6 True False 2 7 5 16 4', 'True False True True False True True True', 'True 6 2 1 [1, 2] 5', '[9, 3]', '[1, 2] 12 2 7', '6 (5, 7) 1', '3 (3, 4) 6 15', "[(1, 'a'), (1, 'z'), (2, 'b')]", "['bb', 'a', 'ccc']", "('y', 9)", "operator.itemgetter(1) operator.attrgetter('a.b') operator.methodcaller('f', 1, z=2)", '120', "'str' object cannot be interpreted as an integer", 'list index out of range']

assert len(_lines) == len(EXPECTED), (len(_lines), len(EXPECTED))
for got, want in zip(_lines, EXPECTED):
    assert got == want, (got, want)
