# Extension: class inheritance (C3 MRO, super()), exception subclasses, property /
# classmethod / staticmethod, __getattr__, descriptors, __init_subclass__, built-in
# bases (dict/list/str), typing.NamedTuple / Generic classes, del obj.attr.
# Upstream Monty rejects bases and method decorators (limitations/classes.md).
class A:
    x = 1
    def __init__(self, v):
        self.v = v
    def f(self):
        return 'A.f'
    def who(self):
        return 'A'
class B(A):
    def __init__(self, v, w):
        super().__init__(v)
        self.w = w
    def who(self):
        return 'B+' + super().who()
b = B(1, 2)
assert b.f() == 'A.f' and b.who() == 'B+A' and b.v == 1 and b.w == 2 and b.x == 1
assert isinstance(b, A) and issubclass(B, A) and not issubclass(A, B)
assert [c.__name__ for c in B.__mro__] == ['B', 'A', 'object']
class C(A):
    def who(self): return 'C+' + super().who()
class D(B, C):
    def who(self): return 'D+' + super().who()
assert D(1, 2).who() == 'D+B+C+A', D(1,2).who()
assert [c.__name__ for c in D.__mro__] == ['D', 'B', 'C', 'A', 'object']

class E(ValueError):
    pass
try:
    raise E('bad')
except ValueError as e:
    assert type(e) is E and str(e) == 'bad' and isinstance(e, E) and e.args == ('bad',)
class F(E):
    def __init__(self, code, msg):
        super().__init__(msg)
        self.code = code
    def __str__(self):
        return f'[{self.code}] {self.args[0]}'
try:
    raise F(7, 'boom')
except E as e:
    assert e.code == 7 and str(e) == '[7] boom', str(e)
assert issubclass(F, Exception) and issubclass(F, E)
try:
    raise F
except TypeError as e:
    assert 'missing' in str(e), str(e)

class P:
    def __init__(self): self._x = 0
    @property
    def x(self): return self._x
    @x.setter
    def x(self, v): self._x = v * 2
    @classmethod
    def make(cls): return cls()
    @staticmethod
    def s(a): return a + 1
p = P.make(); p.x = 5
assert p.x == 10 and P.s(1) == 2 and p.s(2) == 3 and type(p) is P
class Q(P): pass
assert type(Q.make()) is Q
try:
    class R:
        @property
        def y(self): return 1
    R().y = 2
except AttributeError as e:
    assert "has no setter" in str(e), str(e)

class G:
    def __getattr__(self, name):
        if name.startswith('z'): return name * 2
        raise AttributeError(name)
g = G()
assert g.zz == 'zzzz' and hasattr(g, 'zq') and not hasattr(g, 'a') and getattr(g, 'a', 5) == 5

class MyDict(dict):
    def __missing__(self, key): return key.upper()
m = MyDict(a=1)
assert m['a'] == 1 and m['b'] == 'B' and isinstance(m, dict) and len(m) == 1 and list(m.keys()) == ['a']
class MyList(list):
    def total(self): return sum(self)
l = MyList([1,2,3]); l.append(4)
assert l.total() == 10 and l[0] == 1 and len(l) == 4 and l == [1,2,3,4]
class Up(str):
    def shout(self): return self.upper() + '!'
assert Up('hi').shout() == 'HI!' and Up('hi') == 'hi' and isinstance(Up('x'), str)
class Pt:
    def __init__(self, x): self.x = x
    def __eq__(self, o): return self.x == o.x
class Pt2(Pt):
    def __hash__(self): return hash(self.x)
assert hash(Pt2(3)) == 3
try:
    hash(Pt(1)); assert False
except TypeError: pass
del p._x
assert not hasattr(p, '_x')
class Mixin:
    def hello(self): return 'hi ' + self.name
class Named(Mixin):
    name = 'n'
assert Named().hello() == 'hi n'
class Desc:
    def __set_name__(self, owner, name): self.name = name
    def __get__(self, obj, owner): return 'got ' + self.name if obj is not None else self
class H:
    d = Desc()
assert H().d == 'got d' and isinstance(H.d, Desc)
class Base:
    subs = []
    def __init_subclass__(cls, tag=None, **kw):
        Base.subs.append((cls.__name__, tag))
class S1(Base, tag='one'): pass
assert Base.subs == [('S1', 'one')]
from typing import NamedTuple, Generic, TypeVar
T = TypeVar('T')
class Box(Generic[T]):
    def __init__(self, v): self.v = v
assert Box(3).v == 3
class NT(NamedTuple):
    a: int
    b: str = 'x'
    def both(self): return f'{self.a}{self.b}'
assert NT(1).both() == '1x' and NT(1, 'y') == (1, 'y')
