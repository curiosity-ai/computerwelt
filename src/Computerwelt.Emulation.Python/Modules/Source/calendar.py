"""calendar: CPython 3.13's calendar arithmetic and plain-text layout.

The month and weekday constants are plain integers (CPython's are IntEnum members, which
compare and index identically), the name tables are lists, and the names are English —
there is no locale in the sandbox. ``Calendar`` and ``TextCalendar`` are separate classes
sharing one set of methods, because classes here do not inherit; ``HTMLCalendar`` and the
locale calendars are not provided.
"""

import datetime
from _hostexc import IllegalMonthError, IllegalWeekdayError

__all__ = ["IllegalMonthError", "IllegalWeekdayError", "setfirstweekday",
           "firstweekday", "isleap", "leapdays", "weekday", "monthrange",
           "monthcalendar", "prmonth", "month", "prcal", "calendar",
           "timegm", "month_name", "month_abbr", "day_name", "day_abbr",
           "Calendar", "TextCalendar", "weekheader",
           "MONDAY", "TUESDAY", "WEDNESDAY", "THURSDAY", "FRIDAY",
           "SATURDAY", "SUNDAY", "JANUARY", "FEBRUARY", "MARCH", "APRIL", "MAY",
           "JUNE", "JULY", "AUGUST", "SEPTEMBER", "OCTOBER", "NOVEMBER", "DECEMBER"]

error = ValueError

JANUARY = 1
FEBRUARY = 2
MARCH = 3
APRIL = 4
MAY = 5
JUNE = 6
JULY = 7
AUGUST = 8
SEPTEMBER = 9
OCTOBER = 10
NOVEMBER = 11
DECEMBER = 12

MONDAY = 0
TUESDAY = 1
WEDNESDAY = 2
THURSDAY = 3
FRIDAY = 4
SATURDAY = 5
SUNDAY = 6

mdays = [0, 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31]

day_name = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday']
day_abbr = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun']
month_name = ['', 'January', 'February', 'March', 'April', 'May', 'June', 'July',
              'August', 'September', 'October', 'November', 'December']
month_abbr = ['', 'Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep',
              'Oct', 'Nov', 'Dec']


def isleap(year):
    """Return True for leap years, False for non-leap years."""
    return year % 4 == 0 and (year % 100 != 0 or year % 400 == 0)


