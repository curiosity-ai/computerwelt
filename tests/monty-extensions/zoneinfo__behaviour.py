# Extension: `zoneinfo`, and the tzinfo protocol on `datetime` it needs — not in upstream Monty.
# IANA zones from the host's time-zone database, read with CPython's fold=0 rule for an
# ambiguous or skipped local time; `datetime` gains `utcoffset`, `tzname`, `dst` and
# `astimezone`. Expected values produced by CPython 3.13.
import datetime
from datetime import timedelta, timezone
dt = datetime.datetime
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError

berlin = ZoneInfo('Europe/Berlin')
new_york = ZoneInfo('America/New_York')

# === the zone objects ===
assert repr(berlin) == "zoneinfo.ZoneInfo(key='Europe/Berlin')", 'berlin'
assert (str(berlin), berlin.key) == ('Europe/Berlin', 'Europe/Berlin'), 'str(berlin), berlin.key'
assert (ZoneInfo('Europe/Berlin') is berlin) == True, "ZoneInfo('Europe/Berlin') is berlin"
assert (isinstance(berlin, ZoneInfo)) == True, 'isinstance(berlin, ZoneInfo)'
try:
    ZoneInfo('Mars/Olympus_Mons')
    assert False, "ZoneInfo('Mars/Olympus_Mons')"
except ZoneInfoNotFoundError:
    pass
try:
    ZoneInfo('Nowhere/At_All')
    assert False, "ZoneInfo('Nowhere/At_All')"
except KeyError:
    pass
try:
    ZoneInfo('../../etc/passwd')
    assert False, "ZoneInfo('../../etc/passwd')"
except ValueError:
    pass
try:
    ZoneInfo('/etc/localtime')
    assert False, "ZoneInfo('/etc/localtime')"
except ValueError:
    pass

# === offsets, names and DST across the year ===
winter = dt(2024, 1, 15, 12, 0, tzinfo=berlin)
summer = dt(2024, 7, 15, 12, 0, tzinfo=berlin)
assert (winter.utcoffset(), summer.utcoffset()) == (datetime.timedelta(seconds=3600), datetime.timedelta(seconds=7200)), 'winter.utcoffset(), summer.utcoffset()'
assert (winter.tzname(), summer.tzname()) == ('CET', 'CEST'), 'winter.tzname(), summer.tzname()'
assert (winter.dst(), summer.dst()) == (datetime.timedelta(0), datetime.timedelta(seconds=3600)), 'winter.dst(), summer.dst()'
assert repr(winter) == "datetime.datetime(2024, 1, 15, 12, 0, tzinfo=zoneinfo.ZoneInfo(key='Europe/Berlin'))", 'winter'
assert (winter.isoformat(), summer.isoformat()) == ('2024-01-15T12:00:00+01:00', '2024-07-15T12:00:00+02:00'), 'winter.isoformat(), summer.isoformat()'
assert (str(summer)) == '2024-07-15 12:00:00+02:00', 'str(summer)'
assert (berlin.utcoffset(dt(2024, 7, 1)), berlin.tzname(dt(2024, 12, 1)), berlin.utcoffset(None)) == (datetime.timedelta(seconds=7200), 'CET', None), 'berlin.utcoffset(dt(2024, 7, 1)), berlin.tzname(dt(2024, 12, 1)), berlin.utcoffset(None)'
assert (dt(2024, 3, 10, 12, tzinfo=new_york).utcoffset(), dt(2024, 3, 9, 12, tzinfo=new_york).utcoffset()) == (datetime.timedelta(days=-1, seconds=72000), datetime.timedelta(days=-1, seconds=68400)), 'dt(2024, 3, 10, 12, tzinfo=new_york).utcoffset(), dt(2024, 3, 9, 12, tzinfo=new_york).utcoffset()'
assert (dt(2024, 11, 3, 12, tzinfo=new_york).tzname()) == 'EST', 'dt(2024, 11, 3, 12, tzinfo=new_york).tzname()'
assert (ZoneInfo('Asia/Kolkata').utcoffset(dt(2024, 1, 1))) == datetime.timedelta(seconds=19800), "ZoneInfo('Asia/Kolkata').utcoffset(dt(2024, 1, 1))"
assert (ZoneInfo('UTC').utcoffset(dt(2024, 1, 1)), ZoneInfo('UTC').tzname(dt(2024, 1, 1))) == (datetime.timedelta(0), 'UTC'), "ZoneInfo('UTC').utcoffset(dt(2024, 1, 1)), ZoneInfo('UTC').tzname(dt(2024, 1, 1))"

