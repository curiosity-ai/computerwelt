# Extension (fix): round(x, n) rounds the exact binary value half-to-even, as
# CPython does, so round(2.675, 2) is 2.67 (the double is 2.67499999...).
assert round(2.675, 2) == 2.67, round(2.675, 2)
assert round(0.125, 2) == 0.12 and round(0.375, 2) == 0.38 and round(2.5) == 2 and round(3.5) == 4
assert round(1250.6, -2) == 1300.0 and round(1250.0, -2) == 1200.0 and round(-0.4) == 0
assert str(round(-0.4, 0)) == '-0.0' and round(1.005, 2) == 1.0 and round(0.1 + 0.2, 1) == 0.3
assert round(123.456, 1) == 123.5 and round(1e300, 2) == 1e300 and round(5e-324, 400) == 5e-324
