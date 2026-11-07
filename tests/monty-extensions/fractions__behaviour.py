# Extension: `fractions` — not in upstream Monty.
# Exact rational arithmetic with CPython 3.13's construction rules, reprs, hashing and
# exact `__format__`. Expected values produced by CPython 3.13.
import math
from fractions import Fraction
from decimal import Decimal

# === construction ===
assert repr(Fraction(3, 4)) == 'Fraction(3, 4)', 'Fraction(3, 4)'
assert repr(Fraction(6, -8)) == 'Fraction(-3, 4)', 'Fraction(6, -8)'
assert repr(Fraction()) == 'Fraction(0, 1)', 'Fraction()'
assert repr(Fraction(5)) == 'Fraction(5, 1)', 'Fraction(5)'
assert repr(Fraction('3/7')) == 'Fraction(3, 7)', "Fraction('3/7')"
assert repr(Fraction(' -12 / 16 ')) == 'Fraction(-3, 4)', "Fraction(' -12 / 16 ')"
assert repr(Fraction('1.25')) == 'Fraction(5, 4)', "Fraction('1.25')"
assert repr(Fraction('-.5')) == 'Fraction(-1, 2)', "Fraction('-.5')"
assert repr(Fraction('2e3')) == 'Fraction(2000, 1)', "Fraction('2e3')"
assert repr(Fraction('1.5e-2')) == 'Fraction(3, 200)', "Fraction('1.5e-2')"
assert repr(Fraction('1_000/3')) == 'Fraction(1000, 3)', "Fraction('1_000/3')"
assert repr(Fraction(0.1)) == 'Fraction(3602879701896397, 36028797018963968)', 'Fraction(0.1)'
assert repr(Fraction(-2.5)) == 'Fraction(-5, 2)', 'Fraction(-2.5)'
assert repr(Fraction(Decimal('1.1'))) == 'Fraction(11, 10)', "Fraction(Decimal('1.1'))"
assert repr(Fraction(Fraction(1, 2), Fraction(1, 3))) == 'Fraction(3, 2)', 'Fraction(Fraction(1, 2), Fraction(1, 3))'
assert repr(Fraction(numerator=6, denominator=4)) == 'Fraction(3, 2)', 'Fraction(numerator=6, denominator=4)'
assert repr(Fraction.from_float(0.375)) == 'Fraction(3, 8)', 'Fraction.from_float(0.375)'
assert repr(Fraction.from_decimal(Decimal('-0.125'))) == 'Fraction(-1, 8)', "Fraction.from_decimal(Decimal('-0.125'))"
try:
    Fraction(1, 0)
    assert False, 'Fraction(1, 0)'
except ZeroDivisionError:
    pass
try:
    Fraction('1/2/3')
    assert False, "Fraction('1/2/3')"
except ValueError:
    pass
try:
    Fraction('abc')
    assert False, "Fraction('abc')"
except ValueError:
    pass
try:
    Fraction(float('nan'))
    assert False, "Fraction(float('nan'))"
except ValueError:
    pass
try:
    Fraction(float('inf'))
    assert False, "Fraction(float('inf'))"
except OverflowError:
    pass
try:
    Fraction(1.5, 2)
    assert False, 'Fraction(1.5, 2)'
except TypeError:
    pass
try:
    Fraction([1])
    assert False, 'Fraction([1])'
except TypeError:
    pass

# === text ===
assert (str(Fraction(3, 4))) == '3/4', 'str(Fraction(3, 4))'
assert (str(Fraction(8, 4))) == '2', 'str(Fraction(8, 4))'
assert (str(Fraction(-1, 3))) == '-1/3', 'str(Fraction(-1, 3))'
assert ((Fraction(3, 4).numerator, Fraction(3, 4).denominator)) == (3, 4), '(Fraction(3, 4).numerator, Fraction(3, 4).denominator)'
assert (Fraction(6, 4).as_integer_ratio()) == (3, 2), 'Fraction(6, 4).as_integer_ratio()'
assert (Fraction(4, 2).is_integer()) == True, 'Fraction(4, 2).is_integer()'
assert (Fraction(3, 2).is_integer()) == False, 'Fraction(3, 2).is_integer()'

