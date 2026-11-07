# Extension: `pandas` — read_csv / to_csv and read_json / to_json, over the sandbox's own
# filesystem (the fixture harness's `/virtual`) and over `io.StringIO`. Upstream Monty has
# no pandas. Expected values are pandas 2.2's; nothing here reaches outside the virtual
# filesystem, and a URL is refused.
import io
import json
import pandas as pd

df = pd.DataFrame({
    'name': ['Ann', 'Bob', 'Lee, Jr'],
    'age': [30, None, 45],
    'score': [88.5, 92.0, 79.25],
    'active': [True, False, True],
})

# === to_csv returns text when given no path ===
text = df.to_csv(index=False)
assert text == 'name,age,score,active\nAnn,30.0,88.5,True\nBob,,92.0,False\n"Lee, Jr",45.0,79.25,True\n'
assert df.to_csv().splitlines()[0] == ',name,age,score,active'
assert df.to_csv().splitlines()[1] == '0,Ann,30.0,88.5,True'
assert df.to_csv(sep=';', index=False, header=False).splitlines()[0] == 'Ann;30.0;88.5;True'
assert df.to_csv(index=False, na_rep='NA').splitlines()[2] == 'Bob,NA,92.0,False'
assert df.to_csv(index=False, columns=['name']).splitlines() == ['name', 'Ann', 'Bob', '"Lee, Jr"']
assert df.to_csv(index=False, float_format='%.1f').splitlines()[3] == '"Lee, Jr",45.0,79.2,True'
assert pd.Series([1, 2], name='v').to_csv() == ',v\n0,1\n1,2\n'

# === round trip through the virtual filesystem ===
path = '/virtual/people.csv'
assert df.to_csv(path, index=False) is None
with open(path) as f:
    assert f.read() == text
back = pd.read_csv(path)
assert back.columns.tolist() == ['name', 'age', 'score', 'active']
assert [str(t) for t in back.dtypes] == ['object', 'float64', 'float64', 'bool']
assert back['name'].tolist() == ['Ann', 'Bob', 'Lee, Jr']
assert back.equals(df)
df.to_csv('people2.csv', index=False)
assert pd.read_csv('/virtual/people2.csv').shape == (3, 4)
try:
    pd.read_csv('/virtual/missing.csv')
    assert False
except FileNotFoundError:
    pass
try:
    pd.read_csv('https://example.com/data.csv')
    assert False
except PermissionError:
    pass

# === read_csv's options, over StringIO ===
raw = 'a,b,c\n1,2,x\n3,4,y\n'
assert pd.read_csv(io.StringIO(raw))['a'].tolist() == [1, 3]
assert str(pd.read_csv(io.StringIO(raw))['a'].dtype) == 'int64'
assert pd.read_csv(io.StringIO('a;b\n1;2\n'), sep=';').columns.tolist() == ['a', 'b']
assert pd.read_csv(io.StringIO('1,2\n3,4\n'), header=None).columns.tolist() == [0, 1]
assert pd.read_csv(io.StringIO('1,2\n3,4\n'), names=['p', 'q'])['q'].tolist() == [2, 4]
assert pd.read_csv(io.StringIO(raw), usecols=['a', 'c']).columns.tolist() == ['a', 'c']
assert pd.read_csv(io.StringIO(raw), index_col='c').index.tolist() == ['x', 'y']
assert pd.read_csv(io.StringIO(raw), index_col=0).index.name == 'a'
assert pd.read_csv(io.StringIO(raw), nrows=1).shape == (1, 3)
assert pd.read_csv(io.StringIO('junk\na,b\n1,2\n'), skiprows=1).columns.tolist() == ['a', 'b']
assert pd.read_csv(io.StringIO(raw), dtype={'a': float})['a'].tolist() == [1.0, 3.0]
assert pd.read_csv(io.StringIO(raw), dtype={'a': str})['a'].tolist() == ['1', '3']
assert pd.read_csv(io.StringIO('a,a,b\n1,2,3\n')).columns.tolist() == ['a', 'a.1', 'b']
assert pd.read_csv(io.StringIO(',a\n0,1\n')).columns.tolist() == ['Unnamed: 0', 'a']
assert pd.read_csv(io.StringIO('a\tb\n1\t2\n'), sep='\t')['b'].tolist() == [2]
assert pd.read_csv(io.StringIO('a  b\n1   2\n'), sep=r'\s+')['b'].tolist() == [2]
assert pd.read_csv(io.StringIO('x,y\n1,2\n\n3,4\n'))['x'].tolist() == [1, 3]
assert pd.read_csv(io.StringIO('x\n"a\nb"\n'))['x'].tolist() == ['a\nb']
assert pd.read_csv(io.StringIO('x\n"say ""hi"""\n'))['x'].tolist() == ['say "hi"']
assert pd.read_csv(io.StringIO('x\n1,000\n'), sep=';', thousands=',')['x'].tolist() == [1000]