def leapdays(y1, y2):
    """Return number of leap years in range [y1, y2). Assume y1 <= y2."""
    y1 -= 1
    y2 -= 1
    return (y2 // 4 - y1 // 4) - (y2 // 100 - y1 // 100) + (y2 // 400 - y1 // 400)


def weekday(year, month, day):
    """Return weekday (0-6 ~ Mon-Sun) for year, month (1-12), day (1-31)."""
    if not 1 <= year <= 9999:
        year = 2000 + year % 400
    return datetime.date(year, month, day).weekday()


def _validate_month(month):
    if not 1 <= month <= 12:
        raise IllegalMonthError("bad month number %r; must be 1-12" % (month,))


def monthrange(year, month):
    """Return weekday of the first day of the month and number of days in the month."""
    _validate_month(month)
    day1 = weekday(year, month, 1)
    ndays = mdays[month] + (month == FEBRUARY and isleap(year))
    return day1, ndays


def _monthlen(year, month):
    return mdays[month] + (month == FEBRUARY and isleap(year))


def _prevmonth(year, month):
    if month == 1:
        return year - 1, 12
    return year, month - 1


def _nextmonth(year, month):
    if month == 12:
        return year + 1, 1
    return year, month + 1


# ---- the shared methods: Calendar's, and TextCalendar's on top of them ----

def _init(self, firstweekday=0):
    self.firstweekday = firstweekday


def _getfirstweekday(self):
    return self.firstweekday % 7


def _setfirstweekday(self, firstweekday):
    self.firstweekday = firstweekday


def _iterweekdays(self):
    for i in range(self.firstweekday, self.firstweekday + 7):
        yield i % 7


def _itermonthdates(self, year, month):
    for y, m, d in self.itermonthdays3(year, month):
        yield datetime.date(y, m, d)


def _itermonthdays(self, year, month):
    first = self.firstweekday % 7
    day1, ndays = monthrange(year, month)
    days_before = (day1 - first) % 7
    for _ in range(days_before):
        yield 0
    yield from range(1, ndays + 1)
    days_after = (first - day1 - ndays) % 7
    for _ in range(days_after):
        yield 0


def _itermonthdays2(self, year, month):
    for i, d in enumerate(self.itermonthdays(year, month), self.firstweekday % 7):
        yield (d, i % 7)


def _itermonthdays3(self, year, month):
    first = self.firstweekday % 7
    day1, ndays = monthrange(year, month)
    days_before = (day1 - first) % 7
    days_after = (first - day1 - ndays) % 7
    y, m = _prevmonth(year, month)
    end = _monthlen(y, m) + 1
    for d in range(end - days_before, end):
        yield (y, m, d)
    for d in range(1, ndays + 1):
        yield (year, month, d)
    y, m = _nextmonth(year, month)
    for d in range(1, days_after + 1):
        yield (y, m, d)


def _itermonthdays4(self, year, month):
    for i, (y, m, d) in enumerate(self.itermonthdays3(year, month)):
        yield (y, m, d, (self.firstweekday % 7 + i) % 7)


def _rows(items, width):
    return [items[i:i + width] for i in range(0, len(items), width)]


def _monthdatescalendar(self, year, month):
    return _rows(list(self.itermonthdates(year, month)), 7)


def _monthdays2calendar(self, year, month):
    return _rows(list(self.itermonthdays2(year, month)), 7)


def _monthdayscalendar(self, year, month):
    return _rows(list(self.itermonthdays(year, month)), 7)


def _yeardatescalendar(self, year, width=3):
    return _rows([self.monthdatescalendar(year, m) for m in range(1, 13)], width)


def _yeardays2calendar(self, year, width=3):
    return _rows([self.monthdays2calendar(year, m) for m in range(1, 13)], width)


def _yeardayscalendar(self, year, width=3):
    return _rows([self.monthdayscalendar(year, m) for m in range(1, 13)], width)


def _prweek(self, theweek, width):
    print(self.formatweek(theweek, width), end='')


def _formatday(self, day, weekday, width):
    if day == 0:
        s = ''
    else:
        s = '%2i' % day
    return s.center(width)


def _formatweek(self, theweek, width):
    return ' '.join(self.formatday(d, wd, width) for (d, wd) in theweek)


def _formatweekday(self, day, width):
    if width >= 9:
        names = day_name
    else:
        names = day_abbr
    return names[day][:width].center(width)


def _formatweekheader(self, width):
    return ' '.join(self.formatweekday(i, width) for i in self.iterweekdays())


def _formatmonthname(self, theyear, themonth, width, withyear=True):
    _validate_month(themonth)
    s = month_name[themonth]
    if withyear:
        s = "%s %r" % (s, theyear)
    return s.center(width)


def _prmonth(self, theyear, themonth, w=0, l=0):
    print(self.formatmonth(theyear, themonth, w, l), end='')


def _formatmonth(self, theyear, themonth, w=0, l=0):
    w = max(2, w)
    l = max(1, l)
    s = self.formatmonthname(theyear, themonth, 7 * (w + 1) - 1)
    s = s.rstrip()
    s += '\n' * l
    s += self.formatweekheader(w).rstrip()
    s += '\n' * l
    for week in self.monthdays2calendar(theyear, themonth):
        s += self.formatweek(week, w).rstrip()
        s += '\n' * l
    return s


def _formatyear(self, theyear, w=2, l=1, c=6, m=3):
    w = max(2, w)
    l = max(1, l)
    c = max(2, c)
    colwidth = (w + 1) * 7 - 1
    v = []
    a = v.append
    a(repr(theyear).center(colwidth * m + c * (m - 1)).rstrip())
    a('\n' * l)
    header = self.formatweekheader(w)
    for (i, row) in enumerate(self.yeardays2calendar(theyear, m)):
        months = range(m * i + 1, min(m * (i + 1) + 1, 13))
        a('\n' * l)
        names = (self.formatmonthname(theyear, k, colwidth, False) for k in months)
        a(formatstring(names, colwidth, c).rstrip())
        a('\n' * l)
        headers = (header for k in months)
        a(formatstring(headers, colwidth, c).rstrip())
        a('\n' * l)
        height = max(len(cal) for cal in row)
        for j in range(height):
            weeks = []
            for cal in row:
                if j >= len(cal):
                    weeks.append('')
                else:
                    weeks.append(self.formatweek(cal[j], w))
            a(formatstring(weeks, colwidth, c).rstrip())
            a('\n' * l)
    return ''.join(v)


def _pryear(self, theyear, w=0, l=0, c=6, m=3):
    print(self.formatyear(theyear, w, l, c, m), end='')


class Calendar:
    """Provides the data for a calendar, without formatting it."""

    __init__ = _init
    getfirstweekday = _getfirstweekday
    setfirstweekday = _setfirstweekday
    iterweekdays = _iterweekdays
    itermonthdates = _itermonthdates
    itermonthdays = _itermonthdays
    itermonthdays2 = _itermonthdays2
    itermonthdays3 = _itermonthdays3
    itermonthdays4 = _itermonthdays4
    monthdatescalendar = _monthdatescalendar
    monthdays2calendar = _monthdays2calendar
    monthdayscalendar = _monthdayscalendar
    yeardatescalendar = _yeardatescalendar
    yeardays2calendar = _yeardays2calendar
    yeardayscalendar = _yeardayscalendar


class TextCalendar:
    """A Calendar that lays a month or a year out as plain text, as the Unix `cal` does."""

    __init__ = _init
    getfirstweekday = _getfirstweekday
    setfirstweekday = _setfirstweekday
    iterweekdays = _iterweekdays
    itermonthdates = _itermonthdates
    itermonthdays = _itermonthdays
    itermonthdays2 = _itermonthdays2
    itermonthdays3 = _itermonthdays3
    itermonthdays4 = _itermonthdays4
    monthdatescalendar = _monthdatescalendar
    monthdays2calendar = _monthdays2calendar
    monthdayscalendar = _monthdayscalendar
    yeardatescalendar = _yeardatescalendar
    yeardays2calendar = _yeardays2calendar
    yeardayscalendar = _yeardayscalendar
    prweek = _prweek
    formatday = _formatday
    formatweek = _formatweek
    formatweekday = _formatweekday
    formatweekheader = _formatweekheader
    formatmonthname = _formatmonthname
    prmonth = _prmonth
    formatmonth = _formatmonth
    formatyear = _formatyear
    pryear = _pryear


c = TextCalendar()


firstweekday = c.getfirstweekday


def setfirstweekday(firstweekday):
    if not MONDAY <= firstweekday <= SUNDAY:
        raise IllegalWeekdayError(
            "bad weekday number %r; must be 0 (Monday) to 6 (Sunday)" % (firstweekday,))
    c.firstweekday = firstweekday


monthcalendar = c.monthdayscalendar
prweek = c.prweek
week = c.formatweek
weekheader = c.formatweekheader
prmonth = c.prmonth
month = c.formatmonth
calendar = c.formatyear
prcal = c.pryear


_colwidth = 7 * 3 - 1
_spacing = 6


def format(cols, colwidth=_colwidth, spacing=_spacing):
    """Prints multi-column formatting for year calendars."""
    print(formatstring(cols, colwidth, spacing))


def formatstring(cols, colwidth=_colwidth, spacing=_spacing):
    """Returns a string formatted from n strings, centered within n columns."""
    spacing *= ' '
    return spacing.join(c.center(colwidth) for c in cols)


EPOCH = 1970


def _ordinal(year, month, day):
    y = year - 1
    days_before_year = y * 365 + y // 4 - y // 100 + y // 400
    days_before_month = sum(mdays[1:month]) + (month > 2 and isleap(year))
    return days_before_year + days_before_month + day


_EPOCH_ORD = _ordinal(EPOCH, 1, 1)


def timegm(tuple):
    """Unrelated but handy function to calculate Unix timestamp from GMT."""
    year, month, day, hour, minute, second = tuple[:6]
    days = _ordinal(year, month, 1) - _EPOCH_ORD + day - 1
    hours = days * 24 + hour
    minutes = hours * 60 + minute
    seconds = minutes * 60 + second
    return seconds
