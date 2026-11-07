# Extension: `from module import *`. Upstream refuses wildcard imports
# (monty-spec/import__star_error.py, xfail=cpython). Public names, or `__all__`.
from math import *
assert floor(2.5) == 2 and pi > 3 and sqrt(16) == 4.0
from string import *
assert ascii_lowercase.startswith('abc')
try:
    _ = _sentinel_that_does_not_exist
except NameError:
    pass
