# Extension: `heapq`, `bisect`, `copy` and `time`. Upstream Monty has none of them.
# `time.sleep` does not block: it advances the clock the program reads. Expected output is
# CPython 3.13's (the time lines print only comparisons, so they hold on any clock).
_lines = []

def _log(*parts):
    _lines.append(' '.join(str(p) for p in parts))


import heapq, bisect, copy, time
h = []
for v in [5, 3, 8, 1, 9, 2, 7, 3]:
    heapq.heappush(h, v)
_log(h)
_log([heapq.heappop(h) for _ in range(3)], h)
x = [9, 8, 7, 6, 5, 4, 3, 2, 1, 0, 5]; heapq.heapify(x); _log(x)
_log(heapq.heapreplace(x, 100), x, heapq.heappushpop(x, -1), heapq.heappushpop(x, 50), x)
_log(heapq.nsmallest(3, [5, 1, 4, 2, 3]), heapq.nlargest(3, [5, 1, 4, 2, 3]), heapq.nsmallest(2, ['bb', 'a', 'ccc'], key=len), heapq.nlargest(2, [(1, 'a'), (1, 'b'), (0, 'c')], key=lambda t: t[0]), heapq.nsmallest(0, [1]), heapq.nlargest(5, [1, 2]), heapq.nsmallest(1, []))
_log(list(heapq.merge([1, 4, 7], [2, 5, 8], [3, 6, 9])), list(heapq.merge([3, 1], [4, 2], reverse=True)), list(heapq.merge(['b', 'aa'], ['c', 'a'], key=len)))
for f, a in ((heapq.heappop, ([],)), (heapq.heappush, ((1,), 1)), (heapq.heapreplace, ([], 1))):
    try: f(*a)
    except Exception as e: _log(type(e).__name__, e)
pq = []
heapq.heappush(pq, (2, 'b')); heapq.heappush(pq, (1, 'z')); heapq.heappush(pq, (1, 'a')); _log([heapq.heappop(pq) for _ in range(3)])
a = [1, 2, 4, 4, 8]
_log(bisect.bisect(a, 4), bisect.bisect_left(a, 4), bisect.bisect_right(a, 4), bisect.bisect(a, 0), bisect.bisect(a, 9), bisect.bisect(a, 4, 0, 2), bisect.bisect_left(a, 4, lo=3))
b = [1, 3, 5]; bisect.insort(b, 4); bisect.insort_left(b, 3); bisect.insort_right(b, 3); _log(b)
_log(bisect.bisect_left(['a', 'bb', 'ccc'], 2, key=len), bisect.bisect(['a', 'bb', 'ccc'], 2, key=len))
c = ['a', 'ccc']; bisect.insort(c, 'bb', key=len); _log(c)
try: bisect.bisect_left(a, 1, -1)
except ValueError as e: _log(e)
n = [1, [2, 3], {'k': [4]}, (5, [6])]
s = copy.copy(n); d = copy.deepcopy(n)
n[1].append(99); _log(s[1], d[1], s is n, s[1] is n[1], d[2]['k'] is n[2]['k'], d == [1, [2, 3], {'k': [4]}, (5, [6])])
cyc = [1]; cyc.append(cyc); dc = copy.deepcopy(cyc); _log(dc[1] is dc, dc is cyc, len(dc))
class P:
    def __init__(self): self.items = [1]; self.name = 'p'
p = P(); q = copy.copy(p); r = copy.deepcopy(p); p.items.append(2); _log(q.items, r.items, q.name, type(r).__name__, q is p)
class C:
    def __init__(self): self.v = 1
    def __copy__(self): c = C(); c.v = 'copied'; return c
    def __deepcopy__(self, memo): c = C(); c.v = 'deep'; return c
