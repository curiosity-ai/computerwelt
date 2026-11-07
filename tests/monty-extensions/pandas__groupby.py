# Extension: `pandas` — groupby: reductions, agg (names, lists, dicts, named aggregation),
# apply, transform, filter, iteration and as_index=False. Upstream Monty has no pandas.
# Expected values are pandas 2.2's: keys sorted, missing keys dropped, several keys give a
# key per level (here a tuple-labelled index with one name per level).
import pandas as pd

df = pd.DataFrame({
    'city': ['NY', 'LA', 'NY', 'SF', 'LA', 'NY'],
    'year': [2020, 2020, 2021, 2021, 2021, 2022],
    'sales': [100, 200, 150, 300, 250, 120],
    'cost': [80.5, 150.0, 120.25, 200.0, 210.0, 90.0],
})

# === one key, one column ===
g = df.groupby('city')['sales']
total = g.sum()
assert total.index.tolist() == ['LA', 'NY', 'SF']
assert total.tolist() == [450, 370, 300]
assert total.index.name == 'city' and total.name == 'sales'
assert str(total.dtype) == 'int64'
assert repr(total) == 'city\nLA    450\nNY    370\nSF    300\nName: sales, dtype: int64'
assert g.mean().tolist() == [225.0, 370 / 3, 300.0]
assert g.count().tolist() == [2, 3, 1]
assert g.min().tolist() == [200, 100, 300] and g.max().tolist() == [250, 150, 300]
assert g.first().tolist() == [200, 100, 300] and g.last().tolist() == [250, 120, 300]
assert g.median().tolist() == [225.0, 120.0, 300.0]
assert g.nunique().tolist() == [2, 3, 1]
assert g.std().tolist()[0] == 35.35533905932738
assert df.groupby('city').sales.sum().tolist() == [450, 370, 300]

# === sizes ===
size = df.groupby('city').size()
assert size.tolist() == [2, 3, 1] and size.name is None
assert repr(size) == 'city\nLA    2\nNY    3\nSF    1\ndtype: int64'
assert len(df.groupby('city')) == 3
assert df.groupby('city').ngroups == 3

# === the whole frame ===
sums = df.groupby('city').sum()
assert sums.columns.tolist() == ['year', 'sales', 'cost']
assert sums.loc['LA', 'cost'] == 360.0
means = df.groupby('city')[['sales', 'cost']].mean()
assert means.columns.tolist() == ['sales', 'cost']
assert means.loc['SF'].tolist() == [300.0, 200.0]
counted = df.groupby('city').count()
assert counted['sales'].tolist() == [2, 3, 1]
strs = pd.DataFrame({'k': ['a', 'a', 'b'], 'name': ['x', 'y', 'z'], 'v': [1, 2, 3]})
assert strs.groupby('k').sum()['name'].tolist() == ['xy', 'z']
assert strs.groupby('k').mean(numeric_only=True).columns.tolist() == ['v']
try:
    strs.groupby('k').mean()
    assert False
except TypeError:
    pass

# === agg in every spelling ===
assert df.groupby('city')['sales'].agg('sum').tolist() == [450, 370, 300]
multi = df.groupby('city')['sales'].agg(['sum', 'mean', 'count'])
assert multi.columns.tolist() == ['sum', 'mean', 'count']
assert multi.loc['NY', 'count'] == 3
by_column = df.groupby('city').agg({'sales': 'sum', 'cost': 'mean'})
assert by_column.columns.tolist() == ['sales', 'cost']
assert by_column.loc['LA'].tolist() == [450.0, 180.0]
named = df.groupby('city').agg(total=('sales', 'sum'), avg=('cost', 'mean'), n=('sales', 'size'))
assert named.columns.tolist() == ['total', 'avg', 'n']
assert named['total'].tolist() == [450, 370, 300] and named['n'].tolist() == [2, 3, 1]
named2 = df.groupby('city').agg(top=pd.NamedAgg(column='sales', aggfunc='max'))
assert named2['top'].tolist() == [250, 150, 300]
lists = df.groupby('city').agg({'sales': ['min', 'max']})
assert lists.columns.tolist() == [('sales', 'min'), ('sales', 'max')]
assert lists[('sales', 'max')].tolist() == [250, 150, 300]
assert df.groupby('city')['sales'].agg(lambda s: s.max() - s.min()).tolist() == [50, 50, 0]
assert df.groupby('city')['sales'].agg(spread=lambda s: s.max() - s.min())['spread'].tolist() == [50, 50, 0]

