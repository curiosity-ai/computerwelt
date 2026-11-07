# Extension: `decimal` — not in upstream Monty.
# Decimal arithmetic as CPython 3.13 computes it: exact operations rounded once to the
# context, results that keep their exponent (`Decimal('1.10')`), quantize with every rounding
# mode, and the context's precision. Signals always trap (see todo.md). Expected values
# produced by CPython 3.13.
import math
import decimal
from decimal import Decimal, getcontext, localcontext, InvalidOperation, DivisionByZero
from fractions import Fraction

# === construction and text ===
assert repr(Decimal('1.10')) == "Decimal('1.10')", "Decimal('1.10')"
assert repr(Decimal('-0.000')) == "Decimal('-0.000')", "Decimal('-0.000')"
assert repr(Decimal(' 3.14 ')) == "Decimal('3.14')", "Decimal(' 3.14 ')"
assert repr(Decimal('1_000.5')) == "Decimal('1000.5')", "Decimal('1_000.5')"
assert repr(Decimal('1e3')) == "Decimal('1E+3')", "Decimal('1e3')"
assert repr(Decimal('1E+3')) == "Decimal('1E+3')", "Decimal('1E+3')"
assert repr(Decimal('-1.23e-10')) == "Decimal('-1.23E-10')", "Decimal('-1.23e-10')"
assert repr(Decimal('123456789e20')) == "Decimal('1.23456789E+28')", "Decimal('123456789e20')"
assert repr(Decimal('0.0000001')) == "Decimal('1E-7')", "Decimal('0.0000001')"
assert repr(Decimal('0.000001')) == "Decimal('0.000001')", "Decimal('0.000001')"
assert repr(Decimal(42)) == "Decimal('42')", 'Decimal(42)'
assert repr(Decimal(-7)) == "Decimal('-7')", 'Decimal(-7)'
assert repr(Decimal(True)) == "Decimal('1')", 'Decimal(True)'
assert repr(Decimal(0.1)) == "Decimal('0.1000000000000000055511151231257827021181583404541015625')", 'Decimal(0.1)'
assert repr(Decimal(2.5)) == "Decimal('2.5')", 'Decimal(2.5)'
assert repr(Decimal(-0.0)) == "Decimal('-0')", 'Decimal(-0.0)'
assert repr(Decimal('Infinity')) == "Decimal('Infinity')", "Decimal('Infinity')"
assert repr(Decimal('-inf')) == "Decimal('-Infinity')", "Decimal('-inf')"
assert repr(Decimal('NaN')) == "Decimal('NaN')", "Decimal('NaN')"
assert repr(Decimal('sNaN')) == "Decimal('sNaN')", "Decimal('sNaN')"
assert repr(Decimal((1, (3, 1, 4), -2))) == "Decimal('-3.14')", 'Decimal((1, (3, 1, 4), -2))'
assert repr(Decimal((0, (), 0))) == "Decimal('0')", 'Decimal((0, (), 0))'
assert repr(Decimal(Decimal('5.0'))) == "Decimal('5.0')", "Decimal(Decimal('5.0'))"
assert repr(Decimal()) == "Decimal('0')", 'Decimal()'
try:
    Decimal('1.2.3')
    assert False, "Decimal('1.2.3')"
except InvalidOperation:
    pass
try:
    Decimal('abc')
    assert False, "Decimal('abc')"
except InvalidOperation:
    pass
