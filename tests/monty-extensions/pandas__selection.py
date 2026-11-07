# Extension: `pandas` — selecting and assigning: [], .loc, .iloc, .at, .iat, masks and
# slices. Upstream Monty has no pandas. Expected values are pandas 2.2's; writes follow
# copy-on-write, so a Series taken out of a frame and changed leaves the frame alone.
import pandas as pd

df = pd.DataFrame(
    {'a': [1, 2, 3, 4], 'b': [10.0, 20.0, 30.0, 40.0], 'c': ['w', 'x', 'y', 'z']},
    index=['p', 'q', 'r', 's'],
)

# === columns ===
assert df['a'].tolist() == [1, 2, 3, 4]
assert df.a.name == 'a'
assert df[['c', 'a']].columns.tolist() == ['c', 'a']
try:
    df['zz']
    assert False
except KeyError as e:
    assert e.args[0] == 'zz'
try:
    df[['a', 'zz']]
    assert False
except KeyError as e:
    assert e.args[0] == "['zz'] not in index"

# === rows by mask and by slice ===
assert df[df['a'] > 2].index.tolist() == ['r', 's']
assert df[(df.a > 1) & (df.c != 'z')].index.tolist() == ['q', 'r']
assert df[(df.a == 1) | (df.a == 4)]['c'].tolist() == ['w', 'z']
assert df[~(df.a > 1)].index.tolist() == ['p']
assert df[1:3].index.tolist() == ['q', 'r']
assert df[df['c'].isin(['x', 'z'])]['a'].tolist() == [2, 4]

# === .loc by label: scalar, row, slices (inclusive), lists, masks ===
assert df.loc['q', 'b'] == 20.0
row = df.loc['q']
assert row.name == 'q'
assert row.tolist() == [2, 20.0, 'x']
assert str(row.dtype) == 'object'
assert df.loc['q':'s', 'a'].tolist() == [2, 3, 4]
assert df.loc['q':'r', ['a', 'c']].shape == (2, 2)
assert df.loc[['s', 'p']]['a'].tolist() == [4, 1]
assert df.loc[df.a >= 3, 'c'].tolist() == ['y', 'z']
assert df.loc[:, 'b'].tolist() == [10.0, 20.0, 30.0, 40.0]
assert df.loc[lambda d: d.a == 2].index.tolist() == ['q']
try:
    df.loc['nope']
    assert False
except KeyError:
    pass

# === .iloc by position ===
assert df.iloc[0].tolist() == [1, 10.0, 'w']
assert df.iloc[-1]['c'] == 'z'
assert df.iloc[1:3].index.tolist() == ['q', 'r']
assert df.iloc[[0, 2], [0, 2]].to_dict('list') == {'a': [1, 3], 'c': ['w', 'y']}
assert df.iloc[[0, 2], 0].tolist() == [1, 3]
assert df.iloc[2, 1] == 30.0
assert df.iat[0, 2] == 'w' and df.at['s', 'a'] == 4
try:
    df.iloc[10]
    assert False
except IndexError as e:
    assert str(e) == 'single positional indexer is out-of-bounds'

# === a numeric row keeps a numeric dtype ===
nums = pd.DataFrame({'x': [1, 2], 'y': [0.5, 1.5]})
assert str(nums.iloc[0].dtype) == 'float64'
assert nums.iloc[0].tolist() == [1.0, 0.5]
assert str(pd.DataFrame({'x': [1], 'y': [2]}).iloc[0].dtype) == 'int64'

# === assignment ===
w = df.copy()
w['d'] = w['a'] * 2
assert w['d'].tolist() == [2, 4, 6, 8]
w['e'] = 0
assert w['e'].tolist() == [0, 0, 0, 0]
w['f'] = ['i', 'j', 'k', 'l']
assert w.columns.tolist() == ['a', 'b', 'c', 'd', 'e', 'f']
w.loc['q', 'b'] = 99.5
assert w.loc['q', 'b'] == 99.5
w.loc[w.a > 2, 'c'] = 'big'
assert w['c'].tolist() == ['w', 'x', 'big', 'big']
w.loc[w.a == 1, 'new'] = 1.0
assert w['new'].tolist()[0] == 1.0 and pd.isna(w['new'].tolist()[1])
w.iloc[0, 0] = 100
assert w['a'].tolist() == [100, 2, 3, 4]
w.at['s', 'a'] = 7
assert w.loc['s', 'a'] == 7
w.loc['t'] = [5, 50.0, 'v', 10, 0, 'm', 2.0]
assert w.index.tolist() == ['p', 'q', 'r', 's', 't']
assert str(w['a'].dtype) == 'int64'
assert w.loc['t', 'c'] == 'v'
try:
    w['g'] = [1, 2]
    assert False
