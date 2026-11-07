# Extension: `pandas` — the `.str` and `.dt` accessors, to_datetime, Timestamp and
# date_range. Upstream Monty has no pandas. Expected values are pandas 2.2's, except that a
# Timestamp difference is a `datetime.timedelta` here rather than a `pd.Timedelta` (same
# fields, different repr).
import datetime
import math
import pandas as pd

s = pd.Series([' Hello ', 'World', None, 'foo bar'])

# === .str: missing values stay missing ===
assert s.str.strip().tolist()[:2] == ['Hello', 'World']
assert pd.isna(s.str.strip().tolist()[2])
assert s.str.lower().tolist()[1] == 'world' and s.str.upper().tolist()[3] == 'FOO BAR'
assert s.str.len().tolist()[:2] == [7.0, 5.0] and math.isnan(s.str.len().tolist()[2])
assert pd.Series(['ab', 'c']).str.len().tolist() == [2, 1]
assert s.str.contains('o').tolist()[:2] == [True, True]
assert pd.isna(s.str.contains('o').tolist()[2])
assert s.str.contains('o', na=False).tolist() == [True, True, False, True]
assert pd.Series(['Apple', 'apricot']).str.contains('^ap', case=False).tolist() == [True, True]
assert pd.Series(['a.b', 'ab']).str.contains('.', regex=False).tolist() == [True, False]
assert pd.Series(['a1', 'b']).str.contains(r'\d').tolist() == [True, False]
assert pd.Series(['abc', 'xbc']).str.startswith('a').tolist() == [True, False]
assert pd.Series(['abc', 'xbd']).str.endswith('c').tolist() == [True, False]
assert pd.Series(['a-b', 'c']).str.replace('-', '+').tolist() == ['a+b', 'c']
assert pd.Series(['a1b22']).str.replace(r'\d+', '#', regex=True).tolist() == ['a#b#']
assert pd.Series(['a b', 'c']).str.split().tolist() == [['a', 'b'], ['c']]
assert pd.Series(['a,b,c']).str.split(',', n=1).tolist() == [['a', 'b,c']]
parts = pd.Series(['a b', 'c d']).str.split(' ', expand=True)
assert parts.columns.tolist() == [0, 1] and parts[1].tolist() == ['b', 'd']
assert pd.Series(['hello']).str.slice(1, 3).tolist() == ['el']
assert pd.Series(['hello']).str[0].tolist() == ['h']
assert pd.Series(['hello']).str[-2:].tolist() == ['lo']
assert pd.Series(['hello', 'x']).str.get(1).tolist()[0] == 'e'
assert pd.Series(['hello world']).str.title().tolist() == ['Hello World']
assert pd.Series(['hello']).str.capitalize().tolist() == ['Hello']
assert pd.Series(['7']).str.zfill(3).tolist() == ['007']
assert pd.Series(['ab']).str.pad(4, side='left', fillchar='*').tolist() == ['**ab']
assert pd.Series(['ab']).str.pad(4, side='right').tolist() == ['ab  ']
assert pd.Series(['xxa', 'ayy']).str.lstrip('x').tolist() == ['a', 'ayy']
assert pd.Series(['a', 'b']).str.cat(sep='-') == 'a-b'
assert pd.Series(['a', 'b']).str.cat(pd.Series(['1', '2']), sep=':').tolist() == ['a:1', 'b:2']
assert pd.Series(['12', 'ab', '3x']).str.isdigit().tolist() == [True, False, False]
assert pd.Series(['aXa']).str.count('a').tolist() == [2]
assert pd.Series(['abc']).str.find('c').tolist() == [2]
assert pd.Series(['ab']).str.repeat(2).tolist() == ['abab']
ex = pd.Series(['a1', 'b22', 'c']).str.extract(r'([a-z])(\d+)')
assert ex[0].tolist()[:2] == ['a', 'b'] and ex[1].tolist()[:2] == ['1', '22']
assert pd.isna(ex[1].tolist()[2])
assert pd.Series(['a1b2']).str.findall(r'\d').tolist() == [['1', '2']]
df = pd.DataFrame({'Name ': [1], ' Age': [2]})
df.columns = df.columns.str.strip().str.lower()
assert df.columns.tolist() == ['name', 'age']

# === to_datetime ===
d = pd.to_datetime(pd.Series(['2024-01-15', '2024-02-29', '2024-12-31']))
assert str(d.dtype) == 'datetime64[ns]'
assert repr(d) == '0   2024-01-15\n1   2024-02-29\n2   2024-12-31\ndtype: datetime64[ns]'
assert d.dt.year.tolist() == [2024, 2024, 2024]
assert d.dt.month.tolist() == [1, 2, 12]
assert d.dt.day.tolist() == [15, 29, 31]
assert d.dt.dayofweek.tolist() == [0, 3, 1]
assert d.dt.weekday.tolist() == [0, 3, 1]
assert d.dt.day_name().tolist() == ['Monday', 'Thursday', 'Tuesday']
assert d.dt.month_name().tolist()[0] == 'January'
assert d.dt.quarter.tolist() == [1, 1, 4]
assert d.dt.dayofyear.tolist() == [15, 60, 366]
assert d.dt.is_month_end.tolist() == [False, True, True]
assert d.dt.strftime('%d/%m/%Y').tolist() == ['15/01/2024', '29/02/2024', '31/12/2024']
assert d.dt.date.tolist() == [datetime.date(2024, 1, 15), datetime.date(2024, 2, 29), datetime.date(2024, 12, 31)]
assert d.min() == pd.Timestamp('2024-01-15') and d.max() == pd.Timestamp('2024-12-31')
assert (d > '2024-02-01').tolist() == [False, True, True]
assert d.diff().dt.days.tolist()[1:] == [45.0, 306.0]

