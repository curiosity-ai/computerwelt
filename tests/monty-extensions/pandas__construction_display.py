# Extension: `pandas` — construction, dtypes and the text display. Upstream Monty has no
# pandas at all; importing it fails there. This port's pandas is a C# subset that follows
# pandas 2.2 (strings are `object` dtype). Every expected value and repr below is what
# pandas 2.2 prints for the same input.
import pandas as pd

# === Series from a list, with the default RangeIndex ===
s = pd.Series([1, 2, 3])
assert s.tolist() == [1, 2, 3]
assert str(s.dtype) == 'int64'
assert s.dtype == 'int64'
assert s.shape == (3,)
assert s.size == 3
assert len(s) == 3
assert not s.empty
assert s.name is None
assert repr(s) == '0    1\n1    2\n2    3\ndtype: int64'
assert repr(s.index) == 'RangeIndex(start=0, stop=3, step=1)'

# === dtype inference ===
assert str(pd.Series([1.5, 2.0]).dtype) == 'float64'
assert str(pd.Series([1, None]).dtype) == 'float64'
assert str(pd.Series([True, False]).dtype) == 'bool'
assert str(pd.Series(['a', 'b']).dtype) == 'object'
assert str(pd.Series(['a', None]).dtype) == 'object'
assert str(pd.Series([True, 1]).dtype) == 'object'
assert str(pd.Series([], dtype='float64').dtype) == 'float64'
assert pd.Series([1, None]).tolist()[0] == 1.0

# === Series from a dict, a scalar, with a name ===
d = pd.Series({'x': 1, 'y': 2}, name='n')
assert d.index.tolist() == ['x', 'y']
assert d['y'] == 2
assert repr(d) == 'x    1\ny    2\nName: n, dtype: int64'
b = pd.Series(5, index=['a', 'b', 'c'])
assert b.tolist() == [5, 5, 5]
r = pd.Series({'a': 1, 'b': 2}, index=['b', 'c'])
assert r['b'] == 2.0
assert pd.isna(r['c'])

# === Series display: floats share one number of decimals, NaN is NaN ===
assert repr(pd.Series([1.0, 2.5, 3.25], name='v', index=['a', 'b', 'c'])) == 'a    1.00\nb    2.50\nc    3.25\nName: v, dtype: float64'
assert repr(pd.Series([1.0, None])) == '0    1.0\n1    NaN\ndtype: float64'
assert repr(pd.Series([-1, 2])) == '0   -1\n1    2\ndtype: int64'
assert repr(pd.Series(['a', 'bb'])) == '0     a\n1    bb\ndtype: object'
assert repr(pd.Series([True, False])) == '0     True\n1    False\ndtype: bool'
assert repr(pd.Series([10, 200], index=['a', 'bbb'])) == 'a       10\nbbb    200\ndtype: int64'
assert repr(pd.Series([0.1, 0.25, 1e-8])) == '0    1.000000e-01\n1    2.500000e-01\n2    1.000000e-08\ndtype: float64'
assert repr(pd.Series([1 / 3, 2.0])) == '0    0.333333\n1    2.000000\ndtype: float64'
assert repr(pd.Series([], dtype='float64')) == 'Series([], dtype: float64)'

# === a long Series shows its first and last five ===
long = repr(pd.Series(range(100)))
assert long.splitlines()[0] == '0      0'
assert long.splitlines()[5] == '      ..'
assert long.splitlines()[-1] == 'Length: 100, dtype: int64'

# === DataFrame from a dict of lists ===
df = pd.DataFrame({'a': [1, 2, 3], 'b': ['x', 'yy', 'z'], 'c': [1.5, 2.0, None]})
assert df.shape == (3, 3)
assert df.columns.tolist() == ['a', 'b', 'c']
assert [str(t) for t in df.dtypes] == ['int64', 'object', 'float64']
assert repr(df) == '   a   b    c\n0  1   x  1.5\n1  2  yy  2.0\n2  3   z  NaN'
assert str(df) == repr(df)
assert len(df) == 3
assert df.size == 9
assert list(df) == ['a', 'b', 'c']
assert 'a' in df and 'q' not in df
assert repr(df.columns) == "Index(['a', 'b', 'c'], dtype='object')"
assert repr(df.dtypes) == 'a      int64\nb     object\nc    float64\ndtype: object'

# === DataFrame from records and from rows ===
recs = pd.DataFrame([{'a': 1, 'b': 2}, {'a': 3}])
assert recs.columns.tolist() == ['a', 'b']
assert str(recs['b'].dtype) == 'float64'
assert repr(recs) == '   a    b\n0  1  2.0\n1  3  NaN'
rows = pd.DataFrame([[1, 2], [3, 4]], columns=['p', 'q'], index=['r1', 'r2'])
assert repr(rows) == '    p  q\nr1  1  2\nr2  3  4'
assert rows.loc['r2', 'q'] == 4
scalars = pd.DataFrame({'a': [1, 2], 'k': 'same'})
assert scalars['k'].tolist() == ['same', 'same']
try:
    pd.DataFrame({'a': 1, 'b': 2})
    assert False
except ValueError as e:
    assert str(e) == 'If using all scalar values, you must pass an index'
try:
    pd.DataFrame({'a': [1, 2], 'b': [1]})
    assert False
except ValueError as e:
    assert str(e) == 'All arrays must be of the same length'

# === an index name shows on its own line ===
named = pd.DataFrame({'a': [1, 2], 'b': ['x', 'yy']}).set_index('a')
assert repr(named) == '    b\na    \n1   x\n2  yy'

# === empty frames ===
assert repr(pd.DataFrame()) == 'Empty DataFrame\nColumns: []\nIndex: []'
empty = pd.DataFrame(columns=['a', 'b'])
assert empty.empty and empty.shape == (0, 2)

# === a frame past sixty rows shows head, tail and its dimensions ===
tall = repr(pd.DataFrame({'x': range(100)}))
assert tall.splitlines()[:3] == ['     x', '0    0', '1    1']
assert tall.splitlines()[6] == '..  ..'
assert tall.splitlines()[-1] == '[100 rows x 1 columns]'

# === head, tail, T, copy, to_string ===
assert df.head(2).index.tolist() == [0, 1]
assert df.tail(1)['b'].tolist() == ['z']
assert df.T.shape == (3, 3)
assert df.T.loc['b'].tolist() == ['x', 'yy', 'z']
c = df.copy()
c.loc[0, 'a'] = 100
assert df.loc[0, 'a'] == 1
assert df.to_string() == repr(df)

# === Index objects ===
assert repr(pd.Index([1, 2, 3])) == 'Index([1, 2, 3], dtype=\'int64\')'
assert repr(pd.Index([1.5, 2.0])) == "Index([1.5, 2.0], dtype='float64')"
assert repr(pd.Index(['a'], name='k')) == "Index(['a'], dtype='object', name='k')"
assert pd.RangeIndex(5).tolist() == [0, 1, 2, 3, 4]
assert isinstance(df, pd.DataFrame) and isinstance(df['a'], pd.Series)
assert isinstance(df.index, pd.Index)

# === _repr_html_ is a pandas-style table ===
html = df._repr_html_()
assert html.startswith('<div>')
assert '<table border="1" class="dataframe">' in html
assert '<th>a</th>' in html and '<td>yy</td>' in html and '<td>NaN</td>' in html
assert df.to_html().startswith('<table border="1" class="dataframe">')

# === info() prints a summary ===
import io
assert df.info() is None

# === the version this subset follows ===
assert pd.__version__.startswith('2.')