except ValueError as e:
    assert str(e) == 'Length of values (2) does not match length of index (5)'

# setting a float into an int column widens it
z = pd.DataFrame({'n': [1, 2, 3]})
z.loc[1, 'n'] = 2.5
assert str(z['n'].dtype) == 'float64'
assert z['n'].tolist() == [1.0, 2.5, 3.0]

# === copy-on-write: a column taken out is its own copy once written ===
col = df['b']
col.iloc[0] = -1.0
assert df['b'].tolist()[0] == 10.0
assert col.tolist()[0] == -1.0

# === deleting and dropping ===
v = df.copy()
del v['b']
assert v.columns.tolist() == ['a', 'c']
assert df.drop(columns=['b']).columns.tolist() == ['a', 'c']
assert df.drop('p').index.tolist() == ['q', 'r', 's']
assert df.drop(['a', 'c'], axis=1).columns.tolist() == ['b']
assert df.drop(index=['p', 'q']).shape == (2, 3)
popped = v.pop('c')
assert popped.tolist() == ['w', 'x', 'y', 'z'] and v.columns.tolist() == ['a']

# === Series selection ===
s = pd.Series([10, 20, 30], index=['a', 'b', 'c'])
assert s['b'] == 20
assert s[['c', 'a']].tolist() == [30, 10]
assert s[s > 15].index.tolist() == ['b', 'c']
assert s['a':'b'].tolist() == [10, 20]
assert s[1:].tolist() == [20, 30]
assert s.loc['c'] == 30 and s.iloc[0] == 10
assert s.iloc[-1] == 30
assert s.get('zz', 0) == 0
assert 'a' in s and 'zz' not in s
s2 = s.copy()
s2['d'] = 40
assert s2.index.tolist() == ['a', 'b', 'c', 'd']
s2[s2 > 25] = 0
assert s2.tolist() == [10, 20, 0, 0]
s3 = pd.Series([5, 6, 7])
assert s3[0] == 5
try:
    s3[10]
    assert False
except KeyError:
    pass

# === set_index / reset_index ===
k = df.reset_index()
assert k.columns.tolist() == ['index', 'a', 'b', 'c']
assert k.index.tolist() == [0, 1, 2, 3]
assert df.reset_index(drop=True).columns.tolist() == ['a', 'b', 'c']
si = df.set_index('c')
assert si.index.name == 'c' and si.index.tolist() == ['w', 'x', 'y', 'z']
assert si.loc['y', 'a'] == 3
back = si.reset_index()
assert back.columns.tolist() == ['c', 'a', 'b']
renamed = df.rename(columns={'a': 'A'}, index={'p': 'P'})
assert renamed.columns.tolist() == ['A', 'b', 'c'] and renamed.index.tolist()[0] == 'P'
cols = df.copy()
cols.columns = ['x', 'y', 'z']
assert cols.columns.tolist() == ['x', 'y', 'z']
cols.index = [0, 1, 2, 3]
assert cols.loc[2, 'x'] == 3

# === iteration ===
assert [c for c in df] == ['a', 'b', 'c']
assert [label for label, _ in df.iterrows()] == ['p', 'q', 'r', 's']
assert [r['a'] for _, r in df.iterrows()] == [1, 2, 3, 4]
tuples = list(df.itertuples())
assert tuples[0].Index == 'p' and tuples[0].a == 1 and tuples[0].c == 'w'
assert list(df.itertuples(index=False, name=None))[1] == (2, 20.0, 'x')
assert [name for name, _ in df.items()] == ['a', 'b', 'c']
assert list(s.items()) == [('a', 10), ('b', 20), ('c', 30)]

# === query ===
assert df.query('a > 2').index.tolist() == ['r', 's']
assert df.query('a > 1 and c != "z"').index.tolist() == ['q', 'r']
assert df.query("c in ['w', 'y']")['a'].tolist() == [1, 3]
assert df.query('b / 10 == a').shape == (4, 3)

# === sample draws as numpy's legacy RandomState(seed).permutation does ===
ten = pd.DataFrame({'a': range(10)})
assert ten.sample(n=10, random_state=42).index.tolist() == [8, 1, 5, 0, 7, 2, 9, 4, 3, 6]
assert ten.sample(n=3, random_state=0).index.tolist() == [2, 8, 4]
assert ten.sample(frac=0.5, random_state=7).equals(ten.sample(frac=0.5, random_state=7))
assert len(ten.sample(frac=0.3, random_state=1)) == 3
assert ten.sample(n=2, random_state=0, ignore_index=True).index.tolist() == [0, 1]
assert pd.Series(range(5)).sample(frac=1, random_state=1).tolist() == [2, 1, 4, 0, 3]
