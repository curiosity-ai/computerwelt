# Extension: the `statistics` module. Upstream Monty has none. The arithmetic is exact, as
# CPython's is, so results agree to the last digit; expected output is CPython 3.13's.
_lines = []

def _log(*parts):
    _lines.append(' '.join(str(p) for p in parts))


import statistics as s
import math
sets = [[1, 2, 3, 4], [1, 2, 3], [2.5, 3.5, 1.25], [0.1] * 10, [1, 2, 2, 3, 3, 3], [10**20, 1, 2], [1e300, 1e300], [-5, 5], [1, 2.5], [0.1, 0.2, 0.3, 0.4, 0.5, 0.7], [3], [float('inf'), 1.0]]
for d in sets:
    row = []
    for f in (s.mean, s.fmean, s.median, s.median_low, s.median_high, s.mode, s.multimode, s.pvariance, s.pstdev, s.variance, s.stdev, s.median_grouped, s.geometric_mean, s.harmonic_mean):
        try: row.append(f(d))
        except Exception as e: row.append((type(e).__name__, str(e)))
    _log(d, row)
_log(s.variance([1, 3]), s.variance([2.0, 4.0], 3), s.pvariance([1, 2, 3], 2), s.pvariance([1.5, 2.5], mu=2.0), s.stdev([1, 2, 3, 4], 2.5))
for d in ([], [1]):
    for f in (s.mean, s.fmean, s.median, s.mode, s.pvariance, s.variance, s.geometric_mean, s.harmonic_mean, s.stdev):
        try: _log(f.__name__, f(d))
        except Exception as e: _log(f.__name__, type(e).__name__, e)
_log(s.quantiles([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]), s.quantiles([1, 2, 3, 4, 5], n=10, method='inclusive'), s.quantiles([1, 2, 3], n=2), s.quantiles([5], n=3), s.quantiles([1.5, 2.5, 9.0, 4.25], n=5))
try: s.quantiles([1, 2], method='x')
except ValueError as e: _log(e)
x = [1, 2, 3, 4, 5, 6, 7, 8, 9]; y = [1, 2, 3, 1, 2, 3, 1, 2, 3]
_log(s.covariance(x, y), s.correlation(x, y), s.correlation(x, y, method='ranked'), s.linear_regression(x, y), s.linear_regression(x, y, proportional=True))
_log(s.correlation([1, 2, 3], [2, 4, 6]), s.correlation([1, 2, 3], [3, 2, 1]), s.linear_regression([1, 2, 3], [2, 4, 6.5]))
for f, a in ((s.correlation, ([1, 1, 1], [1, 2, 3])), (s.linear_regression, ([1, 1], [1, 2])), (s.covariance, ([1], [1])), (s.covariance, ([1, 2], [1]))):
    try: f(*a)
    except Exception as e: _log(f.__name__, type(e).__name__, e)
_log(s.harmonic_mean([40, 60]), s.harmonic_mean([1, 2, 4]), s.harmonic_mean([1, 2], [3, 1]), s.harmonic_mean([0, 1]), s.harmonic_mean([2.5, 7.5]))
_log(s.geometric_mean([54, 24, 36]), s.geometric_mean([1, 0]), s.mean([True, False, True]))
_log(s.fmean([1, 2, 3], [3, 2, 1]), s.median_grouped([52, 52, 53, 54]), s.median_grouped([1, 3, 3, 5, 7], 2), s.median_grouped([1, 2, 3, 4], interval=0.5))
try: s.mean(['a'])
except TypeError as e: _log(e)
_log(isinstance(s.StatisticsError('x'), ValueError), s.StatisticsError.__name__)
try: s.mean([])
except s.StatisticsError as e: _log('caught', e)
try: s.mean([])
except ValueError as e: _log('as ValueError')


