# Extension: `string` (constants, capwords, Template), `shlex` (split, quote, join) and
# `contextlib` (contextmanager, suppress, closing, nullcontext, ExitStack). None exists in upstream
# Monty. Expected output is CPython 3.13's.
_lines = []

def _log(*parts):
    _lines.append(' '.join(str(p) for p in parts))


import string, shlex, contextlib
from contextlib import contextmanager, suppress, closing, nullcontext, ExitStack
_log(string.ascii_letters, string.digits, string.hexdigits, string.octdigits, repr(string.whitespace), string.punctuation, len(string.printable))
_log(string.capwords('hello   wORLD foo'), string.capwords('a-b c-d', '-'))
T = string.Template
_log(T('Hi $name, ${what}ing $$ $name.').substitute(name='Bob', what='go'))
_log(T('$a $b').safe_substitute(a=1), T('$ {x} $').safe_substitute(), T('${x').safe_substitute())
_log(T('$a').substitute({'a': 5}, a=6), T('$a').substitute({'a': 5}))
try: T('$a').substitute()
except KeyError as e: _log('KeyError', e)
for bad in ['x $', 'a\nb ${', '$1', 'line1\nline2 $-']:
    try: T(bad).substitute()
    except ValueError as e: _log(e)
_log(T('$a $b ${a} $$').get_identifiers(), T('$a $').is_valid(), T('$a').is_valid())
for s in ['a b c', "a 'b c' d", 'a "b c" d', r'a\ b c', '', '  ', "''", 'a""b', r'"a\"b"', r'"a\nb"', r'"a\\b"', "it's", ]:
    try: _log(repr(s), shlex.split(s))
    except ValueError as e: _log(repr(s), 'ValueError', e)
_log(shlex.split('a # c\nb', comments=True), shlex.split('a#b'))
try: shlex.split("a 'b")
except ValueError as e: _log(e)
try: shlex.split('a\\')
except ValueError as e: _log(e)
_log(shlex.quote(''), shlex.quote('abc'), shlex.quote('a b'), shlex.quote("it's"), shlex.quote('a$b'), shlex.quote('é'), shlex.join(['ls', 'my dir', '*.txt']))
@contextmanager
def cm(tag, fail=False):
    _log('enter', tag)
    try:
        yield tag.upper()
    except ValueError as e:
        _log('handled', e)
    finally:
        _log('exit', tag)
with cm('a') as v: _log('body', v)
with cm('b') as v: raise ValueError('boom')
try:
    with cm('c'): raise KeyError('k')
except KeyError as e: _log('propagated', e)
with suppress(KeyError, ValueError):
    raise ValueError('x')
_log('after suppress')
with suppress(): _log('nothing')
try:
    with suppress(KeyError): raise TypeError('t')
except TypeError: _log('not suppressed')
class R:
    def close(self): _log('closed')
with closing(R()) as r: _log('using')
with nullcontext(5) as n: _log(n)
with ExitStack() as st:
    st.callback(print, 'cb1', 'x')
    a = st.enter_context(cm('inner'))
    st.callback(lambda: _log('cb2'))
    _log('in stack', a)
try:
    with ExitStack() as st:
        st.callback(print, 'cleanup')
        raise RuntimeError('rt')
except RuntimeError as e: _log('rt propagated')
@contextmanager
def nogen():
    if False: yield
try:
    with nogen(): pass
except RuntimeError as e: _log(e)
@contextmanager
def twice():
    yield 1
    yield 2
try:
    with twice(): pass
except RuntimeError as e: _log(e)
_log(cm.__name__)


# === compare against what CPython printed ===
EXPECTED = ['abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ 0123456789 0123456789abcdefABCDEF 01234567 \' \\t\\n\\r\\x0b\\x0c\' !"#$%&\'()*+,-./:;<=>?@[\\]^_`{|}~ 100', 'Hello World Foo A-B c-D', 'Hi Bob, going $ Bob.', '1 $b $ {x} $ ${x', '6 5', "KeyError 'a'", 'Invalid placeholder in string: line 1, col 3', 'Invalid placeholder in string: line 2, col 3', 'Invalid placeholder in string: line 1, col 1', 'Invalid placeholder in string: line 2, col 7', "['a', 'b'] False True", "'a b c' ['a', 'b', 'c']", '"a \'b c\' d" [\'a\', \'b c\', \'d\']', '\'a "b c" d\' [\'a\', \'b c\', \'d\']', "'a\\\\ b c' ['a b', 'c']", "'' []", "'  ' []", '"\'\'" [\'\']', '\'a""b\' [\'ab\']', '\'"a\\\\"b"\' [\'a"b\']', '\'"a\\\\nb"\' [\'a\\\\nb\']', '\'"a\\\\\\\\b"\' [\'a\\\\b\']', '"it\'s" ValueError No closing quotation', "['a', 'b'] ['a#b']", 'No closing quotation', 'No escaped character', '\'\' abc \'a b\' \'it\'"\'"\'s\' \'a$b\' \'é\' ls \'my dir\' \'*.txt\'', 'enter a', 'body A', 'exit a', 'enter b', 'handled boom', 'exit b', 'enter c', 'exit c', "propagated 'k'", 'after suppress', 'nothing', 'not suppressed', 'using', 'closed', '5', 'enter inner', 'in stack INNER', 'cb2', 'exit inner', 'rt propagated', "generator didn't yield", "generator didn't stop", 'cm']

assert len(_lines) == len(EXPECTED), (len(_lines), len(EXPECTED))
for got, want in zip(_lines, EXPECTED):
    assert got == want, (got, want)
