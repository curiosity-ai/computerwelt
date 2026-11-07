# Extension: complex numbers (literals, arithmetic, complex(), cmath). Upstream
# Monty rejects complex constants at parse time; monty-spec/parse_error__complex.py,
# which pins that, is xfail=cpython and so outside the ratchet.
z = 1 + 2j
assert repr(z) == '(1+2j)' and repr(2j) == '2j' and repr(-1j) == '(-0-1j)'
assert repr(1.5j) == '1.5j' and repr(complex(1, -2)) == '(1-2j)'
assert z * z == -3 + 4j and (1+1j)**2 == 2j and abs(3+4j) == 5.0
assert z.real == 1.0 and z.imag == 2.0 and z.conjugate() == 1 - 2j
assert complex('1+2j') == z and complex('3') == 3 and complex('-2j') == -2j
assert 1 + 0j == 1 and 1 == 1 + 0j and hash(1 + 0j) == hash(1)
assert type(z) is complex and isinstance(z, complex) and (z / 2) == 0.5 + 1j
import cmath
assert cmath.sqrt(-1) == 1j and abs(cmath.exp(1j * cmath.pi) + 1) < 1e-15
assert cmath.polar(1j) == (1.0, cmath.pi / 2)
try:
    1j / 0
except ZeroDivisionError:
    pass
else:
    assert False
