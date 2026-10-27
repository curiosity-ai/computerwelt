# Extension: the rest of `json`.
# Upstream Monty's `json` has `loads` and `dumps` over a fixed keyword set. `default=`,
# `cls=`, `dump`/`load`, the decoder hooks and `JSONEncoder`/`JSONDecoder` are all rejected
# there; here they behave as they do in CPython (minus subclassing, which classes in this
# interpreter cannot do).
import io
import json
from json.decoder import JSONDecodeError

# === default= handles what JSON has no type for ===
def fallback(o):
    if isinstance(o, set):
        return sorted(o)
    raise TypeError(f'no way to encode {type(o).__name__}')

assert json.dumps({'s': {3, 1, 2}}, default=fallback) == '{"s": [1, 2, 3]}'
assert json.dumps([1, {2}], default=fallback) == '[1, [2]]'
try:
    json.dumps(b'x', default=fallback)
    assert False
except TypeError as exc:
    assert str(exc) == 'no way to encode bytes'

# === default= that keeps returning new objects runs out of stack, not memory ===
try:
    json.dumps({1}, default=lambda o: {2})
    assert False
except RecursionError:
    pass

# === check_circular=False skips the marker and relies on the recursion limit ===
loop = []
loop.append(loop)
try:
    json.dumps(loop, check_circular=False)
    assert False
except RecursionError:
    pass

# === dump and load go through the file protocol ===
buffer = io.StringIO()
assert json.dump({'a': [1, 2]}, buffer, sort_keys=True) is None
assert buffer.getvalue() == '{"a": [1, 2]}'
buffer.seek(0)
assert json.load(buffer) == {'a': [1, 2]}

path = '/virtual/json_roundtrip.json'
with open(path, 'w') as f:
    json.dump({'k': 'v', 'n': None}, f, indent=2)
with open(path) as f:
    assert f.read() == '{\n  "k": "v",\n  "n": null\n}'
with open(path) as f:
    assert json.load(f) == {'k': 'v', 'n': None}

# === object_hook sees every decoded object, innermost first ===
seen = []
def hook(d):
    seen.append(sorted(d))
    return d
json.loads('{"a": {"b": 1}, "c": 2}', object_hook=hook)
assert seen == [['b'], ['a', 'c']]
assert json.loads('{"a": 1}', object_hook=lambda d: len(d)) == 1

# === object_pairs_hook keeps order and duplicates, and wins over object_hook ===
assert json.loads('{"b": 1, "a": 2, "b": 3}', object_pairs_hook=lambda p: p) == [('b', 1), ('a', 2), ('b', 3)]
assert json.loads('{"x": 1}', object_pairs_hook=list, object_hook=lambda d: 'no') == [('x', 1)]

# === parse_float / parse_int / parse_constant ===
assert json.loads('[1.5, 2]', parse_float=str) == ['1.5', 2]
assert json.loads('[1.5, 2]', parse_int=str) == [1.5, '2']
assert json.loads('[1.5e2, -0.0]', parse_float=lambda t: t.upper()) == ['1.5E2', '-0.0']
assert json.loads('[NaN, Infinity, -Infinity]', parse_constant=lambda n: n) == ['NaN', 'Infinity', '-Infinity']

def refuse(name):
    raise ValueError('bad constant ' + name)
try:
    json.loads('[NaN]', parse_constant=refuse)
    assert False
except ValueError as exc:
    assert str(exc) == 'bad constant NaN'

# === strict=False tolerates raw control characters in strings ===
try:
    json.loads('"a\tb"')
    assert False
except JSONDecodeError as exc:
    assert str(exc) == 'Invalid control character at: line 1 column 3 (char 2)'
assert json.loads('"a\tb"', strict=False) == 'a\tb'

# === number grammar is JSON's, not Python's ===
for bad, message in [
    ('01', 'Extra data: line 1 column 2 (char 1)'),
    ('1.', 'Extra data: line 1 column 2 (char 1)'),
    ('.5', 'Expecting value: line 1 column 1 (char 0)'),
    ('+1', 'Expecting value: line 1 column 1 (char 0)'),
    ('-', 'Expecting value: line 1 column 1 (char 0)'),
    ('1e', 'Extra data: line 1 column 2 (char 1)'),
    ('[1 2]', "Expecting ',' delimiter: line 1 column 4 (char 3)"),
]:
    try:
        json.loads(bad)
        assert False, bad
    except JSONDecodeError as exc:
        assert str(exc) == message, (bad, str(exc))
assert json.loads('-0') == 0
assert json.loads('1E3') == 1000.0
assert json.loads('[1e400]') == [float('inf')]

# === only four characters are whitespace ===
assert json.loads(' \t\r\n[1]\n') == [1]
try:
    json.loads(' [1]')
    assert False
except JSONDecodeError:
    pass

# === \u escapes are validated digit by digit ===
assert json.loads('"\\u0041\\ud83d\\ude00"') == 'A\U0001f600'
for bad in ['"\\u 041"', '"\\u+041"', '"\\u00"']:
    try:
        json.loads(bad)
        assert False, bad
    except JSONDecodeError as exc:
        assert exc.msg == 'Invalid \\uXXXX escape', exc.msg

