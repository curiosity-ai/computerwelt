# Extension: the `functools` module (reduce, partial, wraps, lru_cache, cache, cmp_to_key).
# Upstream Monty has no `functools`. Written in Python (src/.../Modules/Source/functools.py); every
# expected line below was produced by CPython 3.13 running this same code.
_lines = []

def _log(*parts):
    _lines.append(' '.join(str(p) for p in parts))


import functools
from functools import partial, reduce, wraps, lru_cache, cmp_to_key, cache
_log(reduce(lambda a, b: a + b, [1, 2, 3, 4]), reduce(lambda a, b: a * b, [], 5))
try: reduce(lambda a, b: a, [])
except TypeError as e: _log(e)
p = partial(int, base=2)
_log(p('101'), p.func is int, p.args, p.keywords)
add = lambda a, b, c=0: a + b + c
q = partial(add, 1); _log(q(2), q(2, c=5), partial(q, 10)(0))
def deco(f):
    @wraps(f)
    def w(*a, **k): return f(*a, **k)
    return w
@deco
def hello(x):
    "doc here"
    return x
_log(hello.__name__, hello.__doc__, hello.__wrapped__(3), hello(4))
calls = []
@lru_cache(maxsize=2)
def sq(n):
    calls.append(n); return n * n
_log(sq(2), sq(2), sq(3), sq(4), sq(2), calls, sq.cache_info())
sq.cache_clear(); _log(sq.cache_info())
@lru_cache
def fib(n): return n if n < 2 else fib(n-1) + fib(n-2)
_log(fib(60), fib.cache_info())
@cache
def tw(x, y=1): return x + y
_log(tw(1), tw(1, y=2), tw(1), tw.cache_info())
@lru_cache(maxsize=None)
def f0(x): return x
f0(1); f0(1.0); _log(f0.cache_info())
@lru_cache(maxsize=0)
def z(x): return x
z(1); z(1); _log(z.cache_info())
_log(sorted([3, 1, 2], key=cmp_to_key(lambda a, b: b - a)))
_log(sorted(['bb', 'a', 'ccc'], key=cmp_to_key(lambda a, b: len(a) - len(b))))
_log(max([1, 5, 2], key=cmp_to_key(lambda a, b: a - b)))
try: lru_cache('x')
except TypeError as e: _log(e)


# === compare against what CPython printed ===
EXPECTED = ['10 5', 'reduce() of empty iterable with no initial value', "5 True () {'base': 2}", '3 8 11', 'hello doc here 3 4', '4 4 9 16 4 [2, 3, 4, 2] CacheInfo(hits=1, misses=4, maxsize=2, currsize=2)', 'CacheInfo(hits=0, misses=0, maxsize=2, currsize=0)', '1548008755920 CacheInfo(hits=58, misses=61, maxsize=128, currsize=61)', '2 3 2 CacheInfo(hits=1, misses=2, maxsize=None, currsize=2)', 'CacheInfo(hits=0, misses=2, maxsize=None, currsize=2)', 'CacheInfo(hits=0, misses=2, maxsize=0, currsize=0)', '[3, 2, 1]', "['a', 'bb', 'ccc']", '5', 'Expected first argument to be an integer, a callable, or None']

assert len(_lines) == len(EXPECTED), (len(_lines), len(EXPECTED))
for got, want in zip(_lines, EXPECTED):
    assert got == want, (got, want)