# === compare against what CPython printed ===
EXPECTED = ['[1, 2, 3, 4] [2.5, 2.5, 2.5, 2, 3, 1, [1, 2, 3, 4], 1.25, 1.118033988749895, 1.6666666666666667, 1.2909944487358056, 2.5, 2.2133638394006434, 1.92]', '[1, 2, 3] [2, 2.0, 2, 2, 2, 1, [1, 2, 3], 0.6666666666666666, 0.816496580927726, 1, 1.0, 2.0, 1.8171205928321397, 1.6363636363636365]', '[2.5, 3.5, 1.25] [2.4166666666666665, 2.4166666666666665, 2.5, 2.5, 2.5, 2.5, [2.5, 3.5, 1.25], 0.8472222222222222, 0.9204467514322717, 1.2708333333333333, 1.1273124382057236, 2.5, 2.219760004356502, 2.019230769230769]', '[0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1] [0.1, 0.1, 0.1, 0.1, 0.1, 0.1, [0.1], 0.0, 0.0, 0.0, 0.0, 0.09999999999999998, 0.10000000000000002, 0.1]', '[1, 2, 2, 3, 3, 3] [2.3333333333333335, 2.3333333333333335, 2.5, 2, 3, 3, [3], 0.5555555555555556, 0.7453559924999299, 0.6666666666666666, 0.816496580927726, 2.5, 2.1822472719434427, 2.0]', '[100000000000000000000, 1, 2] [3.333333333333333e+19, 3.333333333333333e+19, 2, 2, 2, 100000000000000000000, [100000000000000000000, 1, 2], 2.2222222222222223e+39, 4.7140452079103164e+19, 3.333333333333333e+39, 5.773502691896258e+19, 2.0, 5848035.476425733, 2.0]', '[1e+300, 1e+300] [1e+300, 1e+300, 1e+300, 1e+300, 1e+300, 1e+300, [1e+300], 0.0, 0.0, 0.0, 0.0, 1e+300, 9.999999999999763e+299, 9.999999999999999e+299]', '[-5, 5] [0, 0.0, 0.0, -5, 5, -5, [-5, 5], 25, 5.0, 50, 7.0710678118654755, 4.5, (\'StatisticsError\', "(\'No negative inputs allowed\', -5)"), (\'StatisticsError\', \'harmonic mean does not support negative values\')]', '[1, 2.5] [1.75, 1.75, 1.75, 1, 2.5, 1, [1, 2.5], 0.5625, 0.75, 1.125, 1.0606601717798212, 2.0, 1.5811388300841898, 1.4285714285714286]', '[0.1, 0.2, 0.3, 0.4, 0.5, 0.7] [0.36666666666666664, 0.3666666666666667, 0.35, 0.3, 0.4, 0.1, [0.1, 0.2, 0.3, 0.4, 0.5, 0.7], 0.03888888888888888, 0.19720265943665385, 0.04666666666666666, 0.21602468994692867, -0.09999999999999998, 0.30717076619745354, 0.24730127576054955]', "[3] [3, 3.0, 3, 3, 3, 3, [3], 0, 0.0, ('StatisticsError', 'variance requires at least two data points'), ('StatisticsError', 'stdev requires at least two data points'), 3.0, 3.0000000000000004, 3]", "[inf, 1.0] [inf, inf, inf, 1.0, inf, inf, [inf, 1.0], inf, ('ValueError', 'inf or nan encountered in data'), inf, ('ValueError', 'inf or nan encountered in data'), inf, inf, 2.0]", '2 2.0 0.6666666666666666 0.25 1.2909944487358056', 'mean StatisticsError mean requires at least one data point', 'fmean StatisticsError fmean requires at least one data point', 'median StatisticsError no median for empty data', 'mode StatisticsError no mode for empty data', 'pvariance StatisticsError pvariance requires at least one data point', 'variance StatisticsError variance requires at least two data points', 'geometric_mean StatisticsError Must have a non-empty dataset', 'harmonic_mean StatisticsError harmonic_mean requires at least one data point', 'stdev StatisticsError stdev requires at least two data points', 'mean 1', 'fmean 1.0', 'median 1', 'mode 1', 'pvariance 0', 'variance StatisticsError variance requires at least two data points', 'geometric_mean 1.0', 'harmonic_mean 1', 'stdev StatisticsError stdev requires at least two data points', '[2.75, 5.5, 8.25] [1.4, 1.8, 2.2, 2.6, 3.0, 3.4, 3.8, 4.2, 4.6] [2.0] [5, 5] [1.5, 2.5, 4.25, 9.0]', "Unknown method: 'x'", '0.75 0.31622776601683794 0.31622776601683794 LinearRegression(slope=0.1, intercept=1.5) LinearRegression(slope=0.3368421052631579, intercept=0.0)', '1.0 -1.0 LinearRegression(slope=2.25, intercept=-0.33333333333333304)', 'correlation StatisticsError at least one of the inputs is constant', 'linear_regression StatisticsError x is constant', 'covariance StatisticsError covariance requires at least two data points', 'covariance StatisticsError covariance requires that both inputs have same number of data points', '48.0 1.7142857142857142 1.1428571428571428 0 3.75', '36.000000000000014 0.0 0.6666666666666666', '1.6666666666666667 52.5 3.5 2.75', "can't convert type 'str' to numerator/denominator", 'True StatisticsError', 'caught mean requires at least one data point', 'as ValueError']

assert len(_lines) == len(EXPECTED), (len(_lines), len(EXPECTED))
for got, want in zip(_lines, EXPECTED):
    assert got == want, (got, want)