t = pd.to_datetime(pd.Series(['2024-03-10 14:30:15']))
assert t.dt.hour.tolist() == [14] and t.dt.minute.tolist() == [30] and t.dt.second.tolist() == [15]
assert repr(t) == '0   2024-03-10 14:30:15\ndtype: datetime64[ns]'
assert t.dt.normalize().tolist() == [pd.Timestamp('2024-03-10')]
assert t.dt.floor('h').tolist() == [pd.Timestamp('2024-03-10 14:00:00')]

idx = pd.to_datetime(['2024-01-01', '2024-01-02'])
assert repr(idx) == "DatetimeIndex(['2024-01-01', '2024-01-02'], dtype='datetime64[ns]', freq=None)"
assert pd.to_datetime('2024-05-06') == pd.Timestamp(2024, 5, 6)
assert pd.to_datetime('05/06/2024') == pd.Timestamp('2024-05-06')
assert pd.to_datetime('06/05/2024', dayfirst=True) == pd.Timestamp('2024-05-06')
assert pd.to_datetime('2024-05-06T07:08:09') == pd.Timestamp(2024, 5, 6, 7, 8, 9)
assert pd.to_datetime('06.05.2024', format='%d.%m.%Y') == pd.Timestamp('2024-05-06')
assert pd.to_datetime(pd.Series(['2024-01-01', 'nope']), errors='coerce').isna().tolist() == [False, True]
try:
    pd.to_datetime('nope')
    assert False
except ValueError:
    pass
assert pd.to_datetime(0, unit='s') == pd.Timestamp('1970-01-01')
assert pd.to_datetime(86400, unit='s') == pd.Timestamp('1970-01-02')
assert pd.isna(pd.to_datetime(None)) or pd.to_datetime(pd.Series([None])).isna().tolist() == [True]
parts = pd.DataFrame({'year': [2024], 'month': [2], 'day': [3]})
assert pd.to_datetime(parts).tolist() == [pd.Timestamp('2024-02-03')]

# === Timestamp ===
ts = pd.Timestamp('2024-03-01 12:00')
assert repr(ts) == "Timestamp('2024-03-01 12:00:00')"
assert str(ts) == '2024-03-01 12:00:00'
assert ts.year == 2024 and ts.month == 3 and ts.day == 1 and ts.hour == 12
assert ts.dayofweek == 4 and ts.day_name() == 'Friday' and ts.month_name() == 'March'
assert ts.strftime('%Y/%m/%d') == '2024/03/01'
assert ts.date() == datetime.date(2024, 3, 1)
assert ts.isoformat() == '2024-03-01T12:00:00'
assert ts.normalize() == pd.Timestamp('2024-03-01')
assert pd.Timestamp('2024-03-01') - pd.Timestamp('2024-02-01') == datetime.timedelta(days=29)
assert pd.Timestamp('2024-03-01') + pd.Timedelta(days=2) == pd.Timestamp('2024-03-03')
assert pd.Timestamp('2024-03-01') + datetime.timedelta(hours=1) == pd.Timestamp('2024-03-01 01:00')
assert pd.Timestamp('2024-03-01') > pd.Timestamp('2024-02-01')
assert pd.Timestamp('2024-03-01') == datetime.datetime(2024, 3, 1)
assert pd.Timedelta('2 days') == datetime.timedelta(days=2)
assert pd.Timedelta(hours=3).total_seconds() == 10800.0
assert isinstance(ts, pd.Timestamp)
assert pd.Timestamp(2024, 1, 1).is_leap_year

# === date_range ===
days = pd.date_range('2024-01-01', periods=3)
assert repr(days) == "DatetimeIndex(['2024-01-01', '2024-01-02', '2024-01-03'], dtype='datetime64[ns]', freq='D')"
assert pd.date_range('2024-01-01', '2024-01-05').size == 5
assert pd.date_range(end='2024-01-05', periods=2).tolist() == [pd.Timestamp('2024-01-04'), pd.Timestamp('2024-01-05')]
assert [str(x) for x in pd.date_range('2024-01-01', periods=3, freq='h')] == ['2024-01-01 00:00:00', '2024-01-01 01:00:00', '2024-01-01 02:00:00']
assert [x.day for x in pd.date_range('2024-01-31', periods=3, freq='ME')] == [31, 29, 31]
assert [x.month for x in pd.date_range('2024-01-15', periods=3, freq='MS')] == [2, 3, 4]
assert [x.day for x in pd.date_range('2024-01-01', periods=3, freq='W')] == [7, 14, 21]
assert [x.day for x in pd.date_range('2024-01-05', periods=3, freq='B')] == [5, 8, 9]
assert [x.day for x in pd.date_range('2024-01-01', periods=3, freq='2D')] == [1, 3, 5]
series = pd.Series(range(3), index=pd.date_range('2024-01-01', periods=3))
assert repr(series) == '2024-01-01    0\n2024-01-02    1\n2024-01-03    2\nFreq: D, dtype: int64'
assert series.loc['2024-01-02'] == 1
assert series['2024-01-02':'2024-01-03'].tolist() == [1, 2]

# === a datetime column in a frame ===
frame = pd.DataFrame({'when': pd.to_datetime(['2024-01-01', '2024-01-02']), 'v': [1, 2]})
assert repr(frame) == '        when  v\n0 2024-01-01  1\n1 2024-01-02  2'
assert frame[frame['when'] > '2024-01-01']['v'].tolist() == [2]
assert frame['when'].dt.day.sum() == 3