# === arithmetic ===
a = Fraction(1, 3)
b = Fraction(1, 6)
assert repr(a + b) == 'Fraction(1, 2)', 'a + b'
assert repr(a - b) == 'Fraction(1, 6)', 'a - b'
assert repr(a * b) == 'Fraction(1, 18)', 'a * b'
assert repr(a / b) == 'Fraction(2, 1)', 'a / b'
assert repr(a + 1) == 'Fraction(4, 3)', 'a + 1'
assert repr(1 - a) == 'Fraction(2, 3)', '1 - a'
assert repr(2 * a) == 'Fraction(2, 3)', '2 * a'
assert repr(1 / a) == 'Fraction(3, 1)', '1 / a'
assert (a + 0.5) == 0.8333333333333333, 'a + 0.5'
assert (0.5 * a) == 0.16666666666666666, '0.5 * a'
assert repr(a ** 2) == 'Fraction(1, 9)', 'a ** 2'
assert repr(a ** -2) == 'Fraction(9, 1)', 'a ** -2'
assert repr((-a) ** 3) == 'Fraction(-1, 27)', '(-a) ** 3'
assert repr(2 ** Fraction(3)) == '8', '2 ** Fraction(3)'
assert (2 ** Fraction(1, 2)) == 1.4142135623730951, '2 ** Fraction(1, 2)'
assert (Fraction(9, 4) ** Fraction(1, 2)) == 1.5, 'Fraction(9, 4) ** Fraction(1, 2)'
assert (Fraction(7, 2) // Fraction(1, 3)) == 10, 'Fraction(7, 2) // Fraction(1, 3)'
assert repr(Fraction(7, 2) % Fraction(1, 3)) == 'Fraction(1, 6)', 'Fraction(7, 2) % Fraction(1, 3)'
assert (divmod(Fraction(7, 2), 2)) == (1, Fraction(3, 2)), 'divmod(Fraction(7, 2), 2)'
assert repr(-Fraction(-7, 2) % 2) == 'Fraction(3, 2)', '-Fraction(-7, 2) % 2'
assert (Fraction(-7, 2) // 2) == -2, 'Fraction(-7, 2) // 2'
assert repr(abs(Fraction(-3, 5))) == 'Fraction(3, 5)', 'abs(Fraction(-3, 5))'
assert repr(-Fraction(3, 5)) == 'Fraction(-3, 5)', '-Fraction(3, 5)'
assert repr(+Fraction(3, 5)) == 'Fraction(3, 5)', '+Fraction(3, 5)'
assert repr(sum([Fraction(1, n) for n in range(1, 6)])) == 'Fraction(137, 60)', 'sum([Fraction(1, n) for n in range(1, 6)])'
try:
    Fraction(1, 2) / 0
    assert False, 'Fraction(1, 2) / 0'
except ZeroDivisionError:
    pass
try:
    Fraction(0) ** -1
    assert False, 'Fraction(0) ** -1'
except ZeroDivisionError:
    pass
try:
    Fraction(1, 2) + 'x'
    assert False, "Fraction(1, 2) + 'x'"
except TypeError:
    pass

# === comparison and hashing ===
assert (Fraction(1, 2) == 0.5) == True, 'Fraction(1, 2) == 0.5'
assert (Fraction(1, 3) == 1 / 3) == False, 'Fraction(1, 3) == 1 / 3'
assert (Fraction(2, 1) == 2) == True, 'Fraction(2, 1) == 2'
assert (2 == Fraction(4, 2)) == True, '2 == Fraction(4, 2)'
assert (Fraction(1, 2) < Fraction(2, 3)) == True, 'Fraction(1, 2) < Fraction(2, 3)'
assert (1 < Fraction(3, 2)) == True, '1 < Fraction(3, 2)'
assert (Fraction(3, 2) > 1.4) == True, 'Fraction(3, 2) > 1.4'
assert (0.4 < Fraction(1, 2)) == True, '0.4 < Fraction(1, 2)'
assert (Fraction(1, 2) < float('inf')) == True, "Fraction(1, 2) < float('inf')"
assert (Fraction(1, 2) < float('nan')) == False, "Fraction(1, 2) < float('nan')"
assert (sorted([Fraction(1, 2), 0.25, 1, Fraction(1, 3)])) == [0.25, Fraction(1, 3), Fraction(1, 2), 1], 'sorted([Fraction(1, 2), 0.25, 1, Fraction(1, 3)])'
assert (max(Fraction(1, 2), Fraction(2, 3))) == Fraction(2, 3), 'max(Fraction(1, 2), Fraction(2, 3))'
assert (hash(Fraction(4, 2)) == hash(2)) == True, 'hash(Fraction(4, 2)) == hash(2)'
assert (hash(Fraction(1, 2)) == hash(0.5)) == True, 'hash(Fraction(1, 2)) == hash(0.5)'
assert (hash(Fraction(1, 3))) == 1537228672809129301, 'hash(Fraction(1, 3))'
assert (hash(Fraction(-1, 3))) == -1537228672809129301, 'hash(Fraction(-1, 3))'
assert ({Fraction(1, 2): 'half'}[0.5]) == 'half', "{Fraction(1, 2): 'half'}[0.5]"
assert (len({Fraction(1, 1), 1, 1.0})) == 1, 'len({Fraction(1, 1), 1, 1.0})'
assert (bool(Fraction(0)), bool(Fraction(1, 9))) == (False, True), 'bool(Fraction(0)), bool(Fraction(1, 9))'

# === conversions and rounding ===
assert (float(Fraction(1, 3))) == 0.3333333333333333, 'float(Fraction(1, 3))'
assert (float(Fraction(10**400, 10**399))) == 10.0, 'float(Fraction(10**400, 10**399))'
assert (int(Fraction(-7, 2))) == -3, 'int(Fraction(-7, 2))'
assert (math.floor(Fraction(-7, 2)), math.ceil(Fraction(-7, 2)), math.trunc(Fraction(-7, 2))) == (-4, -3, -3), 'math.floor(Fraction(-7, 2)), math.ceil(Fraction(-7, 2)), math.trunc(Fraction(-7, 2))'
assert (round(Fraction(5, 2)), round(Fraction(7, 2)), round(Fraction(-5, 2))) == (2, 4, -2), 'round(Fraction(5, 2)), round(Fraction(7, 2)), round(Fraction(-5, 2))'
assert repr(round(Fraction(22, 7), 3)) == 'Fraction(3143, 1000)', 'round(Fraction(22, 7), 3)'
assert repr(round(Fraction(12345, 1), -2)) == 'Fraction(12300, 1)', 'round(Fraction(12345, 1), -2)'
assert repr(Fraction('3.14159265358979').limit_denominator(1000)) == 'Fraction(355, 113)', "Fraction('3.14159265358979').limit_denominator(1000)"
assert repr(Fraction(1, 3).limit_denominator(2)) == 'Fraction(1, 2)', 'Fraction(1, 3).limit_denominator(2)'
assert repr(Fraction(0.1).limit_denominator()) == 'Fraction(1, 10)', 'Fraction(0.1).limit_denominator()'
try:
    Fraction(1, 3).limit_denominator(0)
    assert False, 'Fraction(1, 3).limit_denominator(0)'
except ValueError:
    pass
assert (isinstance(Fraction(1, 2), Fraction)) == True, 'isinstance(Fraction(1, 2), Fraction)'
assert (type(Fraction(1, 2)) is Fraction) == True, 'type(Fraction(1, 2)) is Fraction'
assert (type(Fraction(1, 2)).__name__) == 'Fraction', 'type(Fraction(1, 2)).__name__'

# === __format__ ===
assert (format(Fraction(1, 3), '.5f')) == '0.33333', "format(Fraction(1, 3), '.5f')"
assert (f'{Fraction(2, 3):.30f}') == '0.666666666666666666666666666667', "f'{Fraction(2, 3):.30f}'"
assert (f'{Fraction(1, 8):%}') == '12.500000%', "f'{Fraction(1, 8):%}'"
assert (f'{Fraction(1, 8):.1%}') == '12.5%', "f'{Fraction(1, 8):.1%}'"
assert (f'{Fraction(-1, 7):.3e}') == '-1.429e-01', "f'{Fraction(-1, 7):.3e}'"
assert (f'{Fraction(12345678, 1):,.2f}') == '12,345,678.00', "f'{Fraction(12345678, 1):,.2f}'"
assert (f'{Fraction(1, 3):>10}') == '       1/3', "f'{Fraction(1, 3):>10}'"
assert (f'{Fraction(1, 3):<8}|') == '1/3     |', "f'{Fraction(1, 3):<8}|'"
assert (f'{Fraction(3, 1):#}') == '3/1', "f'{Fraction(3, 1):#}'"
assert (f'{Fraction(1234567, 3):_}') == '1_234_567/3', "f'{Fraction(1234567, 3):_}'"
assert (f'{Fraction(-5, 2):+.2f}') == '-2.50', "f'{Fraction(-5, 2):+.2f}'"
assert (f'{Fraction(5, 2):+}') == '+5/2', "f'{Fraction(5, 2):+}'"
assert (f'{Fraction(1, 3):.3g}') == '0.333', "f'{Fraction(1, 3):.3g}'"
assert (f'{Fraction(10**8, 3):g}') == '3.33333e+07', "f'{Fraction(10**8, 3):g}'"
assert (f'{Fraction(1, 3):010.4f}') == '00000.3333', "f'{Fraction(1, 3):010.4f}'"
assert (f'{Fraction(-1, 3):=+10.2f}') == '-     0.33', "f'{Fraction(-1, 3):=+10.2f}'"
assert (f'{Fraction(1, 1000):g}') == '0.001', "f'{Fraction(1, 1000):g}'"
assert (f'{Fraction(-1, 1000000):z.2f}') == '0.00', "f'{Fraction(-1, 1000000):z.2f}'"
try:
    format(Fraction(1, 3), 'd')
    assert False, "format(Fraction(1, 3), 'd')"
except ValueError:
    pass
