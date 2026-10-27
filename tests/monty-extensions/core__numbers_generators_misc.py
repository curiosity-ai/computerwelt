# Extension: corrections to the core that the new standard modules depend on. Each line's
# expectation is CPython 3.13's. Upstream Monty documents none of these as supported:
# generator send/throw/close, function attributes and docstrings, `float.as_integer_ratio`,
# `int.from_bytes`, an exact `math.fsum`, correctly rounded big-integer division, `raise ... from
# None`, `issubclass` with a tuple, `callable` on instances, and `\Z` in `re`.
_lines = []

def _log(*parts):
    _lines.append(' '.join(str(p) for p in parts))


import math, collections
# --- numeric: int true division is correctly rounded, and float(int) rounds to nearest even
for n, d in [(1,3),(10**30+1,10**29),(2**1100+1, 3),(10**400+7,10**399+3),(1,2**1070),(3,2**1074),(2**54+3,1),(-7,3),(5,-3),(10**25, 7),(2**70+1, 2**17),(123456789012345678901234567890, 987654321098765432109876543210),(1,10**310),(10**310,1),(0,5),(-(2**80),2**79)]:
    try: _log(n / d)
    except Exception as e: _log(type(e).__name__, e)
_log(float(2**53+1), float(2**63+2**10+1), float(2**70+2**17+1), float(-(2**70+2**17+1)), float(2**1023))
for bad in (lambda: float(2**1024), lambda: 2**1024 + 1.5, lambda: 1.5 * 10**400, lambda: 10**400 / 1.5, lambda: math.sqrt(10**400)):
    try: bad()
    except OverflowError as e: _log('OverflowError', e)