# === several keys ===
two = df.groupby(['city', 'year'])['sales'].sum()
assert two.index.names == ['city', 'year']
assert two.index.tolist() == [('LA', 2020), ('LA', 2021), ('NY', 2020), ('NY', 2021), ('NY', 2022), ('SF', 2021)]
assert two.loc[('NY', 2021)] == 150
assert two['NY'].tolist() == [100, 150, 120]
assert two['NY'].index.tolist() == [2020, 2021, 2022]
flat = two.reset_index()
assert flat.columns.tolist() == ['city', 'year', 'sales']
assert flat['city'].tolist() == ['LA', 'LA', 'NY', 'NY', 'NY', 'SF']
assert repr(two).splitlines()[:3] == ['city  year', 'LA    2020    200', '      2021    250']

# === as_index=False keeps the keys as columns ===
kept = df.groupby('city', as_index=False)['sales'].sum()
assert kept.columns.tolist() == ['city', 'sales']
assert kept.index.tolist() == [0, 1, 2]
assert kept['sales'].tolist() == [450, 370, 300]
sized = df.groupby('city', as_index=False).size()
assert sized.columns.tolist() == ['city', 'size'] and sized['size'].tolist() == [2, 3, 1]

# === sort=False keeps first appearance ===
assert df.groupby('city', sort=False)['sales'].sum().index.tolist() == ['NY', 'LA', 'SF']

# === missing keys are dropped unless dropna=False ===
holes = pd.DataFrame({'k': ['a', None, 'a'], 'v': [1, 2, 3]})
assert holes.groupby('k')['v'].sum().to_dict() == {'a': 4}
assert len(holes.groupby('k', dropna=False)['v'].sum()) == 2

# === iteration, get_group, groups ===
seen = [(key, len(part)) for key, part in df.groupby('city')]
assert seen == [('LA', 2), ('NY', 3), ('SF', 1)]
assert [key for key, _ in df.groupby(['city'])] == [('LA',), ('NY',), ('SF',)]
assert [key for key, _ in df.groupby(['city', 'year'])][0] == ('LA', 2020)
assert df.groupby('city').get_group('SF')['sales'].tolist() == [300]
assert df.groupby('city').groups['NY'].tolist() == [0, 2, 5]

# === apply, transform, filter ===
peak = df.groupby('city').apply(lambda part: part['sales'].max())
assert peak.tolist() == [250, 150, 300] and peak.index.tolist() == ['LA', 'NY', 'SF']
share = df.groupby('city')['sales'].transform('sum')
assert share.tolist() == [370, 450, 370, 300, 450, 370]
assert share.index.tolist() == [0, 1, 2, 3, 4, 5]
centred = df.groupby('city')['sales'].transform(lambda s: s - s.mean())
assert centred.tolist()[3] == 0.0 and centred.tolist()[1] == -25.0
busy = df.groupby('city').filter(lambda part: len(part) > 1)
assert busy.index.tolist() == [0, 1, 2, 4, 5]
assert df.groupby('city')['sales'].cumsum().tolist() == [100, 200, 250, 300, 450, 370]
assert df.groupby('city').cumcount().tolist() == [0, 0, 1, 0, 1, 2]
assert df.groupby('city').head(1).index.tolist() == [0, 1, 3]
assert df.groupby('city')['sales'].shift(1).tolist()[2] == 100.0

# === grouping by a Series, or by something computed ===
assert df.groupby(df['year'] > 2020)['sales'].sum().to_dict() == {False: 300, True: 820}
assert df['sales'].groupby(df['city']).sum().tolist() == [450, 370, 300]

# === value_counts per group ===
vc = df.groupby('city')['year'].value_counts()
assert vc.loc[('NY', 2021)] == 1
assert vc.name == 'count'