# === JSONDecodeError carries its pieces ===
try:
    json.loads('{\n  "a": tru\n}')
    assert False
except json.JSONDecodeError as exc:
    assert exc.msg == 'Expecting value'
    assert exc.doc == '{\n  "a": tru\n}'
    assert exc.pos == 9
    assert exc.lineno == 2
    assert exc.colno == 8
    assert isinstance(exc, ValueError)
    assert str(exc) == 'Expecting value: line 2 column 8 (char 9)'

e = json.JSONDecodeError('boom', 'abc\ndef', 5)
assert str(e) == 'boom: line 2 column 2 (char 5)'
assert (e.msg, e.doc, e.pos, e.lineno, e.colno) == ('boom', 'abc\ndef', 5, 2, 2)

# === a byte order mark on text is refused; on bytes it is honoured ===
try:
    json.loads('﻿[1]')
    assert False
except JSONDecodeError as exc:
    assert exc.msg == 'Unexpected UTF-8 BOM (decode using utf-8-sig)'
assert json.loads(b'\xef\xbb\xbf[1]') == [1]
assert json.loads('[1, "é"]'.encode('utf-16')) == [1, 'é']
assert json.loads('[1]'.encode('utf-16-le')) == [1]
assert json.loads('[1]'.encode('utf-32-be')) == [1]

# === nesting deeper than the recursion limit is an error, not a crash ===
try:
    json.loads('[' * 100000)
    assert False
except RecursionError:
    pass
try:
    json.loads('{"a":' * 100000)
    assert False
except RecursionError:
    pass

# === keyword and positional errors ===
for call, message in [
    (lambda: json.loads('1', bogus=1), "JSONDecoder.__init__() got an unexpected keyword argument 'bogus'"),
    (lambda: json.loads(), "loads() missing 1 required positional argument: 's'"),
    (lambda: json.dumps(), "dumps() missing 1 required positional argument: 'obj'"),
    (lambda: json.dumps(1, indent=1.5), "can't multiply sequence by non-int of type 'float'"),
]:
    try:
        call()
        assert False, message
    except TypeError as exc:
        assert str(exc) == message, str(exc)

# === JSONEncoder and JSONDecoder as objects ===
enc = json.JSONEncoder(sort_keys=True, indent=1, default=lambda o: str(o))
assert enc.encode({'b': 1, 'a': {2}}) == '{\n "a": "{2}",\n "b": 1\n}'
assert ''.join(enc.iterencode([1, 2])) == '[\n 1,\n 2\n]'
assert enc.sort_keys is True and enc.indent == 1 and enc.ensure_ascii is True
assert enc.item_separator == ',' and enc.key_separator == ': '
assert json.JSONEncoder().encode('é') == '"\\u00e9"'
assert json.JSONEncoder(ensure_ascii=False).encode('é') == '"é"'
try:
    json.JSONEncoder().default(object)
    assert False
except TypeError:
    pass
assert isinstance(enc, json.JSONEncoder)

dec = json.JSONDecoder(object_hook=lambda d: d.get('v'))
assert dec.decode('{"v": 7}') == 7
assert dec.raw_decode('[1, 2] trailing') == ([1, 2], 6)
try:
    json.JSONDecoder().decode('[1] x')
    assert False
except JSONDecodeError as exc:
    assert exc.pos == 4

# === cls= accepts the built-in classes, or any factory with encode/decode ===
assert json.dumps({'a': 1}, cls=json.JSONEncoder, indent=1) == '{\n "a": 1\n}'
assert json.loads('[1]', cls=json.JSONDecoder) == [1]

class Shout:
    def __init__(self, **kw):
        self.kw = kw
    def encode(self, o):
        return 'SHOUT:' + str(sorted(self.kw)[:2]) + repr(o)
    def decode(self, s):
        return ('decoded', s)
assert json.dumps(5, cls=Shout).startswith('SHOUT:')
assert json.loads('5', cls=Shout) == ('decoded', '5')

# === the encoder module's string helpers ===
import json.encoder
assert json.encoder.encode_basestring_ascii('é"') == '"\\u00e9\\""'
assert json.encoder.encode_basestring('é"') == '"é\\""'

# === sort_keys happens before skipkeys drops anything ===
try:
    json.dumps({1: 'a', 'b': 'c', (1, 2): 'd'}, sort_keys=True, skipkeys=True)
    assert False
except TypeError as exc:
    assert "'<' not supported between instances of" in str(exc)
assert json.dumps({(1, 2): 1, 'a': 2}, skipkeys=True) == '{"a": 2}'
assert json.dumps({(1, 2): 1}, skipkeys=True, indent=2) == '{\n  \n}'
assert json.dumps({}, indent=2) == '{}'

# === ensure_ascii=None is falsy, not "default" ===
assert json.dumps('é', ensure_ascii=None) == '"é"'
