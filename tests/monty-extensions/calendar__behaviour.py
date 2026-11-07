# Extension: `calendar` — not in upstream Monty.
# The arithmetic and the plain-text layout of CPython 3.13. The constants are plain ints
# (CPython's are IntEnum members that compare equal to them) and the names are English.
# Expected values produced by CPython 3.13.
import calendar

# === leap years and weekdays ===
assert ([calendar.isleap(y) for y in (1900, 2000, 2023, 2024, 2100)]) == [False, True, False, True, False], '[calendar.isleap(y) for y in (1900, 2000, 2023, 2024, 2100)]'
assert (calendar.leapdays(1900, 2025)) == 31, 'calendar.leapdays(1900, 2025)'
assert (calendar.leapdays(2024, 2024)) == 0, 'calendar.leapdays(2024, 2024)'
assert (calendar.weekday(2024, 2, 29)) == calendar.THURSDAY, 'calendar.weekday(2024, 2, 29)'
assert (calendar.weekday(1970, 1, 1)) == calendar.THURSDAY, 'calendar.weekday(1970, 1, 1)'
assert (calendar.weekday(10000, 1, 1)) == calendar.SATURDAY, 'calendar.weekday(10000, 1, 1)'
assert (calendar.monthrange(2024, 2)) == (calendar.THURSDAY, 29), 'calendar.monthrange(2024, 2)'
assert (calendar.monthrange(2023, 2)) == (calendar.WEDNESDAY, 28), 'calendar.monthrange(2023, 2)'
assert (calendar.monthrange(2025, 12)) == (calendar.MONDAY, 31), 'calendar.monthrange(2025, 12)'
try:
    calendar.monthrange(2024, 13)
    assert False, 'calendar.monthrange(2024, 13)'
except calendar.IllegalMonthError:
    pass
try:
    calendar.monthrange(2024, 0)
    assert False, 'calendar.monthrange(2024, 0)'
except ValueError:
    pass
try:
    calendar.monthrange(2024, 0)
    assert False, 'calendar.monthrange(2024, 0)'
except IndexError:
    pass
try:
    calendar.monthrange(2024, 13)
except calendar.IllegalMonthError as e:
    msg = str(e)
assert (msg) == 'bad month number 13; must be 1-12', 'msg'
assert ((calendar.MONDAY, calendar.SUNDAY, calendar.JANUARY, calendar.DECEMBER)) == (calendar.MONDAY, calendar.SUNDAY, calendar.JANUARY, calendar.DECEMBER), '(calendar.MONDAY, calendar.SUNDAY, calendar.JANUARY, calendar.DECEMBER)'
assert (calendar.SUNDAY == 6) == True, 'calendar.SUNDAY == 6'

# === names ===
assert (list(calendar.day_name)) == ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'], 'list(calendar.day_name)'
assert (list(calendar.day_abbr)) == ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'], 'list(calendar.day_abbr)'
assert (calendar.month_name[1:4]) == ['January', 'February', 'March'], 'calendar.month_name[1:4]'
assert (calendar.month_abbr[12]) == 'Dec', 'calendar.month_abbr[12]'
assert (calendar.month_name[0]) == '', 'calendar.month_name[0]'
assert (len(calendar.month_name)) == 13, 'len(calendar.month_name)'

# === month matrices ===
assert (calendar.monthcalendar(2024, 2)) == [[0, 0, 0, 1, 2, 3, 4], [5, 6, 7, 8, 9, 10, 11], [12, 13, 14, 15, 16, 17, 18], [19, 20, 21, 22, 23, 24, 25], [26, 27, 28, 29, 0, 0, 0]], 'calendar.monthcalendar(2024, 2)'
assert (calendar.monthcalendar(2021, 2)) == [[1, 2, 3, 4, 5, 6, 7], [8, 9, 10, 11, 12, 13, 14], [15, 16, 17, 18, 19, 20, 21], [22, 23, 24, 25, 26, 27, 28]], 'calendar.monthcalendar(2021, 2)'
cal = calendar.Calendar(firstweekday=6)
assert (cal.monthdayscalendar(2024, 9)) == [[1, 2, 3, 4, 5, 6, 7], [8, 9, 10, 11, 12, 13, 14], [15, 16, 17, 18, 19, 20, 21], [22, 23, 24, 25, 26, 27, 28], [29, 30, 0, 0, 0, 0, 0]], 'cal.monthdayscalendar(2024, 9)'
assert (cal.getfirstweekday()) == 6, 'cal.getfirstweekday()'
assert (list(cal.iterweekdays())) == [6, 0, 1, 2, 3, 4, 5], 'list(cal.iterweekdays())'
assert (list(cal.itermonthdays2(2024, 2))[:9]) == [(0, 6), (0, 0), (0, 1), (0, 2), (1, 3), (2, 4), (3, 5), (4, 6), (5, 0)], 'list(cal.itermonthdays2(2024, 2))[:9]'
assert (list(cal.itermonthdays3(2024, 3))[:7]) == [(2024, 2, 25), (2024, 2, 26), (2024, 2, 27), (2024, 2, 28), (2024, 2, 29), (2024, 3, 1), (2024, 3, 2)], 'list(cal.itermonthdays3(2024, 3))[:7]'
assert (list(cal.itermonthdays4(2024, 3))[-3:]) == [(2024, 4, 4, 3), (2024, 4, 5, 4), (2024, 4, 6, 5)], 'list(cal.itermonthdays4(2024, 3))[-3:]'
assert ([str(d) for d in cal.itermonthdates(2024, 12)][:3]) == ['2024-12-01', '2024-12-02', '2024-12-03'], '[str(d) for d in cal.itermonthdates(2024, 12)][:3]'
assert (len(calendar.Calendar().yeardayscalendar(2024))) == 4, 'len(calendar.Calendar().yeardayscalendar(2024))'
assert (calendar.Calendar().yeardays2calendar(2024, width=4)[0][0][0]) == [(1, 0), (2, 1), (3, 2), (4, 3), (5, 4), (6, 5), (7, 6)], 'calendar.Calendar().yeardays2calendar(2024, width=4)[0][0][0]'

