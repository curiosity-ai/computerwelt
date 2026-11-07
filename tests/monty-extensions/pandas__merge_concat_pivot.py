# Extension: `pandas` — merge, join, concat, pivot_table, pivot, melt, get_dummies,
# crosstab, drop_duplicates and sorting. Upstream Monty has no pandas. Expected values are
# pandas 2.2's: an inner or left merge keeps the left's row order, an outer merge sorts keys.
import math
import pandas as pd

left = pd.DataFrame({'k': [1, 2, 3], 'a': ['x', 'y', 'z']})
right = pd.DataFrame({'k': [2, 3, 4], 'b': [20, 30, 40]})

# === merge, every how ===
inner = pd.merge(left, right, on='k')
assert inner.to_dict('list') == {'k': [2, 3], 'a': ['y', 'z'], 'b': [20, 30]}
assert inner.index.tolist() == [0, 1]
lj = left.merge(right, on='k', how='left')
assert lj['k'].tolist() == [1, 2, 3]
assert math.isnan(lj['b'].tolist()[0]) and lj['b'].tolist()[1:] == [20.0, 30.0]
assert str(lj['b'].dtype) == 'float64'
rj = pd.merge(left, right, on='k', how='right')
assert rj['k'].tolist() == [2, 3, 4] and rj['a'].tolist()[:2] == ['y', 'z']
oj = pd.merge(left, right, on='k', how='outer')
assert oj['k'].tolist() == [1, 2, 3, 4]
assert repr(oj) == '   k    a     b\n0  1    x   NaN\n1  2    y  20.0\n2  3    z  30.0\n3  4  NaN  40.0'
assert pd.merge(left, right, how='cross').shape == (9, 4)

# shared names get suffixes; different key names keep both columns
l2 = pd.DataFrame({'id': [1, 2], 'v': [1, 2]})
r2 = pd.DataFrame({'key': [2, 1], 'v': [20, 10]})
both = pd.merge(l2, r2, left_on='id', right_on='key')
assert both.columns.tolist() == ['id', 'v_x', 'key', 'v_y']
assert both['v_y'].tolist() == [10, 20]
custom = pd.merge(l2, r2, left_on='id', right_on='key', suffixes=('_l', '_r'))
assert custom.columns.tolist() == ['id', 'v_l', 'key', 'v_r']
ind = pd.merge(left, right, on='k', how='outer', indicator=True)
assert ind['_merge'].tolist() == ['left_only', 'both', 'both', 'right_only']

# several keys
lk = pd.DataFrame({'a': [1, 1, 2], 'b': ['x', 'y', 'x'], 'v': [1, 2, 3]})
rk = pd.DataFrame({'a': [1, 2], 'b': ['y', 'x'], 'w': [10, 20]})
assert pd.merge(lk, rk, on=['a', 'b'])['w'].tolist() == [10, 20]

# === join on the index ===
li = pd.DataFrame({'a': [1, 2]}, index=['x', 'y'])
ri = pd.DataFrame({'b': [3, 4]}, index=['y', 'z'])
joined = li.join(ri)
assert joined.index.tolist() == ['x', 'y']
assert math.isnan(joined.loc['x', 'b']) and joined.loc['y', 'b'] == 3.0
assert li.join(ri, how='inner').index.tolist() == ['y']
try:
    li.join(li)
    assert False
except ValueError:
    pass
assert li.join(li, lsuffix='_l', rsuffix='_r').columns.tolist() == ['a_l', 'a_r']

# === concat ===
stacked = pd.concat([left, left])
assert stacked.index.tolist() == [0, 1, 2, 0, 1, 2]
assert pd.concat([left, left], ignore_index=True).index.tolist() == [0, 1, 2, 3, 4, 5]
mixed = pd.concat([left, right], ignore_index=True)
assert mixed.columns.tolist() == ['k', 'a', 'b']
assert math.isnan(mixed['b'].tolist()[0]) and mixed['a'].tolist()[3] != mixed['a'].tolist()[3]
side = pd.concat([left, right], axis=1)
assert side.columns.tolist() == ['k', 'a', 'k', 'b']
series = pd.concat([pd.Series([1, 2]), pd.Series([3])])
assert series.tolist() == [1, 2, 3] and series.index.tolist() == [0, 1, 0]
cols = pd.concat([pd.Series([1, 2], name='x'), pd.Series([3, 4], name='y')], axis=1)
assert cols.columns.tolist() == ['x', 'y'] and cols['y'].tolist() == [3, 4]
assert pd.concat([left, right], join='inner', ignore_index=True).columns.tolist() == ['k']
keyed = pd.concat([left, left], keys=['first', 'second'])
assert keyed.index.tolist()[3] == ('second', 0)

