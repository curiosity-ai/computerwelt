# Extension: dir, globals, locals, vars, eval, exec, compile. Upstream Monty has
# none of them; here they compile with the same compiler and run on the same
# machine under the same limits, so they reach nothing the program could not.
assert eval('1 + 2') == 3
assert eval('  x * 2', {'x': 21}) == 42
ns = {}
exec('z = 5\ndef f(): return z * 2', ns)
assert ns['f']() == 10 and ns['z'] == 5
code = compile('a + 1', '<s>', 'eval')
assert eval(code, {'a': 1}) == 2
def fn():
    q = 4
    return eval('q * 2'), sorted(locals())
assert fn() == (8, ['q'])
assert 'fn' in globals() and 'fn' in dir()
loc = {}
exec('y = len(data)', {'data': [1, 2]}, loc)
assert loc == {'y': 2}
try:
    eval('1 +')
except SyntaxError:
    pass
else:
    assert False
class C:
    k = 1
    def m(self): pass
c = C()
c.v = 2
assert {'k', 'm', 'v', '__init__'} <= set(dir(c)) and vars(c) == {'v': 2}
assert 'append' in dir([]) and 'upper' in dir('') and 'keys' in dir({})
import math
assert 'sqrt' in dir(math)
s = 'exec(s)'
try:
    exec(s)
except RecursionError:
    pass
else:
    assert False