# === text layout ===
assert (calendar.month(2024, 2)) == '   February 2024\nMo Tu We Th Fr Sa Su\n          1  2  3  4\n 5  6  7  8  9 10 11\n12 13 14 15 16 17 18\n19 20 21 22 23 24 25\n26 27 28 29\n', 'calendar.month(2024, 2)'
assert (calendar.month(2024, 9, w=3, l=2)) == '       September 2024\n\nMon Tue Wed Thu Fri Sat Sun\n\n                          1\n\n  2   3   4   5   6   7   8\n\n  9  10  11  12  13  14  15\n\n 16  17  18  19  20  21  22\n\n 23  24  25  26  27  28  29\n\n 30\n\n', 'calendar.month(2024, 9, w=3, l=2)'
assert (calendar.TextCalendar(firstweekday=6).formatmonth(2025, 3)) == '     March 2025\nSu Mo Tu We Th Fr Sa\n                   1\n 2  3  4  5  6  7  8\n 9 10 11 12 13 14 15\n16 17 18 19 20 21 22\n23 24 25 26 27 28 29\n30 31\n', 'calendar.TextCalendar(firstweekday=6).formatmonth(2025, 3)'
assert (calendar.weekheader(3)) == 'Mon Tue Wed Thu Fri Sat Sun', 'calendar.weekheader(3)'
assert (calendar.weekheader(9)) == '  Monday   Tuesday  Wednesday  Thursday   Friday   Saturday   Sunday ', 'calendar.weekheader(9)'
assert (calendar.calendar(2024)) == '                                  2024\n\n      January                   February                   March\nMo Tu We Th Fr Sa Su      Mo Tu We Th Fr Sa Su      Mo Tu We Th Fr Sa Su\n 1  2  3  4  5  6  7                1  2  3  4                   1  2  3\n 8  9 10 11 12 13 14       5  6  7  8  9 10 11       4  5  6  7  8  9 10\n15 16 17 18 19 20 21      12 13 14 15 16 17 18      11 12 13 14 15 16 17\n22 23 24 25 26 27 28      19 20 21 22 23 24 25      18 19 20 21 22 23 24\n29 30 31                  26 27 28 29               25 26 27 28 29 30 31\n\n       April                      May                       June\nMo Tu We Th Fr Sa Su      Mo Tu We Th Fr Sa Su      Mo Tu We Th Fr Sa Su\n 1  2  3  4  5  6  7             1  2  3  4  5                      1  2\n 8  9 10 11 12 13 14       6  7  8  9 10 11 12       3  4  5  6  7  8  9\n15 16 17 18 19 20 21      13 14 15 16 17 18 19      10 11 12 13 14 15 16\n22 23 24 25 26 27 28      20 21 22 23 24 25 26      17 18 19 20 21 22 23\n29 30                     27 28 29 30 31            24 25 26 27 28 29 30\n\n        July                     August                  September\nMo Tu We Th Fr Sa Su      Mo Tu We Th Fr Sa Su      Mo Tu We Th Fr Sa Su\n 1  2  3  4  5  6  7                1  2  3  4                         1\n 8  9 10 11 12 13 14       5  6  7  8  9 10 11       2  3  4  5  6  7  8\n15 16 17 18 19 20 21      12 13 14 15 16 17 18       9 10 11 12 13 14 15\n22 23 24 25 26 27 28      19 20 21 22 23 24 25      16 17 18 19 20 21 22\n29 30 31                  26 27 28 29 30 31         23 24 25 26 27 28 29\n                                                    30\n\n      October                   November                  December\nMo Tu We Th Fr Sa Su      Mo Tu We Th Fr Sa Su      Mo Tu We Th Fr Sa Su\n    1  2  3  4  5  6                   1  2  3                         1\n 7  8  9 10 11 12 13       4  5  6  7  8  9 10       2  3  4  5  6  7  8\n14 15 16 17 18 19 20      11 12 13 14 15 16 17       9 10 11 12 13 14 15\n21 22 23 24 25 26 27      18 19 20 21 22 23 24      16 17 18 19 20 21 22\n28 29 30 31               25 26 27 28 29 30         23 24 25 26 27 28 29\n                                                    30 31\n', 'calendar.calendar(2024)'
assert (calendar.TextCalendar().formatyear(2023, 2, 1, 4, 4)) == '                                            2023\n\n      January                 February                 March                   April\nMo Tu We Th Fr Sa Su    Mo Tu We Th Fr Sa Su    Mo Tu We Th Fr Sa Su    Mo Tu We Th Fr Sa Su\n                   1           1  2  3  4  5           1  2  3  4  5                    1  2\n 2  3  4  5  6  7  8     6  7  8  9 10 11 12     6  7  8  9 10 11 12     3  4  5  6  7  8  9\n 9 10 11 12 13 14 15    13 14 15 16 17 18 19    13 14 15 16 17 18 19    10 11 12 13 14 15 16\n16 17 18 19 20 21 22    20 21 22 23 24 25 26    20 21 22 23 24 25 26    17 18 19 20 21 22 23\n23 24 25 26 27 28 29    27 28                   27 28 29 30 31          24 25 26 27 28 29 30\n30 31\n\n        May                     June                    July                   August\nMo Tu We Th Fr Sa Su    Mo Tu We Th Fr Sa Su    Mo Tu We Th Fr Sa Su    Mo Tu We Th Fr Sa Su\n 1  2  3  4  5  6  7              1  2  3  4                    1  2        1  2  3  4  5  6\n 8  9 10 11 12 13 14     5  6  7  8  9 10 11     3  4  5  6  7  8  9     7  8  9 10 11 12 13\n15 16 17 18 19 20 21    12 13 14 15 16 17 18    10 11 12 13 14 15 16    14 15 16 17 18 19 20\n22 23 24 25 26 27 28    19 20 21 22 23 24 25    17 18 19 20 21 22 23    21 22 23 24 25 26 27\n29 30 31                26 27 28 29 30          24 25 26 27 28 29 30    28 29 30 31\n                                                31\n\n     September                October                 November                December\nMo Tu We Th Fr Sa Su    Mo Tu We Th Fr Sa Su    Mo Tu We Th Fr Sa Su    Mo Tu We Th Fr Sa Su\n             1  2  3                       1           1  2  3  4  5                 1  2  3\n 4  5  6  7  8  9 10     2  3  4  5  6  7  8     6  7  8  9 10 11 12     4  5  6  7  8  9 10\n11 12 13 14 15 16 17     9 10 11 12 13 14 15    13 14 15 16 17 18 19    11 12 13 14 15 16 17\n18 19 20 21 22 23 24    16 17 18 19 20 21 22    20 21 22 23 24 25 26    18 19 20 21 22 23 24\n25 26 27 28 29 30       23 24 25 26 27 28 29    27 28 29 30             25 26 27 28 29 30 31\n                        30 31\n', 'calendar.TextCalendar().formatyear(2023, 2, 1, 4, 4)'