# missing-value spellings and booleans
na = pd.read_csv(io.StringIO('v,w,f\n1,NA,True\n,n/a,FALSE\n3,x,true\n'))
assert str(na['v'].dtype) == 'float64' and na['v'].isna().tolist() == [False, True, False]
assert na['w'].isna().tolist() == [True, True, False]
assert na['f'].tolist() == [True, False, True] and str(na['f'].dtype) == 'bool'
custom = pd.read_csv(io.StringIO('v\n-\n1\n'), na_values=['-'])
assert custom['v'].isna().tolist() == [True, False]
assert pd.read_csv(io.StringIO('v\nNA\n'), keep_default_na=False)['v'].tolist() == ['NA']

# dates
dates = pd.read_csv(io.StringIO('d,v\n2024-01-02,1\n2024-02-03,2\n'), parse_dates=['d'])
assert str(dates['d'].dtype) == 'datetime64[ns]'
assert dates['d'].dt.month.tolist() == [1, 2]
assert dates.to_csv(index=False) == 'd,v\n2024-01-02,1\n2024-02-03,2\n'

# a frame written with its index reads back with index_col=0
written = pd.DataFrame({'v': [1, 2]}, index=['a', 'b']).to_csv()
assert written == ',v\na,1\nb,2\n'
assert pd.read_csv(io.StringIO(written), index_col=0).index.tolist() == ['a', 'b']

# to_csv into a file-like object
buffer = io.StringIO()
pd.DataFrame({'q': [1]}).to_csv(buffer, index=False)
assert buffer.getvalue() == 'q\n1\n'

# === JSON ===
small = pd.DataFrame({'a': [1, 2], 'b': ['x', 'y']})
assert small.to_json() == '{"a":{"0":1,"1":2},"b":{"0":"x","1":"y"}}'
assert small.to_json(orient='records') == '[{"a":1,"b":"x"},{"a":2,"b":"y"}]'
assert small.to_json(orient='split') == '{"columns":["a","b"],"index":[0,1],"data":[[1,"x"],[2,"y"]]}'
assert small.to_json(orient='index') == '{"0":{"a":1,"b":"x"},"1":{"a":2,"b":"y"}}'
assert small.to_json(orient='values') == '[[1,"x"],[2,"y"]]'
assert small.to_json(orient='records', lines=True) == '{"a":1,"b":"x"}\n{"a":2,"b":"y"}\n'
assert pd.DataFrame({'a': [1.5, None]}).to_json(orient='records') == '[{"a":1.5},{"a":null}]'
assert pd.Series([1, 2], index=['a', 'b']).to_json() == '{"a":1,"b":2}'
assert pd.DataFrame({'u': ['a/b']}).to_json(orient='records') == '[{"u":"a\\/b"}]'
assert json.loads(small.to_json(orient='records')) == [{'a': 1, 'b': 'x'}, {'a': 2, 'b': 'y'}]
assert small.to_json('/virtual/small.json') is None

read = pd.read_json('/virtual/small.json')
assert read.columns.tolist() == ['a', 'b'] and read.index.tolist() == [0, 1]
assert read['a'].tolist() == [1, 2]
records = pd.read_json(io.StringIO('[{"a": 1, "b": "x"}, {"a": 2, "b": "y"}]'))
assert records.to_dict('records') == [{'a': 1, 'b': 'x'}, {'a': 2, 'b': 'y'}]
lines = pd.read_json(io.StringIO('{"a": 1}\n{"a": 2}\n'), lines=True)
assert lines['a'].tolist() == [1, 2]
split = pd.read_json(io.StringIO(small.to_json(orient='split')), orient='split')
assert split.equals(small)

# === to_dict, to_markdown, to_string ===
assert small.to_dict() == {'a': {0: 1, 1: 2}, 'b': {0: 'x', 1: 'y'}}
assert small.to_dict('list') == {'a': [1, 2], 'b': ['x', 'y']}
assert small.to_dict('records') == [{'a': 1, 'b': 'x'}, {'a': 2, 'b': 'y'}]
assert small.to_dict('index') == {0: {'a': 1, 'b': 'x'}, 1: {'a': 2, 'b': 'y'}}
assert small.to_dict('split') == {'index': [0, 1], 'columns': ['a', 'b'], 'data': [[1, 'x'], [2, 'y']]}
assert small.to_string() == '   a  b\n0  1  x\n1  2  y'
assert small.to_string(index=False) == ' a b\n 1 x\n 2 y'
assert small.to_markdown() == '|    |   a | b   |\n|---:|----:|:----|\n|  0 |   1 | x   |\n|  1 |   2 | y   |'
