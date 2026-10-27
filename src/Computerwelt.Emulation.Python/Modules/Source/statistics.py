"""statistics: mathematical statistics functions, computed exactly where CPython does."""

import math
from math import fsum, sqrt, exp, log, isfinite
from collections import Counter, namedtuple
from _hostexc import StatisticsError

LinearRegression = namedtuple('LinearRegression', ('slope', 'intercept'))


def _ratio(x):
    """Exact (numerator, denominator) of an int or float."""
    if isinstance(x, int):
        return (int(x), 1)
    if isinstance(x, float):
        return x.as_integer_ratio()
    raise TypeError("can't convert type '%s' to numerator/denominator" % type(x).__name__)


def _div(n, d):
    """Correctly rounded n / d for integers."""
    return n / d


def _sum_exact(values):
    """Return (T, N, D, count, special): the exact sum N/D of values, and the type to answer in.

    ``special`` is the float sum of any non-finite values, or None.
    """
    T = int
    N = 0
    D = 1
    count = 0
    special = None
    for x in values:
        count += 1
        if isinstance(x, float):
            T = float
            if not isfinite(x):
                special = x if special is None else special + x
                continue
        elif not isinstance(x, int):
            raise TypeError("can't convert type '%s' to numerator/denominator" % type(x).__name__)
        n, d = _ratio(x)
        if d == D:
            N += n
        elif D % d == 0:
            N += n * (D // d)
        else:
            g = math.gcd(D, d)
            scale = d // g
            N = N * scale + n * (D // g)
            D = D * scale
    return T, N, D, count, special


def _convert_ratio(N, D, T):
    """The value N/D, as an int when the data were ints and it is whole, else a float."""
    if T is int and N % D == 0:
        return N // D
    return _div(N, D)


def _as_list(data):
    if isinstance(data, (list, tuple)):
        return data
    return list(data)


def mean(data):
    data = _as_list(data)
    n = len(data)
    if n < 1:
        raise StatisticsError('mean requires at least one data point')
    T, N, D, count, special = _sum_exact(data)
    if special is not None:
        return special
    return _convert_ratio(N, D * n, T)


def fmean(data, weights=None):
    if weights is None:
        data = _as_list(data)
        n = len(data)
        if n < 1:
            raise StatisticsError('fmean requires at least one data point')
        return fsum(data) / n
    data = _as_list(data)
    weights = _as_list(weights)
    n = len(data)
    if len(weights) != n:
        raise StatisticsError('data and weights must be the same length')
    num = fsum([float(a) * float(b) for a, b in zip(data, weights)])
    den = fsum(weights)
    if not den:
        raise StatisticsError('sum of weights must be non-zero')
    return num / den


def geometric_mean(data):
    n = 0
    found_zero = False
    logs = []
    for n, x in enumerate(data, 1):
        if x > 0.0 or math.isnan(x):
            logs.append(log(x))
        elif x == 0.0:
            found_zero = True
        else:
            raise StatisticsError('No negative inputs allowed', x)
    total = fsum(logs)
    if not n:
        raise StatisticsError('Must have a non-empty dataset')
    if math.isnan(total):
        return math.nan
    if found_zero:
        return math.nan if total == math.inf else 0.0
    return exp(total / n)


def harmonic_mean(data, weights=None):
    data = _as_list(data)
    errmsg = 'harmonic mean does not support negative values'
    n = len(data)
    if n < 1:
        raise StatisticsError('harmonic_mean requires at least one data point')
    elif n == 1 and weights is None:
        x = data[0]
        if isinstance(x, (int, float)):
            if x < 0:
                raise StatisticsError(errmsg)
            return x
        raise TypeError('unsupported type')
    if weights is None:
        weights = [1] * n
        sum_weights = n
    else:
        weights = _as_list(weights)
        if len(weights) != n:
            raise StatisticsError('Number of weights must match the number of data points')
        _, wn, wd, _, wspecial = _sum_exact(weights)
        sum_weights = wspecial if wspecial is not None else (wn // wd if wn % wd == 0 else wn / wd)
    for x in data:
        if x < 0:
            raise StatisticsError(errmsg)
    try:
        terms = [w / x if w else 0 for w, x in zip(weights, data)]
    except ZeroDivisionError:
        return 0
    T, N, D, count, special = _sum_exact(terms)
    total_positive = N > 0 if special is None else special > 0
    if not total_positive:
        raise StatisticsError('Weighted sum must be positive')
    if special is not None:
        return 0.0
    swn, swd = _ratio(sum_weights)
    return _div(swn * D, swd * N)


def median(data):
    data = sorted(data)
    n = len(data)
    if n == 0:
        raise StatisticsError('no median for empty data')
    if n % 2 == 1:
        return data[n // 2]
    i = n // 2
    return (data[i - 1] + data[i]) / 2


def median_low(data):
    data = sorted(data)
    n = len(data)
    if n == 0:
        raise StatisticsError('no median for empty data')
    if n % 2 == 1:
        return data[n // 2]
    return data[n // 2 - 1]


def median_high(data):
    data = sorted(data)
    n = len(data)
    if n == 0:
        raise StatisticsError('no median for empty data')
    return data[n // 2]


def median_grouped(data, interval=1):
    data = sorted(data)
    n = len(data)
    if not n:
        raise StatisticsError('no median for empty data')
    x = data[n // 2]
    for obj in (x, interval):
        if isinstance(obj, (str, bytes)):
            raise TypeError('expected number but got %r' % (obj,))
    if n == 1:
        return float(data[0])
    L = x - interval / 2
    i = 0
    while i < n and data[i] < x:
        i += 1
    j = i
    while j < n and data[j] <= x:
        j += 1
    cf = i
    f = j - i
    return L + interval * (n / 2 - cf) / f


def mode(data):
    pairs = Counter(iter(data)).most_common(1)
    try:
        return pairs[0][0]
    except IndexError:
        raise StatisticsError('no mode for empty data')


def multimode(data):
    counts = Counter(iter(data))
    if not counts:
        return []
    maxcount = max(counts.values())
    return [value for value, count in counts.items() if count == maxcount]


def _ss(data, mu=None):
    """Exact sum of squared deviations from the mean (or mu): returns (T, SS_num, SS_den, n, special)."""
    T, N, D, n, special = _sum_exact(data)
    if special is not None:
        return T, 0, 1, n, special
    if mu is None:
        cn, cd = N, D * n if n else 1
    else:
        cn, cd = _ratio(mu)
        if isinstance(mu, float):
            T = float
    pairs = [_ratio(x) for x in data]
    maxd = 1
    for xn, xd in pairs:
        if xd > maxd:
            maxd = xd
    ss = 0
    for xn, xd in pairs:
        t = xn * (maxd // xd) * cd - cn * maxd
        ss += t * t
    den = (maxd * cd) ** 2
    return T, ss, den, n, None


def _variance_result(T, num, den, special):
    if special is not None:
        return math.nan if math.isnan(special) else math.inf
    return _convert_ratio(num, den, T)


_SQRT_BIT_WIDTH = 2 * 53 + 3


def _integer_sqrt_of_frac_rto(n, m):
    a = math.isqrt(n // m)
    return a | (a * a * m != n)


def _float_sqrt_of_frac(n, m):
    """Square root of n/m as a correctly rounded float."""
    q = (n.bit_length() - m.bit_length() - _SQRT_BIT_WIDTH) // 2
    if q >= 0:
        numerator = _integer_sqrt_of_frac_rto(n, m << 2 * q) << q
        denominator = 1
    else:
        numerator = _integer_sqrt_of_frac_rto(n << -2 * q, m)
        denominator = 1 << -q
    return numerator / denominator


def _variance(name, minimum, data, center, sample):
    data = _as_list(data)
    n = len(data)
    if n < minimum:
        raise StatisticsError('%s requires at least %s data point%s' % (name, 'two' if minimum == 2 else 'one', 's' if minimum == 2 else ''))
    T, ss, den, n, special = _ss(data, center)
    return T, ss, den * (n - 1 if sample else n), special


def pvariance(data, mu=None):
    T, num, den, special = _variance('pvariance', 1, data, mu, False)
    return _variance_result(T, num, den, special)


def variance(data, xbar=None):
    T, num, den, special = _variance('variance', 2, data, xbar, True)
    return _variance_result(T, num, den, special)


def pstdev(data, mu=None):
    T, num, den, special = _variance('pstdev', 1, data, mu, False)
    if special is not None:
        raise ValueError('inf or nan encountered in data')
    return _float_sqrt_of_frac(num, den)


def stdev(data, xbar=None):
    T, num, den, special = _variance('stdev', 2, data, xbar, True)
    if special is not None:
        raise ValueError('inf or nan encountered in data')
    return _float_sqrt_of_frac(num, den)


def quantiles(data, *, n=4, method='exclusive'):
    if n < 1:
        raise StatisticsError('n must be at least 1')
    data = sorted(data)
    ld = len(data)
    if ld < 2:
        if ld == 1:
            return data * (n - 1)
        raise StatisticsError('must have at least one data point')
    if method == 'inclusive':
        m = ld - 1
        result = []
        for i in range(1, n):
            j, delta = divmod(i * m, n)
            interpolated = (data[j] * (n - delta) + data[j + 1] * delta) / n
            result.append(interpolated)
        return result
    if method == 'exclusive':
        m = ld + 1
        result = []
        for i in range(1, n):
            j = i * m // n
            j = 1 if j < 1 else ld - 1 if j > ld - 1 else j
            delta = i * m - j * n
            interpolated = (data[j - 1] * (n - delta) + data[j] * delta) / n
            result.append(interpolated)
        return result
    raise ValueError('Unknown method: %r' % (method,))


def _sumprod(p, q):
    """Exact sum of pairwise products, rounded once."""
    N = 0
    D = 1
    T = int
    for a, b in zip(p, q):
        if isinstance(a, float) or isinstance(b, float):
            T = float
        an, ad = _ratio(a)
        bn, bd = _ratio(b)
        tn, td = an * bn, ad * bd
        if td == D:
            N += tn
        elif D % td == 0:
            N += tn * (D // td)
        else:
            g = math.gcd(D, td)
            scale = td // g
            N = N * scale + tn * (D // g)
            D = D * scale
    return _convert_ratio(N, D, T) if T is int else _div(N, D)


def covariance(x, y):
    x = _as_list(x)
    y = _as_list(y)
    n = len(x)
    if len(y) != n:
        raise StatisticsError('covariance requires that both inputs have same number of data points')
    if n < 2:
        raise StatisticsError('covariance requires at least two data points')
    xbar = fsum(x) / n
    ybar = fsum(y) / n
    sxy = _sumprod([xi - xbar for xi in x], [yi - ybar for yi in y])
    return sxy / (n - 1)


def _sqrtprod(x, y):
    h = sqrt(x * y)
    if not isfinite(h) or h <= 0:
        return h
    xn, xd = _ratio(x)
    yn, yd = _ratio(y)
    hn, hd = _ratio(h)
    # d = x*y - h*h, exactly
    num = xn * yn * hd * hd - hn * hn * xd * yd
    den = xd * yd * hd * hd
    d = _div(num, den)
    return h + d / (2.0 * h)


def _rank(data):
    ordered = sorted(range(len(data)), key=lambda i: data[i])
    ranks = [0.0] * len(data)
    i = 0
    n = len(data)
    while i < n:
        j = i
        while j + 1 < n and data[ordered[j + 1]] == data[ordered[i]]:
            j += 1
        avg = (i + j) / 2 + 1
        for k in range(i, j + 1):
            ranks[ordered[k]] = avg
        i = j + 1
    return ranks


def correlation(x, y, /, *, method='linear'):
    x = _as_list(x)
    y = _as_list(y)
    n = len(x)
    if len(y) != n:
        raise StatisticsError('correlation requires that both inputs have same number of data points')
    if n < 2:
        raise StatisticsError('correlation requires at least two data points')
    if method not in {'linear', 'ranked'}:
        raise ValueError('Unknown method: %r' % (method,))
    if method == 'ranked':
        x = _rank(x)
        y = _rank(y)
    xbar = fsum(x) / n
    ybar = fsum(y) / n
    xc = [xi - xbar for xi in x]
    yc = [yi - ybar for yi in y]
    sxy = _sumprod(xc, yc)
    sxx = _sumprod(xc, xc)
    syy = _sumprod(yc, yc)
    try:
        result = sxy / _sqrtprod(sxx, syy)
    except ZeroDivisionError:
        raise StatisticsError('at least one of the inputs is constant')
    return max(-1.0, min(1.0, result))


def linear_regression(x, y, /, *, proportional=False):
    x = _as_list(x)
    y = _as_list(y)
    n = len(x)
    if len(y) != n:
        raise StatisticsError('linear regression requires that both inputs have same number of data points')
    if n < 2:
        raise StatisticsError('linear regression requires at least two data points')
    if not proportional:
        xbar = fsum(x) / n
        ybar = fsum(y) / n
        x = [xi - xbar for xi in x]
        y = [yi - ybar for yi in y]
    sxy = _sumprod(x, y) + 0.0
    sxx = _sumprod(x, x)
    try:
        slope = sxy / sxx
    except ZeroDivisionError:
        raise StatisticsError('x is constant')
    intercept = 0.0 if proportional else ybar - slope * xbar
    return LinearRegression(slope=slope, intercept=intercept)