_log(10**400 // 10**399, 10**400 % 7, divmod(10**400, 10**399 + 1)[0] > 0, 7 // 2.0, 10**20 + 0.5)
# --- fsum is exact
_log(math.fsum([1e100, 1.0, -1e100]), math.fsum([0.1] * 10), math.fsum([1e308, 1e308, -1e308]) if False else '', math.fsum([]), math.fsum([float('inf'), 1.0]), math.fsum([3, 4.5]))
for bad in ([float('inf'), float('-inf')],):
    try: math.fsum(bad)
    except ValueError as e: _log('ValueError', e)
_log(math.fsum([float('nan'), 1.0]) != math.fsum([float('nan'), 1.0]), math.fsum(x / 10 for x in range(1, 11)), math.fsum([2**-1074, 2**-1074]))
# --- float.as_integer_ratio, int.from_bytes
_log((0.5).as_integer_ratio(), (0.1).as_integer_ratio(), (-3.0).as_integer_ratio(), (1e300).as_integer_ratio()[1], (5e-324).as_integer_ratio()[1] == 2**1074, (0.0).as_integer_ratio())
for bad in (float('nan'), float('inf')):
    try: bad.as_integer_ratio()
    except (ValueError, OverflowError) as e: _log(type(e).__name__, e)
_log(int.from_bytes(b'\x01\x02', 'big'), int.from_bytes(b'\x01\x02', 'little'), int.from_bytes(b'\xff\xfe', 'big', signed=True), int.from_bytes(b'', 'big'), int.from_bytes(b'\x80', byteorder='big', signed=True), int.from_bytes(bytes(range(20)), 'big'))
# --- raise ... from None hides the context
def f():
    try:
        {}['k']
    except KeyError:
        raise ValueError('hidden') from None
try: f()
except ValueError as e: _log(e.__cause__, e.__suppress_context__ if hasattr(e, '__suppress_context__') else 'n/a')
# --- exceptions built from several arguments print as a tuple
_log(str(ValueError('a', 1)), str(KeyError('k')), str(Exception()), str(Exception('only')), repr(ValueError('a', 1)))
# --- issubclass takes a tuple
_log(issubclass(KeyError, (ValueError, LookupError)), issubclass(KeyError, (ValueError, TypeError)), issubclass(bool, (str, int)), issubclass(int, (float,)))
# --- callable sees __call__
class Adder:
    def __call__(self, x): return x + 1
class Plain: pass
_log(callable(Adder()), callable(Plain()), callable(Plain), callable(len), callable(1), callable(lambda: 0))
# --- function attributes and docstrings
def documented():
    "I am documented."
def undocumented(): pass
documented.counter = 0; documented.counter += 1; documented.tag = 'x'
_log(documented.__doc__, undocumented.__doc__, documented.counter, documented.tag, documented.__name__, hasattr(documented, 'missing'))
documented.__name__ = 'renamed'; _log(documented.__name__)
try: undocumented.nope
except AttributeError as e: _log('AttributeError')
# --- re: \Z anchors the end of the string
import re
_log(bool(re.search(r'a\Z', 'ba')), bool(re.search(r'a\Z', 'ab')), bool(re.search(r'[a-z]\.\Z', 'd.')), bool(re.search(r'\Aab\Z', 'ab')), re.findall(r'\w+\Z', 'one two'))
# --- generators: send, throw, close
def g():
    try:
        x = yield 1
        _log('got', x)
        y = yield 2
        _log('got', y)
    except ValueError as e:
        _log('caught', e)
        yield 99
    finally:
        _log('cleanup')
a = g(); _log(next(a)); _log(a.send('s'))
try: a.send('t')
except StopIteration: _log('stop')
a = g(); next(a); _log(a.throw(ValueError('boom')))
try: next(a)
except StopIteration: _log('stop2')
a = g(); next(a)
try: a.throw(KeyError('k'))
except KeyError as e: _log('propagated', e)
a = g(); next(a); a.close(); _log('closed')
try: next(a)
except StopIteration: _log('exhausted')
b = g()
try: b.send(1)
except TypeError as e: _log(e)
def stubborn():
    try: yield 1
    finally: yield 2
s = stubborn(); next(s)
try: s.close()
except RuntimeError as e: _log(e)
def acc():
    t = 0
    while True:
        v = yield t
        if v is None: break
        t += v
c = acc(); next(c); c.send(3); _log(c.send(4))
fresh = g()
try: fresh.throw(ValueError('early'))
except ValueError as e: _log('thrown at start', e)
fresh.close(); fresh.close(); _log('double close ok')


# === compare against what CPython printed ===
EXPECTED = ['0.3333333333333333', '10.0', 'OverflowError integer division result too large for a float', '10.0', '8e-323', '1.5e-323', '1.8014398509481988e+16', '-2.3333333333333335', '-1.6666666666666667', '1.4285714285714285e+24', '9007199254740992.0', '0.1249999988609375', '1e-310', 'OverflowError integer division result too large for a float', '0.0', '-2.0', '9007199254740992.0 9.223372036854778e+18 1.1805916207174116e+21 -1.1805916207174116e+21 8.98846567431158e+307', 'OverflowError int too large to convert to float', 'OverflowError int too large to convert to float', 'OverflowError int too large to convert to float', 'OverflowError int too large to convert to float', 'OverflowError int too large to convert to float', '10 4 True 3.0 1e+20', '1.0 1.0  0.0 inf 7.5', 'ValueError -inf + inf in fsum', 'True 5.5 1e-323', '(1, 2) (3602879701896397, 36028797018963968) (-3, 1) 1 True (0, 1)', 'ValueError cannot convert NaN to integer ratio', 'OverflowError cannot convert Infinity to integer ratio', '258 513 -2 0 -128 22475995960490625424124334220934763854762515', 'None True', "('a', 1) 'k'  only ValueError('a', 1)", 'True False True False', 'True False True True False True', 'I am documented. None 1 x documented False', 'renamed', 'AttributeError', "True False True True ['two']", '1', 'got s', '2', 'got t', 'cleanup', 'stop', 'caught boom', '99', 'cleanup', 'stop2', 'cleanup', "propagated 'k'", 'cleanup', 'closed', 'exhausted', "can't send non-None value to a just-started generator", 'generator ignored GeneratorExit', '7', 'thrown at start early', 'double close ok']

assert len(_lines) == len(EXPECTED), (len(_lines), len(EXPECTED))
for got, want in zip(_lines, EXPECTED):
    assert got == want, (got, want)
