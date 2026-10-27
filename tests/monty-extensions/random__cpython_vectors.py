# Extension: the `random` module.
# Upstream Monty has no `random` at all, so the import raised ModuleNotFoundError. This one is
# CPython's generator (a Mersenne Twister seeded the same way) under CPython's algorithms,
# which is what makes a seeded run reproducible across implementations: every expected value
# below was produced by CPython 3.13 itself.
import random


# === random() after seed(0) ===
r = random.Random(0)
assert [r.random() for _ in range(3)] == [0.8444218515250481, 0.7579544029403025, 0.420571580830845]

# === random() after seed(1) ===
r = random.Random(1)
assert [r.random() for _ in range(3)] == [0.13436424411240122, 0.8474337369372327, 0.763774618976614]

# === random() after seed(42) ===
r = random.Random(42)
assert [r.random() for _ in range(3)] == [0.6394267984578837, 0.025010755222666936, 0.27502931836911926]

# === random() after seed(-7) ===
r = random.Random(-7)
assert [r.random() for _ in range(3)] == [0.32383276483316237, 0.15084917392450192, 0.6509344730398537]

# === random() after seed(1099511627776) ===
r = random.Random(1099511627776)
assert [r.random() for _ in range(3)] == [0.1036394562812375, 0.6705558677439764, 0.4266543913526999]

# === random() after seed(1267650600228229401496703205381) ===
r = random.Random(1267650600228229401496703205381)
assert [r.random() for _ in range(3)] == [0.7033503239395905, 0.9851952005245675, 0.03063164465720125]

# === random() after seed('hello') ===
r = random.Random('hello')
assert [r.random() for _ in range(3)] == [0.3537754404730722, 0.6631985810268619, 0.5476053663383964]

# === random() after seed(b'bytes') ===
r = random.Random(b'bytes')
assert [r.random() for _ in range(3)] == [0.37075677971469856, 0.6703304051208392, 0.1462615841414766]

# === random() after seed(3.5) ===
r = random.Random(3.5)
assert [r.random() for _ in range(3)] == [0.3039190124834461, 0.23014450764056538, 0.9120431245165537]

# === random() after seed('é☃') ===
r = random.Random('é☃')
assert [r.random() for _ in range(3)] == [0.7036224017270832, 0.5556423703280035, 0.6032316267187672]

# === getrandbits at every width ===
r = random.Random(12345)
assert [r.getrandbits(k) for k in (0, 1, 5, 31, 32, 33, 64, 100, 257)] == [0, 0, 23, 21838114, 3522623596, 7839202253, 15774518201988393282, 332256083118531044858259323495, 146024246062169781882962453821637302686448089239783394635032962968627223849285]

# === randbytes ===
r = random.Random(12345)
assert r.randbytes(11) == b'\x87\x99\xa7j:C\x91\xbbr\x9a\x02'

# === randrange ===
r = random.Random(12345)
assert [r.randrange(10) for _ in range(5)] + [r.randrange(5, 50, 7) for _ in range(5)] + [r.randrange(100, 0, -9) for _ in range(5)] + [r.randrange(2**70) for _ in range(2)] == [6, 0, 4, 5, 3, 19, 33, 26, 12, 19, 91, 46, 64, 28, 10, 216433244207631809487, 482348160374121203070]

# === randint ===
r = random.Random(12345)
assert [r.randint(-5, 5) for _ in range(8)] == [1, -5, -1, 0, -2, -1, 4, 1]

# === randrange(1) still consumes the stream ===
r = random.Random(12345)
assert [r.randrange(1) for _ in range(3)] + [r.random()] == [0, 0, 0, 0.8551383418471491]

# === choice ===
r = random.Random(12345)
assert [r.choice(list(range(10))) for _ in range(6)] + [r.choice('abcdef'), r.choice((1, 2, 3))] == [6, 0, 4, 5, 3, 4, 'e', 2]

# === shuffle ===
r = random.Random(12345)
data = list(range(10))
r.shuffle(data)
assert data == [8, 7, 3, 5, 1, 2, 9, 4, 0, 6]

# === sample from a small population ===
r = random.Random(12345)
assert r.sample(range(20), 5) == [13, 0, 9, 11, 6]