assert (str(Decimal('1E+3'))) == '1E+3', "str(Decimal('1E+3'))"
assert (str(Decimal('100'))) == '100', "str(Decimal('100'))"
assert (str(Decimal('-0.00012300'))) == '-0.00012300', "str(Decimal('-0.00012300'))"
assert (Decimal('123E+5').to_eng_string()) == '12.3E+6', "Decimal('123E+5').to_eng_string()"
assert (Decimal('0.000123').to_eng_string()) == '0.000123', "Decimal('0.000123').to_eng_string()"
assert (Decimal('1E-7').to_eng_string()) == '100E-9', "Decimal('1E-7').to_eng_string()"
assert (Decimal('0E+4').to_eng_string()) == '0.00E+6', "Decimal('0E+4').to_eng_string()"
assert (tuple(Decimal("3.140").as_tuple())) == (0, (3, 1, 4, 0), -3), 'tuple(Decimal("3.140").as_tuple())'
assert (tuple(Decimal("-Infinity").as_tuple())) == (1, (0,), 'F'), 'tuple(Decimal("-Infinity").as_tuple())'
assert repr(Decimal("3.140").as_tuple()) == 'DecimalTuple(sign=0, digits=(3, 1, 4, 0), exponent=-3)', 'Decimal("3.140").as_tuple()'
assert (Decimal('1.25').as_integer_ratio()) == (5, 4), "Decimal('1.25').as_integer_ratio()"
assert (Decimal('-0.5').adjusted(), Decimal('123.4').adjusted()) == (-1, 2), "Decimal('-0.5').adjusted(), Decimal('123.4').adjusted()"
assert (isinstance(Decimal(1), Decimal), type(Decimal(1)) is Decimal, type(Decimal(1)).__name__) == (True, True, 'Decimal'), 'isinstance(Decimal(1), Decimal), type(Decimal(1)) is Decimal, type(Decimal(1)).__name__'

