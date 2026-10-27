"""operator: the standard operators as functions."""


def lt(a, b): return a < b
def le(a, b): return a <= b
def eq(a, b): return a == b
def ne(a, b): return a != b
def ge(a, b): return a >= b
def gt(a, b): return a > b
def not_(a): return not a
def truth(a): return True if a else False
def is_(a, b): return a is b
def is_not(a, b): return a is not b
_abs = abs


def abs(a): return _abs(a)
def add(a, b): return a + b
def and_(a, b): return a & b
def floordiv(a, b): return a // b
def index(a):
    if isinstance(a, int):
        return a
    raise TypeError(f"'{type(a).__name__}' object cannot be interpreted as an integer")
def inv(a): return ~a
invert = inv
def lshift(a, b): return a << b
def mod(a, b): return a % b
def mul(a, b): return a * b
def matmul(a, b): return a @ b
def neg(a): return -a
def or_(a, b): return a | b
def pos(a): return +a
def pow(a, b): return a ** b
def rshift(a, b): return a >> b
def sub(a, b): return a - b
def truediv(a, b): return a / b
def xor(a, b): return a ^ b
def concat(a, b):
    return a + b
def contains(a, b): return b in a
def countOf(a, b):
    count = 0
    for item in a:
        if item is b or item == b:
            count += 1
    return count
def delitem(a, b):
    del a[b]
def getitem(a, b): return a[b]
def indexOf(a, b):
    for i, item in enumerate(a):
        if item is b or item == b:
            return i
    raise ValueError('sequence.index(x): x not in sequence')
def setitem(a, b, c):
    a[b] = c
def length_hint(obj, default=0):
    try:
        return len(obj)
    except TypeError:
        return default


class attrgetter:
    """Callable that fetches the given attribute(s), dotted names included."""

    def __init__(self, attr, *attrs):
        if not attrs:
            if not isinstance(attr, str):
                raise TypeError('attribute name must be a string')
            self._attrs = (attr,)
            self._single = True
        else:
            self._attrs = (attr,) + attrs
            self._single = False

    def _resolve(self, obj, name):
        for part in name.split('.'):
            obj = getattr(obj, part)
        return obj

    def __call__(self, obj):
        if self._single:
            return self._resolve(obj, self._attrs[0])
        return tuple(self._resolve(obj, name) for name in self._attrs)

    def __repr__(self):
        return 'operator.attrgetter(' + ', '.join(repr(a) for a in self._attrs) + ')'


class itemgetter:
    """Callable that fetches the given item(s) from its operand."""

    def __init__(self, item, *items):
        if not items:
            self._items = (item,)
            self._single = True
        else:
            self._items = (item,) + items
            self._single = False

    def __call__(self, obj):
        if self._single:
            return obj[self._items[0]]
        return tuple(obj[i] for i in self._items)

    def __repr__(self):
        return 'operator.itemgetter(' + ', '.join(repr(i) for i in self._items) + ')'


class methodcaller:
    """Callable that calls the named method with the stored arguments."""

    def __init__(self, name, *args, **kwargs):
        if not isinstance(name, str):
            raise TypeError('method name must be a string')
        self._name = name
        self._args = args
        self._kwargs = kwargs

    def __call__(self, obj):
        return getattr(obj, self._name)(*self._args, **self._kwargs)

    def __repr__(self):
        parts = [repr(self._name)]
        parts.extend(repr(a) for a in self._args)
        parts.extend(f'{k}={v!r}' for k, v in self._kwargs.items())
        return 'operator.methodcaller(' + ', '.join(parts) + ')'


def iadd(a, b):
    a += b
    return a
def iand(a, b):
    a &= b
    return a
def ifloordiv(a, b):
    a //= b
    return a
def ilshift(a, b):
    a <<= b
    return a
def imod(a, b):
    a %= b
    return a
def imul(a, b):
    a *= b
    return a
def ior(a, b):
    a |= b
    return a
def ipow(a, b):
    a **= b
    return a
def irshift(a, b):
    a >>= b
    return a
def isub(a, b):
    a -= b
    return a
def itruediv(a, b):
    a /= b
    return a
def ixor(a, b):
    a ^= b
    return a
def iconcat(a, b):
    a += b
    return a

__lt__ = lt
__le__ = le
__eq__ = eq
__ne__ = ne
__ge__ = ge
__gt__ = gt
__not__ = not_
__abs__ = abs
__add__ = add
__and__ = and_
__floordiv__ = floordiv
__index__ = index
__inv__ = inv
__invert__ = invert
__lshift__ = lshift
__mod__ = mod
__mul__ = mul
__neg__ = neg
__or__ = or_
__pos__ = pos
__pow__ = pow
__rshift__ = rshift
__sub__ = sub
__truediv__ = truediv
__xor__ = xor
__contains__ = contains
__getitem__ = getitem
__setitem__ = setitem
__delitem__ = delitem
