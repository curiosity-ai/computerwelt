# Extension: `warnings` and the warning categories — not in upstream Monty.
# CPython 3.13's filters (`simplefilter`, `filterwarnings`, the default ignore list), the
# once-per-location rule of the `default` action, `error` raising the category, and
# `catch_warnings(record=True)`. Expected values produced by CPython 3.13.
import warnings

# === the categories are builtins ===
assert (issubclass(UserWarning, Warning), issubclass(Warning, Exception)) == (True, True), 'issubclass(UserWarning, Warning), issubclass(Warning, Exception)'
assert ([c.__name__ for c in (DeprecationWarning, PendingDeprecationWarning, RuntimeWarning, FutureWarning, SyntaxWarning, ImportWarning, UnicodeWarning, BytesWarning, ResourceWarning, EncodingWarning)]) == ['DeprecationWarning', 'PendingDeprecationWarning', 'RuntimeWarning', 'FutureWarning', 'SyntaxWarning', 'ImportWarning', 'UnicodeWarning', 'BytesWarning', 'ResourceWarning', 'EncodingWarning'], '[c.__name__ for c in (DeprecationWarning, PendingDeprecationWarning, RuntimeWarning, FutureWarning, SyntaxWarning, ImportWarning, UnicodeWarning, BytesWarning, ResourceWarning, EncodingWarning)]'
assert (all(issubclass(c, Warning) for c in (DeprecationWarning, RuntimeWarning, FutureWarning))) == True, 'all(issubclass(c, Warning) for c in (DeprecationWarning, RuntimeWarning, FutureWarning))'

# === recording ===
with warnings.catch_warnings(record=True) as caught:
    warnings.simplefilter('always')
    warnings.warn('first')
    warnings.warn('second', RuntimeWarning)
    warnings.warn(FutureWarning('third'))
    warnings.warn('deprecated thing', DeprecationWarning)
assert ([(str(w.message), w.category.__name__) for w in caught]) == [('first', 'UserWarning'), ('second', 'RuntimeWarning'), ('third', 'FutureWarning'), ('deprecated thing', 'DeprecationWarning')], '[(str(w.message), w.category.__name__) for w in caught]'
assert (isinstance(caught[0].message, UserWarning)) == True, 'isinstance(caught[0].message, UserWarning)'
assert (type(caught[2].message) is FutureWarning) == True, 'type(caught[2].message) is FutureWarning'
assert (caught[0].lineno > 0) == True, 'caught[0].lineno > 0'
assert ([w.lineno - caught[0].lineno for w in caught]) == [0, 1, 2, 3], '[w.lineno - caught[0].lineno for w in caught]'

# === the default action shows a location once ===
def emit(text):
    warnings.warn(text, UserWarning, stacklevel=2)

with warnings.catch_warnings(record=True) as caught:
    warnings.simplefilter('default')
    for _ in range(3):
        warnings.warn('repeated')
    for _ in range(2):
        emit('from a helper')
assert ([str(w.message) for w in caught]) == ['repeated', 'from a helper'], '[str(w.message) for w in caught]'

with warnings.catch_warnings(record=True) as caught:
    warnings.simplefilter('once')
    warnings.warn('one')
    warnings.warn('one')
    warnings.warn('two')
assert ([str(w.message) for w in caught]) == ['one', 'two'], '[str(w.message) for w in caught]'

# === ignore, error and the default filter list ===
with warnings.catch_warnings(record=True) as caught:
    warnings.simplefilter('ignore')
    warnings.warn('silent')
assert (len(caught)) == 0, 'len(caught)'

with warnings.catch_warnings():
    warnings.simplefilter('error')
    try:
        warnings.warn('loud', RuntimeWarning)
        outcome = 'not raised'
    except RuntimeWarning as error:
        outcome = 'raised: ' + str(error)
assert (outcome) == 'raised: loud', 'outcome'

with warnings.catch_warnings(record=True) as caught:
    warnings.simplefilter('always')
    warnings.simplefilter('error', category=DeprecationWarning)
    warnings.warn('fine', UserWarning)
    try:
        warnings.warn('old', DeprecationWarning)
    except DeprecationWarning:
        caught.append('raised')
assert ([w if isinstance(w, str) else str(w.message) for w in caught]) == ['fine', 'raised'], '[w if isinstance(w, str) else str(w.message) for w in caught]'

with warnings.catch_warnings(record=True) as caught:
    warnings.filterwarnings('ignore', message='skip.*')
    warnings.filterwarnings('always')
    warnings.filterwarnings('ignore', message='skip')
    warnings.warn('skip me')
    warnings.warn('keep me')
assert ([str(w.message) for w in caught]) == ['keep me'], '[str(w.message) for w in caught]'

with warnings.catch_warnings(record=True, action='always') as caught:
    warnings.warn('via action')
assert ([str(w.message) for w in caught]) == ['via action'], '[str(w.message) for w in caught]'

# === filters are restored ===
before = len(warnings.filters)
with warnings.catch_warnings():
    warnings.simplefilter('ignore')
    warnings.simplefilter('error', RuntimeWarning)
assert (len(warnings.filters) == before) == True, 'len(warnings.filters) == before'
assert (warnings.filters[0][0], warnings.filters[0][2].__name__) == ('default', 'DeprecationWarning'), 'warnings.filters[0][0], warnings.filters[0][2].__name__'

# === formatting and errors ===
assert (warnings.formatwarning('msg', UserWarning, 'file.py', 12)) == 'file.py:12: UserWarning: msg\n', "warnings.formatwarning('msg', UserWarning, 'file.py', 12)"
assert (warnings.formatwarning('msg', RuntimeWarning, 'file.py', 3, 'x = 1  ')) == 'file.py:3: RuntimeWarning: msg\n  x = 1\n', "warnings.formatwarning('msg', RuntimeWarning, 'file.py', 3, 'x = 1  ')"
try:
    warnings.simplefilter('loudly')
    assert False, "warnings.simplefilter('loudly')"
except ValueError:
    pass
try:
    warnings.warn('x', category=int)
    assert False, "warnings.warn('x', category=int)"
except TypeError:
    pass
try:
    warnings.filterwarnings('always', lineno=-1)
    assert False, "warnings.filterwarnings('always', lineno=-1)"
except ValueError:
    pass
