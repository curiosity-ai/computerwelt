# Extension: `pandas` — missing data (NaN, None, NaT, pd.NA): isna, fillna, dropna,
# interpolate, and how a missing value widens a column's dtype. Also the sandbox's cap on
# what one call may build. Upstream Monty has no pandas. Expected values are pandas 2.2's.
import math
import pandas as pd

# === what counts as missing ===
assert pd.isna(None) and pd.isna(float('nan')) and pd.isna(pd.NaT) and pd.isna(pd.NA)
assert not pd.isna(0) and not pd.isna('') and pd.notna('x')
assert pd.isnull(None) and pd.notnull(1)
assert repr(pd.NaT) == 'NaT' and repr(pd.NA) == '<NA>'
s = pd.Series([1.0, None, 3.0, float('nan')])
assert s.isna().tolist() == [False, True, False, True]
assert s.notna().tolist() == [True, False, True, False]
assert s.isnull().sum() == 2
assert s.hasnans
assert pd.isna(s).tolist() == [False, True, False, True]

# === a missing value widens the dtype ===
assert str(pd.Series([1, 2, None]).dtype) == 'float64'
assert str(pd.Series([True, None]).dtype) == 'object'
assert pd.Series(['a', None]).tolist() == ['a', None]
widened = pd.Series([1, 2], index=['a', 'b']).reindex(['a', 'b', 'c'])
assert str(widened.dtype) == 'float64' and math.isnan(widened['c'])
assert pd.Series([1, 2]).reindex([0, 1, 2], fill_value=0).tolist() == [1, 2, 0]

# === fillna, ffill, bfill ===
assert s.fillna(0).tolist() == [1.0, 0.0, 3.0, 0.0]
assert s.ffill().tolist() == [1.0, 1.0, 3.0, 3.0]
b = s.bfill().tolist()
assert b[:3] == [1.0, 3.0, 3.0] and math.isnan(b[3])
assert s.fillna(method='ffill').tolist() == [1.0, 1.0, 3.0, 3.0]
assert pd.Series([None, None, 1.0]).ffill().isna().tolist() == [True, True, False]
assert pd.Series([1.0, None, None, 4.0]).ffill(limit=1).isna().tolist() == [False, False, True, False]
assert s.fillna(s.mean()).tolist() == [1.0, 2.0, 3.0, 2.0]
assert pd.Series([1.0, None], index=['a', 'b']).fillna({'b': 9.0}).tolist() == [1.0, 9.0]
obj = pd.Series(['x', None])
assert obj.fillna('missing').tolist() == ['x', 'missing']
inplace = s.copy()
assert inplace.fillna(-1, inplace=True) is None
assert inplace.tolist() == [1.0, -1.0, 3.0, -1.0]

# === dropna ===
assert s.dropna().tolist() == [1.0, 3.0]
assert s.dropna().index.tolist() == [0, 2]
df = pd.DataFrame({'a': [1, None, 3], 'b': [None, None, 'z'], 'c': [7, 8, 9]})
assert str(df['a'].dtype) == 'float64'
assert df.isna().sum().to_dict() == {'a': 1, 'b': 2, 'c': 0}
assert df.isna().to_dict('list') == {'a': [False, True, False], 'b': [True, True, False], 'c': [False, False, False]}
assert df.dropna().index.tolist() == [2]
assert df.dropna(subset=['a']).index.tolist() == [0, 2]
assert df.dropna(how='all', subset=['a', 'b']).index.tolist() == [0, 2]
assert df.dropna(thresh=2).index.tolist() == [0, 2]
assert df.dropna(axis=1).columns.tolist() == ['c']
assert df.dropna(ignore_index=True).index.tolist() == [0]
copy = df.copy()
copy.dropna(inplace=True)
assert copy.shape == (1, 3)

