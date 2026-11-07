# Extension: `pandas` — vectorized arithmetic, comparison, label alignment and reductions.
# Upstream Monty has no pandas. Expected values are pandas 2.2's (numpy's rules for each
# dtype: int // int stays int, / is always float, a missing label aligns to NaN).
import math
import pandas as pd

s = pd.Series([1, 2, 3])

# === with scalars, both ways round ===
assert (s + 1).tolist() == [2, 3, 4]
assert (1 + s).tolist() == [2, 3, 4]
assert (s - 1).tolist() == [0, 1, 2]
assert (10 - s).tolist() == [9, 8, 7]
assert (s * 2).tolist() == [2, 4, 6]
assert (s / 2).tolist() == [0.5, 1.0, 1.5]
assert str((s / 1).dtype) == 'float64'
assert (s // 2).tolist() == [0, 1, 1]
assert str((s // 2).dtype) == 'int64'
assert (s % 2).tolist() == [1, 0, 1]
assert (s ** 2).tolist() == [1, 4, 9]
assert (2 ** s).tolist() == [2, 4, 8]
assert (-s).tolist() == [-1, -2, -3]
assert abs(pd.Series([-1.5, 2.0])).tolist() == [1.5, 2.0]
assert (pd.Series([-7, 7]) // 2).tolist() == [-4, 3]
assert (pd.Series([-7, 7]) % 3).tolist() == [2, 1]
assert (pd.Series([1.5, -1.5]) // 1).tolist() == [1.0, -2.0]
t = s.copy()
t += 10
assert t.tolist() == [11, 12, 13]

# division by zero follows numpy: inf, and NaN for 0/0
z = pd.Series([1, 0, -1]) / 0
assert z.tolist()[0] == math.inf and math.isnan(z.tolist()[1]) and z.tolist()[2] == -math.inf
fz = pd.Series([1, 0]) // 0
assert fz.tolist()[0] == math.inf and math.isnan(fz.tolist()[1])
assert str(fz.dtype) == 'float64'

# === strings ===
names = pd.Series(['a', 'b'])
assert (names + '!').tolist() == ['a!', 'b!']
assert ('<' + names).tolist() == ['<a', '<b']
assert (names * 2).tolist() == ['aa', 'bb']

# === comparisons give bool Series ===
assert (s > 1).tolist() == [False, True, True]
assert (s == 2).tolist() == [False, True, False]
assert (s != 2).tolist() == [True, False, True]
assert (1 < s).tolist() == [False, True, True]
assert str((s >= 2).dtype) == 'bool'
assert ((s > 1) & (s < 3)).tolist() == [False, True, False]
assert ((s < 2) | (s > 2)).tolist() == [True, False, True]
assert (~(s > 1)).tolist() == [True, False, False]
assert ((s > 1) ^ (s > 2)).tolist() == [False, True, False]
nan = pd.Series([1.0, None])
assert (nan > 0).tolist() == [True, False]
assert (nan == nan).tolist() == [True, False]
assert (nan != nan).tolist() == [False, True]
assert (names == 'a').tolist() == [True, False]
try:
    if s > 1:
        pass
    assert False
except ValueError as e:
    assert str(e).startswith('The truth value of a Series is ambiguous')

# === two Series align on their labels ===
a = pd.Series([1, 2, 3], index=['x', 'y', 'z'])
b = pd.Series([10, 20, 30], index=['y', 'z', 'w'])
total = a + b
assert total.index.tolist() == ['w', 'x', 'y', 'z']
assert math.isnan(total['w']) and math.isnan(total['x'])
assert total['y'] == 12.0 and total['z'] == 23.0
assert str(total.dtype) == 'float64'
assert a.add(b, fill_value=0).tolist() == [30.0, 1.0, 12.0, 23.0]
assert (a + pd.Series([1, 1, 1], index=['x', 'y', 'z'])).tolist() == [2, 3, 4]
assert a.sub(1).tolist() == [0, 1, 2] and a.mul(2).tolist() == [2, 4, 6] and a.div(2).tolist() == [0.5, 1.0, 1.5]
assert a.rsub(10).tolist() == [9, 8, 7]
assert a.eq(2).tolist() == [False, True, False] and a.gt(1).tolist() == [False, True, True]
try:
    a == b
    assert False
except ValueError as e:
    assert str(e) == 'Can only compare identically-labeled Series objects'

# === DataFrame arithmetic ===
df = pd.DataFrame({'a': [1, 2], 'b': [3.0, 4.0]})
assert (df * 2).to_dict('list') == {'a': [2, 4], 'b': [6.0, 8.0]}
assert (df + df).to_dict('list') == {'a': [2, 4], 'b': [6.0, 8.0]}
assert (df > 1.5).to_dict('list') == {'a': [False, True], 'b': [True, True]}
assert (df['a'] + df['b']).tolist() == [4.0, 6.0]
assert (df - pd.Series({'a': 1, 'b': 1})).to_dict('list') == {'a': [0, 1], 'b': [2.0, 3.0]}
assert (-df)['a'].tolist() == [-1, -2]
df['c'] = df['a'] * df['b']
assert df['c'].tolist() == [3.0, 8.0]
assert df.sub(df['a'], axis=0)['b'].tolist() == [2.0, 2.0]

# === reductions ===
v = pd.Series([4, 1, 3, 2])
assert v.sum() == 10 and v.min() == 1 and v.max() == 4
assert v.mean() == 2.5 and v.median() == 2.5
assert v.count() == 4 and v.prod() == 24
assert round(v.std(), 10) == round(1.2909944487358056, 10)
assert round(v.var(), 10) == round(1.6666666666666667, 10)
assert v.std(ddof=0) == 1.118033988749895
assert v.idxmax() == 0 and v.idxmin() == 1
assert v.argmax() == 0
assert v.quantile(0.25) == 1.75
assert v.quantile([0.5]).tolist() == [2.5]
assert v.cumsum().tolist() == [4, 5, 8, 10]
assert v.cumprod().tolist() == [4, 4, 12, 24]
assert v.cummax().tolist() == [4, 4, 4, 4]
assert v.cummin().tolist() == [4, 1, 1, 1]
assert v.diff().tolist()[1:] == [-3.0, 2.0, -1.0] and math.isnan(v.diff().tolist()[0])
assert v.shift(1).tolist()[1:] == [4.0, 1.0, 3.0]
assert v.shift(-1, fill_value=0).tolist() == [1, 3, 2, 0]
assert v.pct_change().tolist()[1:] == [1 / 4 - 1, 3 / 1 - 1, 2 / 3 - 1]
assert v.rank().tolist() == [4.0, 1.0, 3.0, 2.0]
assert pd.Series([1, 2, 2, 3]).rank().tolist() == [1.0, 2.5, 2.5, 4.0]
assert pd.Series([1, 2, 2, 3]).rank(method='min').tolist() == [1.0, 2.0, 2.0, 4.0]
assert pd.Series([1, 2, 2, 3]).rank(method='dense').tolist() == [1.0, 2.0, 2.0, 3.0]
assert v.nlargest(2).tolist() == [4, 3] and v.nsmallest(2).index.tolist() == [1, 3]
assert v.between(2, 3).tolist() == [False, False, True, True]
assert v.clip(2, 3).tolist() == [3, 2, 3, 2]
assert v.where(v > 2, 0).tolist() == [4, 0, 3, 0]
assert v.mask(v > 2, 0).tolist() == [0, 1, 0, 2]
assert (v > 0).all() and (v > 3).any() and not (v > 4).any()
assert pd.Series([0.1, 0.2, 0.3]).sum() == 0.6000000000000001
assert pd.Series([1.0, None, 3.0]).sum() == 4.0
assert pd.Series([1.0, None, 3.0]).mean() == 2.0
assert pd.Series([1.0, None, 3.0]).count() == 2
assert math.isnan(pd.Series([1.0, None]).sum(skipna=False))
assert pd.Series([], dtype='float64').sum() == 0
assert math.isnan(pd.Series([], dtype='float64').mean())
assert pd.Series(['a', 'b']).sum() == 'ab'
assert pd.Series(['b', 'a']).max() == 'b'
assert pd.Series([True, True, False]).sum() == 2
assert pd.Series([1.234, 5.678]).round(1).tolist() == [1.2, 5.7]
assert pd.Series([0.5, 1.5, 2.5]).round().tolist() == [0.0, 2.0, 2.0]
assert pd.Series([1, 1, 2]).nunique() == 2
assert list(pd.Series([3, 1, 3]).unique()) == [3, 1]
assert pd.Series([1, 1, 2, 3, 3]).mode().tolist() == [1, 3]

# === rolling and expanding windows ===
w = pd.Series([1, 2, 3, 4, 5])
r = w.rolling(3).mean().tolist()
assert math.isnan(r[0]) and math.isnan(r[1]) and r[2:] == [2.0, 3.0, 4.0]
assert w.rolling(2).sum().tolist()[1:] == [3.0, 5.0, 7.0, 9.0]
assert w.rolling(2, min_periods=1).max().tolist() == [1.0, 2.0, 3.0, 4.0, 5.0]
assert w.expanding().sum().tolist() == [1.0, 3.0, 6.0, 10.0, 15.0]
assert w.expanding().mean().tolist() == [1.0, 1.5, 2.0, 2.5, 3.0]

# === DataFrame reductions over each axis ===
f = pd.DataFrame({'a': [1, 2, 3], 'b': [4.0, 5.0, 6.0], 'c': ['x', 'y', 'z']})
assert f.sum(numeric_only=True).to_dict() == {'a': 6, 'b': 15.0}
assert f.mean(numeric_only=True).to_dict() == {'a': 2.0, 'b': 5.0}
assert f[['a', 'b']].sum(axis=1).tolist() == [5.0, 7.0, 9.0]
assert f[['a', 'b']].max(axis=1).tolist() == [4.0, 5.0, 6.0]
assert f.count().to_dict() == {'a': 3, 'b': 3, 'c': 3}
assert f.min().to_dict() == {'a': 1, 'b': 4.0, 'c': 'x'}
assert f.nunique().to_dict() == {'a': 3, 'b': 3, 'c': 3}
assert f[['a', 'b']].cumsum().to_dict('list') == {'a': [1, 3, 6], 'b': [4.0, 9.0, 15.0]}
assert f[['a', 'b']].corr().loc['a', 'b'] == 1.0
try:
    f.mean()
    assert False
except TypeError:
    pass
desc = f.describe()
assert desc.index.tolist() == ['count', 'mean', 'std', 'min', '25%', '50%', '75%', 'max']
assert desc.columns.tolist() == ['a', 'b']
assert desc.loc['mean', 'a'] == 2.0 and desc.loc['std', 'b'] == 1.0 and desc.loc['25%', 'a'] == 1.5
assert repr(pd.DataFrame({'a': [1, 2, 3]}).describe()) == (
    '         a\ncount  3.0\nmean   2.0\nstd    1.0\nmin    1.0\n25%    1.5\n50%    2.0\n75%    2.5\nmax    3.0'
)
cat = pd.Series(['a', 'b', 'a']).describe()
assert cat.tolist() == [3, 2, 'a', 2] and cat.index.tolist() == ['count', 'unique', 'top', 'freq']

# === apply and map ===
assert s.apply(lambda x: x * 10).tolist() == [10, 20, 30]
assert s.map({1: 'one', 2: 'two'}).tolist()[:2] == ['one', 'two']
assert math.isnan(s.map({1: 'one'}).tolist()[2])
assert s.map(str).tolist() == ['1', '2', '3']
assert f.apply(lambda col: col.max())['a'] == 3
assert f.apply(lambda row: row['a'] + row['b'], axis=1).tolist() == [5.0, 7.0, 9.0]
assert f[['a', 'b']].map(lambda x: x * 2).to_dict('list') == {'a': [2, 4, 6], 'b': [8.0, 10.0, 12.0]}
assert f[['a', 'b']].applymap(lambda x: x + 1)['a'].tolist() == [2, 3, 4]
assert f.agg('sum')['a'] == 6
assert s.agg(['sum', 'max']).to_dict() == {'sum': 6, 'max': 3}
assert f.assign(d=lambda d: d.a * 2)['d'].tolist() == [2, 4, 6]
assert f.pipe(lambda d: d.shape) == (3, 3)
