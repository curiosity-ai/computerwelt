# Extension: the `random` module, beyond the seeded vectors in random__cpython_vectors.py:
# what is not a number — instances, state, errors and the range of what comes out.
import random

# === Random instances are independent of each other and of the module ===
a = random.Random(7)
b = random.Random(7)
assert [a.random() for _ in range(3)] == [b.random() for _ in range(3)]
first = a.random()
random.seed(7)
random.random()
c = random.Random(7)
assert c.random() == random.Random(7).random()
assert isinstance(a, random.Random)
assert not isinstance(3, random.Random)

# === seeding the module twice replays the sequence ===
random.seed(2024)
one = [random.random() for _ in range(4)]
random.seed(2024)
assert [random.random() for _ in range(4)] == one

# === getstate / setstate round trip, including the pending gauss value ===
r = random.Random(1)
r.gauss()
state = r.getstate()
after = [r.gauss(), r.random()]
r.setstate(state)
assert [r.gauss(), r.random()] == after
fresh = random.Random()
fresh.setstate(state)
assert [fresh.gauss(), fresh.random()] == after

# === what unseeded generators produce is in range, and differs between generators ===
values = [random.Random().random() for _ in range(5)]
assert all(0.0 <= v < 1.0 for v in values)
assert len(set(values)) == 5

# === ranges ===
r = random.Random(3)
for _ in range(200):
    assert 0 <= r.randrange(7) < 7
    assert 3 <= r.randint(3, 5) <= 5
    assert r.randrange(10, 0, -3) in (10, 7, 4, 1)
    assert 0 <= r.getrandbits(5) < 32
    assert 1.0 <= r.uniform(1, 2) <= 2.0
assert r.getrandbits(0) == 0
assert r.randrange(2**200).bit_length() <= 200
assert len(r.randbytes(0)) == 0
assert type(r.randbytes(4)) is bytes and len(r.randbytes(4)) == 4

# === shuffle and sample keep the members ===
items = list(range(50))
r.shuffle(items)
assert sorted(items) == list(range(50))
assert items != list(range(50))
picked = r.sample(items, 10)
assert len(picked) == 10 and len(set(picked)) == 10 and all(p in items for p in picked)
assert r.sample([], 0) == []
assert r.choices(['only'], k=3) == ['only', 'only', 'only']
assert r.choices('abc', k=0) == []
assert r.choices('abc', weights=[0, 0, 1], k=4) == ['c', 'c', 'c', 'c']

# === SystemRandom ===
s = random.SystemRandom()
assert isinstance(s, random.SystemRandom)
assert isinstance(s, random.Random)
assert 0.0 <= s.random() < 1.0
assert 0 <= s.randrange(10) < 10
assert s.getrandbits(8) < 256
assert s.seed(5) is None
assert s.choice('abc') in 'abc'
for call in (s.getstate, lambda: s.setstate(None)):
    try:
        call()
        assert False
    except NotImplementedError as exc:
        assert str(exc) == 'System entropy source does not have state.'

# === errors, worded as CPython words them ===
r = random.Random(1)
cases = [
    (lambda: r.choice([]), IndexError, 'Cannot choose from an empty sequence'),
    (lambda: r.choice(5), TypeError, "object of type 'int' has no len()"),
    (lambda: r.randrange(0), ValueError, 'empty range for randrange()'),
    (lambda: r.randrange(5, 5), ValueError, 'empty range in randrange(5, 5)'),
    (lambda: r.randrange(1, 10, 0), ValueError, 'zero step for randrange()'),
    (lambda: r.randrange(10, 1, 2), ValueError, 'empty range in randrange(10, 1, 2)'),
    (lambda: r.randrange(1.5), TypeError, "'float' object cannot be interpreted as an integer"),
    (lambda: r.randint(1), TypeError, "Random.randint() missing 1 required positional argument: 'b'"),
    (lambda: r.sample([1, 2], 3), ValueError, 'Sample larger than population or is negative'),
    (lambda: r.sample({1, 2}, 1), TypeError, 'Population must be a sequence.  For dicts or sets, use sorted(d).'),
    (lambda: r.choices([1], weights=[1, 2]), ValueError, 'The number of weights does not match the population'),
    (lambda: r.choices([1], weights=[0]), ValueError, 'Total of weights must be greater than zero'),
    (lambda: r.choices([1], weights=[1], cum_weights=[1]), TypeError, 'Cannot specify both weights and cumulative weights'),
    (lambda: r.getrandbits(-1), ValueError, 'number of bits must be non-negative'),
    (lambda: r.seed([1]), TypeError, 'The only supported seed types are:\nNone, int, float, str, bytes, and bytearray.'),
    (lambda: r.gammavariate(0, 1), ValueError, 'gammavariate: alpha and beta must be > 0.0'),
    (lambda: r.binomialvariate(-1, 0.5), ValueError, 'n must be non-negative'),
    (lambda: r.binomialvariate(3, 1.5), ValueError, 'p must be in the range 0.0 <= p <= 1.0'),
    (lambda: r.setstate((9, (), None)), ValueError, 'state with version 9 passed to Random.setstate() of version 3'),
    (lambda: r.setstate((3, (1, 2), None)), ValueError, 'state vector is the wrong size'),
    (lambda: r.random(1), TypeError, 'Random.random() takes no arguments (1 given)'),
    (lambda: r.uniform(1, 2, c=3), TypeError, "Random.uniform() got an unexpected keyword argument 'c'"),
]
for call, kind, message in cases:
    try:
        call()
        assert False, message
    except kind as exc:
        assert str(exc) == message, (message, str(exc))

# === the API is where CPython puts it ===
for name in ('seed', 'getstate', 'setstate', 'random', 'getrandbits', 'randbytes', 'randrange', 'randint',
             'choice', 'shuffle', 'sample', 'choices', 'uniform', 'triangular', 'binomialvariate',
             'normalvariate', 'gauss', 'lognormvariate', 'expovariate', 'vonmisesvariate',
             'gammavariate', 'betavariate', 'paretovariate', 'weibullvariate', 'Random', 'SystemRandom'):
    assert hasattr(random, name), name
assert 'sample' in random.__all__
assert not hasattr(random, 'system')

# === a request too large to honour is refused rather than attempted ===
for call in (lambda: random.getrandbits(10**12), lambda: random.randbytes(10**10), lambda: random.choices([1], k=10**9)):
    try:
        call()
        assert False
    except MemoryError:
        pass