# === DataFrame fillna ===
filled = df.fillna(0)
assert filled['a'].tolist() == [1.0, 0.0, 3.0] and filled['b'].tolist() == [0, 0, 'z']
per_column = df.fillna({'a': -1, 'b': 'none'})
assert per_column['a'].tolist() == [1.0, -1.0, 3.0] and per_column['b'].tolist() == ['none', 'none', 'z']
assert df.fillna(df.mean(numeric_only=True))['a'].tolist() == [1.0, 2.0, 3.0]
assert df.ffill()['a'].tolist() == [1.0, 1.0, 3.0]
assert repr(df) == '     a     b  c\n0  1.0  None  7\n1  NaN  None  8\n2  3.0     z  9'

# === reductions skip missing values ===
assert s.sum() == 4.0 and s.mean() == 2.0 and s.count() == 2
assert s.min() == 1.0 and s.max() == 3.0
assert math.isnan(s.sum(skipna=False))
assert math.isnan(pd.Series([None, None], dtype='float64').mean())
assert pd.Series([None, None], dtype='float64').sum() == 0.0
assert math.isnan(pd.Series([None, None], dtype='float64').sum(min_count=1))
assert s.cumsum().tolist()[2] == 4.0 and math.isnan(s.cumsum().tolist()[1])
assert pd.Series([1.0, None, 3.0]).value_counts().to_dict() == {1.0: 1, 3.0: 1}
assert pd.Series([1.0, None, 3.0]).nunique() == 2
assert pd.Series([1.0, None, 3.0]).nunique(dropna=False) == 3

# === interpolate, replace, where, isin with missing values ===
assert pd.Series([1.0, None, 3.0, None]).interpolate().tolist() == [1.0, 2.0, 3.0, 3.0]
assert pd.Series([0, 1, 0]).replace(0, None).isna().tolist() == [True, False, True]
assert pd.Series([1.0, None]).replace(float('nan'), 0).tolist() == [1.0, 0.0]
assert pd.Series(['a', 'b']).replace({'a': 'A'}).tolist() == ['A', 'b']
assert pd.Series([1, 2, 3]).replace([1, 2], [10, 20]).tolist() == [10, 20, 3]
assert pd.DataFrame({'x': ['?', 'k']}).replace('?', None)['x'].isna().tolist() == [True, False]
assert pd.Series([1.0, None]).isin([1.0]).tolist() == [True, False]

# === NaT in datetime columns ===
times = pd.to_datetime(pd.Series(['2024-01-01', None]))
assert times.isna().tolist() == [False, True]
assert repr(times) == '0   2024-01-01\n1          NaT\ndtype: datetime64[ns]'
assert times.dt.year.tolist()[0] == 2024.0 and math.isnan(times.dt.year.tolist()[1])

# === astype refuses NaN for int, as numpy does ===
try:
    pd.Series([1.0, None]).astype(int)
    assert False
except ValueError as e:
    assert str(e) == 'Cannot convert non-finite values (NA or inf) to integer'
assert pd.Series([1.0, 2.0]).astype(int).tolist() == [1, 2]
assert str(pd.Series([1.9]).astype('int64').dtype) == 'int64'
assert pd.Series([1.9, -1.9]).astype(int).tolist() == [1, -1]
assert pd.Series([1, 0]).astype(bool).tolist() == [True, False]
assert pd.Series([1.5, None]).astype(str).tolist() == ['1.5', 'nan']
assert pd.to_numeric(pd.Series(['1', 'x', '2.5']), errors='coerce').isna().tolist() == [False, True, False]
assert pd.to_numeric(pd.Series(['1', '2'])).tolist() == [1, 2]
try:
    pd.to_numeric(pd.Series(['x']))
    assert False
except ValueError:
    pass

# === the sandbox caps what one call may build ===
try:
    pd.Series(0, index=range(1 << 25))
    assert False
except MemoryError:
    pass
try:
    pd.DataFrame({'a': range(5000)}).merge(pd.DataFrame({'a': [0] * 5000}), how='cross')
    assert False
except MemoryError:
    pass