# === pivot_table ===
sales = pd.DataFrame({
    'city': ['NY', 'LA', 'NY', 'SF', 'LA', 'NY'],
    'year': [2020, 2020, 2021, 2021, 2021, 2021],
    'sales': [100, 200, 150, 300, 250, 50],
})
pt = sales.pivot_table(index='city', columns='year', values='sales', aggfunc='sum', fill_value=0)
assert pt.index.tolist() == ['LA', 'NY', 'SF']
assert pt.columns.tolist() == [2020, 2021]
assert pt.loc['NY'].tolist() == [100, 200]
assert pt.loc['SF', 2020] == 0
assert pt.columns.name == 'year' and pt.index.name == 'city'
mean = pd.pivot_table(sales, index='city', values='sales')
assert mean['sales'].tolist() == [225.0, 100.0, 300.0]
wide = sales.pivot_table(index='city', columns='year', values='sales', aggfunc='sum')
assert math.isnan(wide.loc['SF', 2020]) and wide.loc['LA', 2021] == 250.0

# === pivot and melt ===
long = pd.DataFrame({'d': ['a', 'a', 'b', 'b'], 'k': ['x', 'y', 'x', 'y'], 'v': [1, 2, 3, 4]})
p = long.pivot(index='d', columns='k', values='v')
assert p.to_dict() == {'x': {'a': 1, 'b': 3}, 'y': {'a': 2, 'b': 4}}
assert p.columns.name == 'k'
try:
    pd.DataFrame({'d': ['a', 'a'], 'k': ['x', 'x'], 'v': [1, 2]}).pivot(index='d', columns='k', values='v')
    assert False
except ValueError as e:
    assert str(e) == 'Index contains duplicate entries, cannot reshape'
melted = pd.DataFrame({'id': [1, 2], 'x': [10, 20], 'y': [30, 40]}).melt(id_vars='id')
assert melted.columns.tolist() == ['id', 'variable', 'value']
assert melted['variable'].tolist() == ['x', 'x', 'y', 'y']
assert melted['value'].tolist() == [10, 20, 30, 40]
named = pd.melt(pd.DataFrame({'id': [1], 'x': [5]}), id_vars=['id'], var_name='col', value_name='val')
assert named.columns.tolist() == ['id', 'col', 'val']

# === get_dummies and crosstab ===
dummies = pd.get_dummies(pd.Series(['b', 'a', 'b']))
assert dummies.columns.tolist() == ['a', 'b']
assert dummies['b'].tolist() == [True, False, True]
framed = pd.get_dummies(pd.DataFrame({'c': ['x', 'y'], 'n': [1, 2]}))
assert framed.columns.tolist() == ['n', 'c_x', 'c_y']
assert pd.get_dummies(pd.Series(['a', 'b']), dtype=int)['a'].tolist() == [1, 0]
ct = pd.crosstab(long['d'], long['k'])
assert ct.loc['a', 'x'] == 1 and ct.loc['b', 'y'] == 1

# === duplicates and sorting ===
dup = pd.DataFrame({'a': [1, 1, 2, 1], 'b': ['x', 'x', 'y', 'z']})
assert dup.duplicated().tolist() == [False, True, False, False]
assert dup.duplicated(subset=['a']).tolist() == [False, True, False, True]
assert dup.drop_duplicates().index.tolist() == [0, 2, 3]
assert dup.drop_duplicates(subset='a', keep='last').index.tolist() == [2, 3]
assert dup.drop_duplicates(subset=['a'], keep=False).index.tolist() == [2]
assert dup.sort_values('b', ascending=False)['b'].tolist() == ['z', 'y', 'x', 'x']
assert dup.sort_values(['a', 'b'], ascending=[True, False]).index.tolist() == [3, 0, 1, 2]
assert dup.sort_index(ascending=False).index.tolist() == [3, 2, 1, 0]
nans = pd.DataFrame({'v': [2.0, None, 1.0]})
assert nans.sort_values('v').index.tolist() == [2, 0, 1]
assert nans.sort_values('v', na_position='first').index.tolist() == [1, 2, 0]
assert dup.value_counts().tolist() == [2, 1, 1]
assert dup['a'].value_counts().to_dict() == {1: 3, 2: 1}
assert dup['a'].value_counts(normalize=True).to_dict() == {1: 0.75, 2: 0.25}
assert dup.nlargest(2, 'a').index.tolist() == [2, 0]
assert dup.nsmallest(1, 'a').index.tolist() == [0]
assert dup['b'].value_counts().index.tolist() == ['x', 'y', 'z']
