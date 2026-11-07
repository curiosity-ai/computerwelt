# Extension: `types` and `secrets` — not in upstream Monty.
# SimpleNamespace, MappingProxyType and the interpreter's own type objects; secrets' tokens and
# choices from the host's cryptographic generator (so only their shapes are pinned).
# Expected values produced by CPython 3.13.
import types
import secrets
import string

# === SimpleNamespace ===
ns = types.SimpleNamespace(name='box', size=3)
assert repr(ns) == "namespace(name='box', size=3)", 'ns'
assert (ns.name, ns.size) == ('box', 3), 'ns.name, ns.size'
ns.size = 4
ns.color = 'red'
assert repr(ns) == "namespace(name='box', size=4, color='red')", 'ns'
assert (ns == types.SimpleNamespace(name='box', size=4, color='red')) == True, "ns == types.SimpleNamespace(name='box', size=4, color='red')"
assert (ns == types.SimpleNamespace(name='box')) == False, "ns == types.SimpleNamespace(name='box')"
assert repr(types.SimpleNamespace()) == 'namespace()', 'types.SimpleNamespace()'
assert repr(types.SimpleNamespace({'a': 1}, b=2)) == 'namespace(a=1, b=2)', "types.SimpleNamespace({'a': 1}, b=2)"
assert (isinstance(ns, types.SimpleNamespace), type(ns) is types.SimpleNamespace) == (True, True), 'isinstance(ns, types.SimpleNamespace), type(ns) is types.SimpleNamespace'
assert (ns.__dict__) == {'name': 'box', 'size': 4, 'color': 'red'}, 'ns.__dict__'
assert (getattr(ns, 'missing', 'default')) == 'default', "getattr(ns, 'missing', 'default')"
try:
    ns.missing
    assert False, 'ns.missing'
except AttributeError:
    pass
nested = types.SimpleNamespace(inner=types.SimpleNamespace(x=1))
assert repr(nested) == 'namespace(inner=namespace(x=1))', 'nested'
assert (nested.inner.x) == 1, 'nested.inner.x'

# === MappingProxyType ===
backing = {'a': 1, 'b': 2}
view = types.MappingProxyType(backing)
assert repr(view) == "mappingproxy({'a': 1, 'b': 2})", 'view'
assert (view['a'], len(view), 'b' in view, 'z' in view) == (1, 2, True, False), "view['a'], len(view), 'b' in view, 'z' in view"
assert (list(view), list(view.keys()), list(view.values()), list(view.items())) == (['a', 'b'], ['a', 'b'], [1, 2], [('a', 1), ('b', 2)]), 'list(view), list(view.keys()), list(view.values()), list(view.items())'
assert (view.get('z', 0), view.get('a')) == (0, 1), "view.get('z', 0), view.get('a')"
backing['c'] = 3
assert (view['c']) == 3, "view['c']"
assert (dict(view)) == {'a': 1, 'b': 2, 'c': 3}, 'dict(view)'
assert (view.copy()) == {'a': 1, 'b': 2, 'c': 3}, 'view.copy()'
assert (view == {'a': 1, 'b': 2, 'c': 3}) == True, "view == {'a': 1, 'b': 2, 'c': 3}"
try:
    view.update({"x": 1})
    assert False, 'view.update({"x": 1})'
except AttributeError:
    pass
try:
    view['a'] = 10
    outcome = 'assigned'
except TypeError as error:
    outcome = str(error)
assert (outcome) == "'mappingproxy' object does not support item assignment", 'outcome'
try:
    types.MappingProxyType([1, 2])
    assert False, 'types.MappingProxyType([1, 2])'
except TypeError:
    pass
assert (isinstance(view, types.MappingProxyType)) == True, 'isinstance(view, types.MappingProxyType)'

# === the type objects ===
def function():
    yield 1
assert (isinstance(function, types.FunctionType), isinstance(lambda: 0, types.LambdaType)) == (True, True), 'isinstance(function, types.FunctionType), isinstance(lambda: 0, types.LambdaType)'
assert (isinstance(function(), types.GeneratorType)) == True, 'isinstance(function(), types.GeneratorType)'
assert (isinstance(types, types.ModuleType)) == True, 'isinstance(types, types.ModuleType)'
assert (isinstance(len, types.BuiltinFunctionType)) == True, 'isinstance(len, types.BuiltinFunctionType)'
assert (types.NoneType is type(None)) == True, 'types.NoneType is type(None)'
assert (isinstance(..., types.EllipsisType)) == True, 'isinstance(..., types.EllipsisType)'

# === secrets ===
assert (len(secrets.token_bytes(16)), type(secrets.token_bytes()).__name__, len(secrets.token_bytes())) == (16, 'bytes', 32), 'len(secrets.token_bytes(16)), type(secrets.token_bytes()).__name__, len(secrets.token_bytes())'
assert (len(secrets.token_hex(8)), all(c in '0123456789abcdef' for c in secrets.token_hex())) == (16, True), "len(secrets.token_hex(8)), all(c in '0123456789abcdef' for c in secrets.token_hex())"
assert (len(secrets.token_urlsafe(32)), all(c in string.ascii_letters + string.digits + '-_' for c in secrets.token_urlsafe())) == (43, True), "len(secrets.token_urlsafe(32)), all(c in string.ascii_letters + string.digits + '-_' for c in secrets.token_urlsafe())"
assert (len(secrets.token_urlsafe(10))) == 14, 'len(secrets.token_urlsafe(10))'
assert (secrets.token_bytes(0), secrets.token_hex(0)) == (b'', ''), 'secrets.token_bytes(0), secrets.token_hex(0)'
assert (all(0 <= secrets.randbelow(10) < 10 for _ in range(50))) == True, 'all(0 <= secrets.randbelow(10) < 10 for _ in range(50))'
assert (secrets.randbelow(1)) == 0, 'secrets.randbelow(1)'
assert (all(0 <= secrets.randbits(5) < 32 for _ in range(50)), secrets.randbits(0)) == (True, 0), 'all(0 <= secrets.randbits(5) < 32 for _ in range(50)), secrets.randbits(0)'
assert (secrets.choice('aaa'), secrets.choice([7])) == ('a', 7), "secrets.choice('aaa'), secrets.choice([7])"
assert (secrets.compare_digest('abc', 'abc'), secrets.compare_digest(b'abc', b'abd')) == (True, False), "secrets.compare_digest('abc', 'abc'), secrets.compare_digest(b'abc', b'abd')"
assert (secrets.DEFAULT_ENTROPY) == 32, 'secrets.DEFAULT_ENTROPY'
assert (secrets.SystemRandom().random() < 1) == True, 'secrets.SystemRandom().random() < 1'
try:
    secrets.randbelow(0)
    assert False, 'secrets.randbelow(0)'
except ValueError:
    pass
try:
    secrets.choice([])
    assert False, 'secrets.choice([])'
except IndexError:
    pass
try:
    secrets.compare_digest('a', b'a')
    assert False, "secrets.compare_digest('a', b'a')"
except TypeError:
    pass