# === the hours a DST change skips and repeats (fold=0) ===
assert (dt(2024, 3, 31, 2, 30, tzinfo=berlin).utcoffset()) == datetime.timedelta(seconds=3600), 'dt(2024, 3, 31, 2, 30, tzinfo=berlin).utcoffset()'
assert (dt(2024, 10, 27, 2, 30, tzinfo=berlin).utcoffset()) == datetime.timedelta(seconds=7200), 'dt(2024, 10, 27, 2, 30, tzinfo=berlin).utcoffset()'
assert (dt(2024, 10, 27, 1, 59, tzinfo=berlin).utcoffset(), dt(2024, 10, 27, 3, 0, tzinfo=berlin).utcoffset()) == (datetime.timedelta(seconds=7200), datetime.timedelta(seconds=3600)), 'dt(2024, 10, 27, 1, 59, tzinfo=berlin).utcoffset(), dt(2024, 10, 27, 3, 0, tzinfo=berlin).utcoffset()'

# === converting between zones ===
utc_noon = dt(2024, 7, 4, 12, 0, tzinfo=timezone.utc)
assert repr(utc_noon.astimezone(new_york)) == "datetime.datetime(2024, 7, 4, 8, 0, tzinfo=zoneinfo.ZoneInfo(key='America/New_York'))", 'utc_noon.astimezone(new_york)'
assert (utc_noon.astimezone(berlin).isoformat()) == '2024-07-04T14:00:00+02:00', 'utc_noon.astimezone(berlin).isoformat()'
assert (summer.astimezone(timezone.utc).isoformat()) == '2024-07-15T10:00:00+00:00', 'summer.astimezone(timezone.utc).isoformat()'
assert (summer.astimezone(new_york).hour) == 6, 'summer.astimezone(new_york).hour'
assert (summer == summer.astimezone(new_york)) == True, 'summer == summer.astimezone(new_york)'
assert (winter < summer) == True, 'winter < summer'
assert ((summer - winter)) == datetime.timedelta(days=182), '(summer - winter)'
assert ((summer.astimezone(new_york) - winter)) == datetime.timedelta(days=181, seconds=82800), '(summer.astimezone(new_york) - winter)'
assert (berlin.fromutc(dt(2024, 7, 15, 10, 0, tzinfo=berlin)).isoformat()) == '2024-07-15T12:00:00+02:00', 'berlin.fromutc(dt(2024, 7, 15, 10, 0, tzinfo=berlin)).isoformat()'
assert (dt.fromtimestamp(1720000000, tz=berlin).isoformat()) == '2024-07-03T11:46:40+02:00', 'dt.fromtimestamp(1720000000, tz=berlin).isoformat()'
assert (dt.fromtimestamp(1700000000, tz=new_york).isoformat()) == '2023-11-14T17:13:20-05:00', 'dt.fromtimestamp(1700000000, tz=new_york).isoformat()'
assert (dt(2024, 1, 1, tzinfo=berlin).timestamp()) == 1704063600.0, 'dt(2024, 1, 1, tzinfo=berlin).timestamp()'

# === wall-clock arithmetic keeps the zone ===
before = dt(2024, 3, 30, 12, 0, tzinfo=berlin)
after = before + timedelta(days=1)
assert (after.isoformat(), after.tzinfo is berlin) == ('2024-03-31T12:00:00+02:00', True), 'after.isoformat(), after.tzinfo is berlin'
assert (after - before) == datetime.timedelta(days=1), 'after - before'
assert (after.astimezone(timezone.utc) - before.astimezone(timezone.utc)) == datetime.timedelta(seconds=82800), 'after.astimezone(timezone.utc) - before.astimezone(timezone.utc)'
import zoneinfo
assert ('Europe/Berlin' in zoneinfo.available_timezones(), 'UTC' in zoneinfo.available_timezones()) == (True, True), "'Europe/Berlin' in zoneinfo.available_timezones(), 'UTC' in zoneinfo.available_timezones()"