# === sample from a large population ===
r = random.Random(12345)
assert r.sample(range(10000), 12) == [6825, 166, 4892, 6036, 3172, 4427, 9273, 7147, 2649, 6112, 2035, 7093]

# === sample of a string ===
r = random.Random(12345)
assert r.sample('abcdefgh', 3) == ['g', 'f', 'a']

# === sample with counts ===
r = random.Random(12345)
assert r.sample(['a', 'b', 'c'], 4, counts=[3, 1, 2]) == ['b', 'a', 'a', 'a']

# === sample of the whole population ===
r = random.Random(12345)
assert r.sample(list(range(8)), 8) == [6, 5, 0, 2, 4, 7, 1, 3]

# === choices ===
r = random.Random(12345)
assert r.choices([1, 2, 3, 4], k=6) == [2, 1, 4, 2, 2, 1]

# === choices with weights ===
r = random.Random(12345)
assert r.choices('abc', weights=[1, 5, 2], k=8) == ['b', 'a', 'c', 'b', 'b', 'b', 'b', 'b']

# === choices with cumulative weights ===
r = random.Random(12345)
assert r.choices('abc', cum_weights=[1, 6, 8], k=8) == ['b', 'a', 'c', 'b', 'b', 'b', 'b', 'b']

# === uniform ===
r = random.Random(12345)
assert [r.uniform(1, 3), r.uniform(-2.5, 7)] == [1.8332397450906823, -2.4033928901578507]

# === triangular ===
r = random.Random(12345)
assert [r.triangular(), r.triangular(0, 10, 2), r.triangular(5, 1, 3)] == [0.4564098336721839, 0.45098047534385255, 2.182517621843351]

# === gauss ===
r = random.Random(12345)
assert [r.gauss(), r.gauss(10, 2), r.gauss(0, 1)] == [-0.12380079558885389, 10.14304992695133, 0.3833691943171596]

# === normalvariate ===
r = random.Random(12345)
assert [r.normalvariate(), r.normalvariate(5, 3)] == [-0.1445104756229858, 7.38636655527141]

# === lognormvariate ===
r = random.Random(12345)
assert r.lognormvariate(0, 0.5) == 0.930293419449595

# === expovariate ===
r = random.Random(12345)
assert [r.expovariate(), r.expovariate(4)] == [0.5389162854563174, 0.0025553071735785462]

# === vonmisesvariate ===
r = random.Random(12345)
assert [r.vonmisesvariate(1, 4), r.vonmisesvariate(2, 0), r.vonmisesvariate(-3, 0.5)] == [1.3782844375749927, 1.8764095503283207, 4.079236134408328]

# === gammavariate, in each of its three regimes ===
r = random.Random(12345)
assert [r.gammavariate(3, 2), r.gammavariate(1, 1), r.gammavariate(0.4, 2)] == [5.1613512202305465, 1.7441500547477897, 0.13738889055495557]

# === betavariate ===
r = random.Random(12345)
assert [r.betavariate(2, 5), r.betavariate(0.5, 0.5)] == [0.1641035598359883, 0.2975869955171247]

# === paretovariate ===
r = random.Random(12345)
assert r.paretovariate(3) == 1.1967849606038736

# === weibullvariate ===
r = random.Random(12345)
assert r.weibullvariate(2, 1.5) == 1.3244763283563263

# === binomialvariate, in each of its algorithms ===
r = random.Random(12345)
assert [r.binomialvariate(), r.binomialvariate(20, 0.3), r.binomialvariate(100, 0.5), r.binomialvariate(1000, 0.7), r.binomialvariate(5, 0.2), r.binomialvariate(10**4, 0.01)] == [1, 3, 45, 716, 1, 89]

# === module-level functions share one generator ===
random.seed(99)
assert [random.random(), random.randint(1, 100), random.choice('xyz')] == [0.40397807494366633, 26, 'z']

# === seed version 1 hashes text the Python 2 way ===
random.seed('abc', version=1)
assert random.random() == 0.09639374978773074

# === getstate has the version, the words, and the pending gauss ===
r = random.Random(5)
r.gauss()
assert (r.getstate()[0], len(r.getstate()[1]), r.getstate()[1][-1], r.getstate()[2]) == (3, 625, 4, -1.1481606807908016)