# === arithmetic keeps exponents ===
assert repr(Decimal('1.10') + Decimal('2.205')) == "Decimal('3.305')", "Decimal('1.10') + Decimal('2.205')"
assert repr(Decimal('1.30') - Decimal('1.3')) == "Decimal('0.00')", "Decimal('1.30') - Decimal('1.3')"
assert repr(Decimal('1.5') * Decimal('2.00')) == "Decimal('3.000')", "Decimal('1.5') * Decimal('2.00')"
assert repr(Decimal('3.0') * 2) == "Decimal('6.0')", "Decimal('3.0') * 2"
assert repr(2 * Decimal('3.0')) == "Decimal('6.0')", "2 * Decimal('3.0')"
assert repr(Decimal('0.1') + Decimal('0.2')) == "Decimal('0.3')", "Decimal('0.1') + Decimal('0.2')"
assert repr(Decimal(1) / Decimal(3)) == "Decimal('0.3333333333333333333333333333')", 'Decimal(1) / Decimal(3)'
assert repr(Decimal(2) / Decimal(3)) == "Decimal('0.6666666666666666666666666667')", 'Decimal(2) / Decimal(3)'
assert repr(Decimal(1) / Decimal(4)) == "Decimal('0.25')", 'Decimal(1) / Decimal(4)'
assert repr(Decimal('10') / Decimal('4')) == "Decimal('2.5')", "Decimal('10') / Decimal('4')"
assert repr(Decimal('100') / Decimal('10')) == "Decimal('10')", "Decimal('100') / Decimal('10')"
assert repr(Decimal('1.00') / Decimal('3')) == "Decimal('0.3333333333333333333333333333')", "Decimal('1.00') / Decimal('3')"
assert repr(Decimal(1) / 8) == "Decimal('0.125')", 'Decimal(1) / 8'
assert repr(1 / Decimal(8)) == "Decimal('0.125')", '1 / Decimal(8)'
assert repr(Decimal(7) // Decimal(2)) == "Decimal('3')", 'Decimal(7) // Decimal(2)'
assert repr(Decimal(-7) // Decimal(2)) == "Decimal('-3')", 'Decimal(-7) // Decimal(2)'
assert repr(Decimal(7) % Decimal(-2)) == "Decimal('1')", 'Decimal(7) % Decimal(-2)'
assert repr(Decimal(-7) % Decimal(2)) == "Decimal('-1')", 'Decimal(-7) % Decimal(2)'
assert repr(Decimal('7.5') % Decimal('2')) == "Decimal('1.5')", "Decimal('7.5') % Decimal('2')"
assert repr(Decimal('7.5') // 2) == "Decimal('3')", "Decimal('7.5') // 2"
assert (divmod(Decimal('7.5'), 2)) == (Decimal('3'), Decimal('1.5')), "divmod(Decimal('7.5'), 2)"
assert repr(Decimal(2) ** 10) == "Decimal('1024')", 'Decimal(2) ** 10'
assert repr(Decimal('1.1') ** 2) == "Decimal('1.21')", "Decimal('1.1') ** 2"
assert repr(Decimal(2) ** -2) == "Decimal('0.25')", 'Decimal(2) ** -2'
assert repr(Decimal('-2') ** 3) == "Decimal('-8')", "Decimal('-2') ** 3"
assert repr(Decimal(10) ** -3) == "Decimal('0.001')", 'Decimal(10) ** -3'
assert repr(Decimal('1.000001') ** 100) == "Decimal('1.000100004950161703921300289')", "Decimal('1.000001') ** 100"
assert repr(Decimal(2) ** Decimal('0.5')) == "Decimal('1.414213562373095048801688724')", "Decimal(2) ** Decimal('0.5')"
assert repr(Decimal(0) ** 5) == "Decimal('0')", 'Decimal(0) ** 5'
assert repr(-Decimal('1.50')) == "Decimal('-1.50')", "-Decimal('1.50')"
assert repr(+Decimal('1.234567890123456789012345678901')) == "Decimal('1.234567890123456789012345679')", "+Decimal('1.234567890123456789012345678901')"
assert repr(abs(Decimal('-3.20'))) == "Decimal('3.20')", "abs(Decimal('-3.20'))"
assert repr(-Decimal('0')) == "Decimal('0')", "-Decimal('0')"
assert repr(sum([Decimal('0.1')] * 10)) == "Decimal('1.0')", "sum([Decimal('0.1')] * 10)"
assert repr(sum([Decimal('1.1'), Decimal('2.2')], Decimal('0.00'))) == "Decimal('3.30')", "sum([Decimal('1.1'), Decimal('2.2')], Decimal('0.00'))"
assert repr(Decimal('1e100') + Decimal('1e-100')) == "Decimal('1.000000000000000000000000000E+100')", "Decimal('1e100') + Decimal('1e-100')"
assert repr(Decimal('12345678901234567890123456789') + 1) == "Decimal('1.234567890123456789012345679E+28')", "Decimal('12345678901234567890123456789') + 1"
assert repr(Decimal('Infinity') + 1) == "Decimal('Infinity')", "Decimal('Infinity') + 1"
assert repr(Decimal('NaN') + 1) == "Decimal('NaN')", "Decimal('NaN') + 1"
assert repr(Decimal(5) * Decimal('-Infinity')) == "Decimal('-Infinity')", "Decimal(5) * Decimal('-Infinity')"
try:
    Decimal(1) / 0
    assert False, 'Decimal(1) / 0'
except DivisionByZero:
    pass
try:
    Decimal(1) / Decimal(0)
    assert False, 'Decimal(1) / Decimal(0)'
except ZeroDivisionError:
    pass
try:
    Decimal(0) / 0
    assert False, 'Decimal(0) / 0'
except InvalidOperation:
    pass
try:
    Decimal(1) // 0
    assert False, 'Decimal(1) // 0'
except DivisionByZero:
    pass
try:
    Decimal(1) % 0
    assert False, 'Decimal(1) % 0'
except InvalidOperation:
    pass
try:
    Decimal('Infinity') - Decimal('Infinity')
    assert False, "Decimal('Infinity') - Decimal('Infinity')"
except InvalidOperation:
    pass
try:
    Decimal(0) ** 0
    assert False, 'Decimal(0) ** 0'
except InvalidOperation:
    pass
try:
    Decimal(-4) ** Decimal('0.5')
    assert False, "Decimal(-4) ** Decimal('0.5')"
except InvalidOperation:
    pass
try:
    Decimal(1) + 1.5
    assert False, 'Decimal(1) + 1.5'
except TypeError:
    pass
try:
    Decimal(1) + Fraction(1, 2)
    assert False, 'Decimal(1) + Fraction(1, 2)'
except TypeError:
    pass
try:
    Decimal('1') + '1'
    assert False, "Decimal('1') + '1'"
except TypeError:
    pass
try:
    Decimal(1) / 0
except DivisionByZero as error:
    message = str(error)
assert (message) == "[<class 'decimal.DivisionByZero'>]", 'message'
try:
    Decimal('x')
except InvalidOperation as error:
    message = str(error)
assert (message) == "[<class 'decimal.ConversionSyntax'>]", 'message'

# === comparison and hashing ===
assert (Decimal('1.0') == Decimal('1.00')) == True, "Decimal('1.0') == Decimal('1.00')"
assert (Decimal('1.0') == 1) == True, "Decimal('1.0') == 1"
assert (1 == Decimal('1.000')) == True, "1 == Decimal('1.000')"
assert (Decimal('0.5') == 0.5) == True, "Decimal('0.5') == 0.5"
assert (Decimal('0.1') == 0.1) == False, "Decimal('0.1') == 0.1"
assert (Decimal('0.5') == Fraction(1, 2)) == True, "Decimal('0.5') == Fraction(1, 2)"
assert (Decimal('2') < 3) == True, "Decimal('2') < 3"
assert (3 > Decimal('2.5')) == True, "3 > Decimal('2.5')"
assert (Decimal('1.5') < 2.0) == True, "Decimal('1.5') < 2.0"
assert (Decimal('-1') < Decimal('-0.5') < Decimal(0) < Decimal('1E-10')) == True, "Decimal('-1') < Decimal('-0.5') < Decimal(0) < Decimal('1E-10')"
assert (Decimal('NaN') == Decimal('NaN')) == False, "Decimal('NaN') == Decimal('NaN')"
assert (Decimal('Infinity') > Decimal('1e999')) == True, "Decimal('Infinity') > Decimal('1e999')"
assert (sorted([Decimal('2.5'), 1, Decimal('-1'), Decimal('0.10'), Decimal('0.1')])) == [Decimal('-1'), Decimal('0.10'), Decimal('0.1'), 1, Decimal('2.5')], "sorted([Decimal('2.5'), 1, Decimal('-1'), Decimal('0.10'), Decimal('0.1')])"
assert (max([Decimal('1.5'), Decimal('1.50'), Decimal('0.9')])) == Decimal('1.5'), "max([Decimal('1.5'), Decimal('1.50'), Decimal('0.9')])"
assert (hash(Decimal('2.0')) == hash(2)) == True, "hash(Decimal('2.0')) == hash(2)"
assert (hash(Decimal('0.5')) == hash(0.5)) == True, "hash(Decimal('0.5')) == hash(0.5)"
assert (hash(Decimal('1.1'))) == 2075258708292324557, "hash(Decimal('1.1'))"
assert (hash(Decimal('-123.456'))) == -1715547198854988423, "hash(Decimal('-123.456'))"
assert (len({Decimal('1'), Decimal('1.0'), 1})) == 1, "len({Decimal('1'), Decimal('1.0'), 1})"
assert (bool(Decimal('0.00')), bool(Decimal('0.01'))) == (False, True), "bool(Decimal('0.00')), bool(Decimal('0.01'))"
try:
    Decimal('NaN') < 1
    assert False, "Decimal('NaN') < 1"
except InvalidOperation:
    pass

# === conversions ===
assert (float(Decimal('0.1'))) == 0.1, "float(Decimal('0.1'))"
assert repr(float(Decimal('1e400'))) == 'inf', "float(Decimal('1e400'))"
assert (float(Decimal('-1.5e-3'))) == -0.0015, "float(Decimal('-1.5e-3'))"
assert (int(Decimal('-7.9'))) == -7, "int(Decimal('-7.9'))"
assert (int(Decimal('1e3'))) == 1000, "int(Decimal('1e3'))"
assert (math.floor(Decimal('-2.5')), math.ceil(Decimal('-2.5')), math.trunc(Decimal('-2.5'))) == (-3, -2, -2), "math.floor(Decimal('-2.5')), math.ceil(Decimal('-2.5')), math.trunc(Decimal('-2.5'))"
assert (math.floor(Decimal('2.0')), math.ceil(Decimal('2.000001'))) == (2, 3), "math.floor(Decimal('2.0')), math.ceil(Decimal('2.000001'))"
assert (round(Decimal('2.5')), round(Decimal('3.5')), round(Decimal('-2.5'))) == (2, 4, -2), "round(Decimal('2.5')), round(Decimal('3.5')), round(Decimal('-2.5'))"
assert repr(round(Decimal('3.14159'), 2)) == "Decimal('3.14')", "round(Decimal('3.14159'), 2)"
assert repr(round(Decimal('2.675'), 2)) == "Decimal('2.68')", "round(Decimal('2.675'), 2)"
assert repr(round(Decimal('1234.5'), -2)) == "Decimal('1.2E+3')", "round(Decimal('1234.5'), -2)"
assert (math.sqrt(Decimal(2))) == 1.4142135623730951, 'math.sqrt(Decimal(2))'
try:
    int(Decimal('NaN'))
    assert False, "int(Decimal('NaN'))"
except ValueError:
    pass
try:
    int(Decimal('Infinity'))
    assert False, "int(Decimal('Infinity'))"
except OverflowError:
    pass

# === quantize and the rounding modes ===
value = Decimal('7.325')
assert repr(value.quantize(Decimal('0.01'))) == "Decimal('7.32')", "value.quantize(Decimal('0.01'))"
assert repr(value.quantize(Decimal('.01'), rounding=decimal.ROUND_HALF_UP)) == "Decimal('7.33')", "value.quantize(Decimal('.01'), rounding=decimal.ROUND_HALF_UP)"
assert repr(value.quantize(Decimal('.01'), rounding=decimal.ROUND_HALF_EVEN)) == "Decimal('7.32')", "value.quantize(Decimal('.01'), rounding=decimal.ROUND_HALF_EVEN)"
assert repr(value.quantize(Decimal('.01'), rounding=decimal.ROUND_HALF_DOWN)) == "Decimal('7.32')", "value.quantize(Decimal('.01'), rounding=decimal.ROUND_HALF_DOWN)"
assert repr(value.quantize(Decimal('.01'), rounding=decimal.ROUND_DOWN)) == "Decimal('7.32')", "value.quantize(Decimal('.01'), rounding=decimal.ROUND_DOWN)"
assert repr(value.quantize(Decimal('.01'), rounding=decimal.ROUND_UP)) == "Decimal('7.33')", "value.quantize(Decimal('.01'), rounding=decimal.ROUND_UP)"
assert repr(value.quantize(Decimal('.01'), rounding=decimal.ROUND_FLOOR)) == "Decimal('7.32')", "value.quantize(Decimal('.01'), rounding=decimal.ROUND_FLOOR)"
assert repr(value.quantize(Decimal('.01'), rounding=decimal.ROUND_CEILING)) == "Decimal('7.33')", "value.quantize(Decimal('.01'), rounding=decimal.ROUND_CEILING)"
assert repr(value.quantize(Decimal('.01'), rounding=decimal.ROUND_05UP)) == "Decimal('7.32')", "value.quantize(Decimal('.01'), rounding=decimal.ROUND_05UP)"
assert repr((-value).quantize(Decimal('.01'), rounding=decimal.ROUND_FLOOR)) == "Decimal('-7.33')", "(-value).quantize(Decimal('.01'), rounding=decimal.ROUND_FLOOR)"
assert repr((-value).quantize(Decimal('.01'), rounding=decimal.ROUND_CEILING)) == "Decimal('-7.32')", "(-value).quantize(Decimal('.01'), rounding=decimal.ROUND_CEILING)"
assert repr((-value).quantize(Decimal('1'), rounding=decimal.ROUND_UP)) == "Decimal('-8')", "(-value).quantize(Decimal('1'), rounding=decimal.ROUND_UP)"
assert repr(Decimal('1.5').quantize(Decimal('1.000'))) == "Decimal('1.500')", "Decimal('1.5').quantize(Decimal('1.000'))"
assert repr(Decimal('123.456').quantize(Decimal('1E1'))) == "Decimal('1.2E+2')", "Decimal('123.456').quantize(Decimal('1E1'))"
assert repr(Decimal('0.0001').quantize(Decimal('0.01'))) == "Decimal('0.00')", "Decimal('0.0001').quantize(Decimal('0.01'))"
assert repr(Decimal('-0.0001').quantize(Decimal('0.01'))) == "Decimal('-0.00')", "Decimal('-0.0001').quantize(Decimal('0.01'))"
assert repr(Decimal('9.999').quantize(Decimal('0.01'))) == "Decimal('10.00')", "Decimal('9.999').quantize(Decimal('0.01'))"
assert repr(Decimal('2.5').quantize(Decimal(1), decimal.ROUND_HALF_UP)) == "Decimal('3')", "Decimal('2.5').quantize(Decimal(1), decimal.ROUND_HALF_UP)"
try:
    Decimal('1e30').quantize(Decimal('0.01'))
    assert False, "Decimal('1e30').quantize(Decimal('0.01'))"
except InvalidOperation:
    pass
assert repr(Decimal('1.20').to_integral_value()) == "Decimal('1')", "Decimal('1.20').to_integral_value()"
assert repr(Decimal('2.5').to_integral_value()) == "Decimal('2')", "Decimal('2.5').to_integral_value()"
assert repr(Decimal('2.5').to_integral_value(rounding=decimal.ROUND_HALF_UP)) == "Decimal('3')", "Decimal('2.5').to_integral_value(rounding=decimal.ROUND_HALF_UP)"
assert repr(Decimal('-2.5').to_integral(decimal.ROUND_FLOOR)) == "Decimal('-3')", "Decimal('-2.5').to_integral(decimal.ROUND_FLOOR)"
assert repr(Decimal('1E+2').to_integral_value()) == "Decimal('1E+2')", "Decimal('1E+2').to_integral_value()"
assert repr(Decimal('120.00').normalize()) == "Decimal('1.2E+2')", "Decimal('120.00').normalize()"
assert repr(Decimal('0.000').normalize()) == "Decimal('0')", "Decimal('0.000').normalize()"
assert repr(Decimal('1E2').normalize()) == "Decimal('1E+2')", "Decimal('1E2').normalize()"

# === sqrt and the predicates ===
assert repr(Decimal(2).sqrt()) == "Decimal('1.414213562373095048801688724')", 'Decimal(2).sqrt()'
assert repr(Decimal('0.25').sqrt()) == "Decimal('0.5')", "Decimal('0.25').sqrt()"
assert repr(Decimal(16).sqrt()) == "Decimal('4')", 'Decimal(16).sqrt()'
assert repr(Decimal('1E+2').sqrt()) == "Decimal('1E+1')", "Decimal('1E+2').sqrt()"
assert repr(Decimal('1.21').sqrt()) == "Decimal('1.1')", "Decimal('1.21').sqrt()"
assert repr(Decimal(0).sqrt()) == "Decimal('0')", 'Decimal(0).sqrt()'
try:
    Decimal(-1).sqrt()
    assert False, 'Decimal(-1).sqrt()'
except InvalidOperation:
    pass
assert (Decimal('NaN').is_nan(), Decimal('sNaN').is_snan(), Decimal('NaN').is_qnan()) == (True, True, True), "Decimal('NaN').is_nan(), Decimal('sNaN').is_snan(), Decimal('NaN').is_qnan()"
assert (Decimal('-Infinity').is_infinite(), Decimal('1').is_finite(), Decimal('0.0').is_zero()) == (True, True, True), "Decimal('-Infinity').is_infinite(), Decimal('1').is_finite(), Decimal('0.0').is_zero()"
assert (Decimal('-0').is_signed(), Decimal('5').is_signed()) == (True, False), "Decimal('-0').is_signed(), Decimal('5').is_signed()"
assert repr(Decimal('-1.5').copy_abs()) == "Decimal('1.5')", "Decimal('-1.5').copy_abs()"
assert repr(Decimal('1.5').copy_negate()) == "Decimal('-1.5')", "Decimal('1.5').copy_negate()"
assert repr(Decimal('1.5').copy_sign(Decimal('-2'))) == "Decimal('-1.5')", "Decimal('1.5').copy_sign(Decimal('-2'))"
assert repr(Decimal('1.5').max(Decimal('2'))) == "Decimal('2')", "Decimal('1.5').max(Decimal('2'))"
assert repr(Decimal('1.5').min(Decimal('2'))) == "Decimal('1.5')", "Decimal('1.5').min(Decimal('2'))"
assert repr(Decimal('2.0').max(Decimal('2'))) == "Decimal('2')", "Decimal('2.0').max(Decimal('2'))"
assert repr(Decimal(1).compare(Decimal(2))) == "Decimal('-1')", 'Decimal(1).compare(Decimal(2))'
assert repr(Decimal(2).compare(Decimal('2.00'))) == "Decimal('0')", "Decimal(2).compare(Decimal('2.00'))"

# === the context ===
assert (getcontext().prec) == 28, 'getcontext().prec'
assert (getcontext().rounding) == 'ROUND_HALF_EVEN', 'getcontext().rounding'
getcontext().prec = 6
assert repr(Decimal(1) / Decimal(7)) == "Decimal('0.142857')", 'Decimal(1) / Decimal(7)'
assert repr(Decimal('123.456789') + 0) == "Decimal('123.457')", "Decimal('123.456789') + 0"
assert repr(+Decimal('123.456789')) == "Decimal('123.457')", "+Decimal('123.456789')"
assert repr(Decimal('3.1415926535')) == "Decimal('3.1415926535')", "Decimal('3.1415926535')"
assert repr(Decimal(2).sqrt()) == "Decimal('1.41421')", 'Decimal(2).sqrt()'
getcontext().rounding = decimal.ROUND_DOWN
assert repr(Decimal(2) / 3) == "Decimal('0.666666')", 'Decimal(2) / 3'
getcontext().prec = 28
getcontext().rounding = decimal.ROUND_HALF_EVEN
with localcontext() as ctx:
    ctx.prec = 3
    third = Decimal(1) / 3
assert repr(third) == "Decimal('0.333')", 'third'
assert repr(Decimal(1) / 3) == "Decimal('0.3333333333333333333333333333')", 'Decimal(1) / 3'
with localcontext(prec=50):
    long_third = Decimal(1) / 3
assert repr(long_third) == "Decimal('0.33333333333333333333333333333333333333333333333333')", 'long_third'
assert repr(getcontext().create_decimal('3.14159265358979323846264338327950288')) == "Decimal('3.141592653589793238462643383')", "getcontext().create_decimal('3.14159265358979323846264338327950288')"
try:
    setattr(getcontext(), 'prec', 0)
    assert False, "setattr(getcontext(), 'prec', 0)"
except ValueError:
    pass
try:
    setattr(getcontext(), 'rounding', 'sideways')
    assert False, "setattr(getcontext(), 'rounding', 'sideways')"
except TypeError:
    pass

# === __format__ ===
assert (format(Decimal('1234.5678'), '.2f')) == '1234.57', "format(Decimal('1234.5678'), '.2f')"
assert (f'{Decimal("1234.5678"):,.2f}') == '1,234.57', 'f\'{Decimal("1234.5678"):,.2f}\''
assert (f'{Decimal("0.125"):.2f}') == '0.12', 'f\'{Decimal("0.125"):.2f}\''
assert (f'{Decimal("0.135"):.2f}') == '0.14', 'f\'{Decimal("0.135"):.2f}\''
assert (f'{Decimal("1.1"):.5f}') == '1.10000', 'f\'{Decimal("1.1"):.5f}\''
assert (f'{Decimal("1234.5"):e}') == '1.2345e+3', 'f\'{Decimal("1234.5"):e}\''
assert (f'{Decimal("1234.5"):.2e}') == '1.23e+3', 'f\'{Decimal("1234.5"):.2e}\''
assert (f'{Decimal("0.25"):%}') == '25%', 'f\'{Decimal("0.25"):%}\''
assert (f'{Decimal("0.25"):.1%}') == '25.0%', 'f\'{Decimal("0.25"):.1%}\''
assert (f'{Decimal("12.50")}') == '12.50', 'f\'{Decimal("12.50")}\''
assert (f'{Decimal("12.50"):>10}') == '     12.50', 'f\'{Decimal("12.50"):>10}\''
assert (f'{Decimal("-12.5"):010.2f}') == '-000012.50', 'f\'{Decimal("-12.5"):010.2f}\''
assert (f'{Decimal("12.5"):+.1f}') == '+12.5', 'f\'{Decimal("12.5"):+.1f}\''
assert (f'{Decimal("1E+5"):f}') == '100000', 'f\'{Decimal("1E+5"):f}\''
assert (f'{Decimal("123456789.123"):g}') == '123456789.123', 'f\'{Decimal("123456789.123"):g}\''
assert (f'{Decimal("0.00001234"):g}') == '0.00001234', 'f\'{Decimal("0.00001234"):g}\''
assert (f'{Decimal("1234567"):_}') == '1_234_567', 'f\'{Decimal("1234567"):_}\''
assert (f'{Decimal("NaN"):>6}') == '   NaN', 'f\'{Decimal("NaN"):>6}\''
assert (f'{Decimal("-Infinity"):f}') == '-Infinity', 'f\'{Decimal("-Infinity"):f}\''