_log(copy.copy(C()).v, copy.deepcopy(C()).v)
import collections
dd = collections.defaultdict(list); dd['a'].append(1); dd2 = copy.deepcopy(dd); dd['a'].append(2); _log(dd2, copy.copy(collections.Counter('aab')), copy.deepcopy(collections.deque([1, [2]], maxlen=3)))
_log(copy.copy((1, [2])), copy.deepcopy((1, 2)) , copy.copy('s'), copy.deepcopy({1, 2}), copy.copy(None), copy.deepcopy(frozenset([1])), copy.copy(len), copy.copy(range(3)))
Pt = collections.namedtuple('Pt', 'x y'); _log(copy.deepcopy(Pt(1, [2])), copy.copy(Pt(1, 2)))
t = time.time(); m0 = time.monotonic(); time.sleep(0.5); _log(time.time() - t >= 0.5, time.monotonic() - m0 >= 0.5, time.perf_counter() > 0, isinstance(time.time_ns(), int), time.process_time() >= 0)
try: time.sleep(-1)
except ValueError as e: _log(e)
g = time.gmtime(86400 * 365.25 * 30); _log(g.tm_year, g.tm_mon, g.tm_mday, g.tm_hour, g.tm_wday, g.tm_yday, g[0], tuple(g)[:3], time.mktime(g), len(g))
_log(time.strftime('%Y-%m-%d %H:%M:%S %a %b %j %y %p %I %% %e|%z|%c|%x|%X|%D|%F|%T|%R|%u|%w', time.gmtime(1700000000)))
_log(time.asctime(time.gmtime(0)), time.ctime(0), time.strftime('%Y', (2001, 2, 3, 4, 5, 6, 0, 0, 0)), time.timezone, time.tzname, time.altzone, time.daylight)
_log(time.gmtime(0)[:6], time.gmtime(-1)[:6], time.gmtime(1.9)[5], time.localtime(5)[:6])


# === compare against what CPython printed ===
EXPECTED = ['[1, 3, 2, 3, 9, 8, 7, 5]', '[1, 2, 3] [3, 7, 5, 8, 9]', '[0, 1, 3, 2, 5, 4, 7, 9, 6, 5, 8]', '0 [2, 5, 3, 6, 5, 4, 7, 9, 100, 50, 8] -1 1 [2, 5, 3, 6, 5, 4, 7, 9, 100, 50, 8]', "[1, 2, 3] [5, 4, 3] ['a', 'bb'] [(1, 'a'), (1, 'b')] [] [2, 1] []", "[1, 2, 3, 4, 5, 6, 7, 8, 9] [4, 3, 2, 1] ['b', 'c', 'a', 'aa']", 'IndexError index out of range', 'TypeError heappush() argument 1 must be list, not tuple', 'IndexError index out of range', "[(1, 'a'), (1, 'z'), (2, 'b')]", '4 2 4 0 5 2 3', '[1, 3, 3, 3, 4, 5]', '1 2', "['a', 'bb', 'ccc']", 'lo must be non-negative', '[2, 3, 99] [2, 3] False True False True', 'True False 2', '[1, 2] [1] p P False', 'copied deep', "defaultdict(<class 'list'>, {'a': [1]}) Counter({'a': 2, 'b': 1}) deque([1, [2]], maxlen=3)", '(1, [2]) (1, 2) s {1, 2} None frozenset({1}) <built-in function len> range(0, 3)', 'Pt(x=1, y=[2]) Pt(x=1, y=2)', 'True True True True True', 'sleep length must be non-negative', '2000 1 1 12 5 1 2000 (2000, 1, 1) 946728000.0 9', '2023-11-14 22:13:20 Tue Nov 318 23 PM 10 % 14|+0000|Tue Nov 14 22:13:20 2023|11/14/23|22:13:20|11/14/23|2023-11-14|22:13:20|22:13|2|2', "Thu Jan  1 00:00:00 1970 Thu Jan  1 00:00:00 1970 2001 0 ('UTC', 'UTC') 0 0", '(1970, 1, 1, 0, 0, 0) (1969, 12, 31, 23, 59, 59) 1 (1970, 1, 1, 0, 0, 5)']

assert len(_lines) == len(EXPECTED), (len(_lines), len(EXPECTED))
for got, want in zip(_lines, EXPECTED):
    assert got == want, (got, want)