# === timegm ===
assert (calendar.timegm((1970, 1, 1, 0, 0, 0))) == 0, 'calendar.timegm((1970, 1, 1, 0, 0, 0))'
assert (calendar.timegm((2024, 2, 29, 12, 30, 15, 3, 60, 0))) == 1709209815, 'calendar.timegm((2024, 2, 29, 12, 30, 15, 3, 60, 0))'
assert (calendar.timegm((1969, 12, 31, 23, 59, 59))) == -1, 'calendar.timegm((1969, 12, 31, 23, 59, 59))'

# === firstweekday ===
assert (calendar.firstweekday()) == 0, 'calendar.firstweekday()'
calendar.setfirstweekday(calendar.SUNDAY)
assert (calendar.firstweekday()) == 6, 'calendar.firstweekday()'
assert (calendar.monthcalendar(2024, 6)[0]) == [0, 0, 0, 0, 0, 0, 1], 'calendar.monthcalendar(2024, 6)[0]'
calendar.setfirstweekday(calendar.MONDAY)
try:
    calendar.setfirstweekday(7)
    assert False, 'calendar.setfirstweekday(7)'
except calendar.IllegalWeekdayError:
    pass

# === str.center, which the layout depends on: an odd pad in an odd width goes left ===
assert ([s.center(w) for s, w in [("ab", 5), ("a", 4), ("abc", 6), (" 5", 3), ("x", 2), ("", 3)]]) == ['  ab ', ' a  ', ' abc  ', '  5', 'x ', '   '], '[s.center(w) for s, w in [("ab", 5), ("a", 4), ("abc", 6), (" 5", 3), ("x", 2), ("", 3)]]'
assert ("ab".center(7, "*"), "abc".center(8, "-")) == ('***ab**', '--abc---'), '"ab".center(7, "*"), "abc".center(8, "-")'
