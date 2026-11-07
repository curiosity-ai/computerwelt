# Extension: `urllib.parse` — not in upstream Monty. No network is involved.
# CPython 3.13's splitting, joining and quoting rules. The result objects iterate, index and
# compare equal to tuples, but are not tuple subclasses (see todo.md). Expected values
# produced by CPython 3.13.
import urllib.parse
from urllib.parse import (urlparse, urlunparse, urlsplit, urlunsplit, urljoin, urldefrag,
                          urlencode, quote, quote_plus, unquote, unquote_plus,
                          parse_qs, parse_qsl, quote_from_bytes, unquote_to_bytes)

# === urlparse and urlsplit ===
u = urlparse('https://user:pw@Example.COM:8443/a/b;p=1?x=1&y=2#frag')
assert (tuple(u)) == ('https', 'user:pw@Example.COM:8443', '/a/b', 'p=1', 'x=1&y=2', 'frag'), 'tuple(u)'
assert repr(u) == "ParseResult(scheme='https', netloc='user:pw@Example.COM:8443', path='/a/b', params='p=1', query='x=1&y=2', fragment='frag')", 'u'
assert ((u.scheme, u.netloc, u.path, u.params, u.query, u.fragment)) == ('https', 'user:pw@Example.COM:8443', '/a/b', 'p=1', 'x=1&y=2', 'frag'), '(u.scheme, u.netloc, u.path, u.params, u.query, u.fragment)'
assert ((u.username, u.password, u.hostname, u.port)) == ('user', 'pw', 'example.com', 8443), '(u.username, u.password, u.hostname, u.port)'
assert (u.geturl()) == 'https://user:pw@Example.COM:8443/a/b;p=1?x=1&y=2#frag', 'u.geturl()'
assert (u == ('https', 'user:pw@Example.COM:8443', '/a/b', 'p=1', 'x=1&y=2', 'frag')) == True, "u == ('https', 'user:pw@Example.COM:8443', '/a/b', 'p=1', 'x=1&y=2', 'frag')"
assert (u[1]) == 'user:pw@Example.COM:8443', 'u[1]'
assert (len(u)) == 6, 'len(u)'
assert (u._replace(fragment='', query='z=3').geturl()) == 'https://user:pw@Example.COM:8443/a/b;p=1?z=3', "u._replace(fragment='', query='z=3').geturl()"
assert (u._asdict()) == {'scheme': 'https', 'netloc': 'user:pw@Example.COM:8443', 'path': '/a/b', 'params': 'p=1', 'query': 'x=1&y=2', 'fragment': 'frag'}, 'u._asdict()'
scheme, netloc, path, params, query, fragment = u
assert ((scheme, fragment)) == ('https', 'frag'), '(scheme, fragment)'
s = urlsplit('http://[::1]:80/index.html?q#top')
assert (tuple(s)) == ('http', '[::1]:80', '/index.html', 'q', 'top'), 'tuple(s)'
assert repr(s) == "SplitResult(scheme='http', netloc='[::1]:80', path='/index.html', query='q', fragment='top')", 's'
assert ((s.hostname, s.port)) == ('::1', 80), '(s.hostname, s.port)'
assert (tuple(urlsplit('www.example.com/path'))) == ('', '', 'www.example.com/path', '', ''), "tuple(urlsplit('www.example.com/path'))"
assert (tuple(urlsplit('//www.example.com/path'))) == ('', 'www.example.com', '/path', '', ''), "tuple(urlsplit('//www.example.com/path'))"
assert (tuple(urlsplit('mailto:someone@example.com'))) == ('mailto', '', 'someone@example.com', '', ''), "tuple(urlsplit('mailto:someone@example.com'))"
assert (tuple(urlsplit('file:///etc/hosts'))) == ('file', '', '/etc/hosts', '', ''), "tuple(urlsplit('file:///etc/hosts'))"
assert (tuple(urlsplit('path/only?x#y', scheme='https'))) == ('https', '', 'path/only', 'x', 'y'), "tuple(urlsplit('path/only?x#y', scheme='https'))"
assert (tuple(urlsplit('http://h/p#f', allow_fragments=False))) == ('http', 'h', '/p#f', '', ''), "tuple(urlsplit('http://h/p#f', allow_fragments=False))"
assert (tuple(urlparse('http://h/a;b/c;d'))) == ('http', 'h', '/a;b/c', 'd', '', ''), "tuple(urlparse('http://h/a;b/c;d'))"
assert (tuple(urlparse('  http://example.com/\tpath\n'))) == ('http', 'example.com', '/path', '', '', ''), "tuple(urlparse('  http://example.com/\\tpath\\n'))"
assert (urlparse('http://example.com').port) == None, "urlparse('http://example.com').port"
assert (urlparse('http://example.com').username) == None, "urlparse('http://example.com').username"
assert (urlparse('http://ExAmPle.com/').hostname) == 'example.com', "urlparse('http://ExAmPle.com/').hostname"
assert (urlparse('HTTP://x').scheme) == 'http', "urlparse('HTTP://x').scheme"
try:
    urlsplit('http://[::1/')
    assert False, "urlsplit('http://[::1/')"
except ValueError:
    pass
try:
    urlsplit('http://[1.2.3.4]/')
    assert False, "urlsplit('http://[1.2.3.4]/')"
except ValueError:
    pass

# === unsplit, unparse, defrag ===
assert (urlunsplit(('https', 'example.com', 'a b', 'q=1', 'f'))) == 'https://example.com/a b?q=1#f', "urlunsplit(('https', 'example.com', 'a b', 'q=1', 'f'))"
assert (urlunsplit(('http', '', '/x', '', ''))) == 'http:///x', "urlunsplit(('http', '', '/x', '', ''))"
assert (urlunparse(('http', 'h', '/p', 'params', 'q', 'f'))) == 'http://h/p;params?q#f', "urlunparse(('http', 'h', '/p', 'params', 'q', 'f'))"
assert (urlunparse(urlparse('http://h/p?q'))) == 'http://h/p?q', "urlunparse(urlparse('http://h/p?q'))"
assert (tuple(urldefrag('http://h/p?q#frag'))) == ('http://h/p?q', 'frag'), "tuple(urldefrag('http://h/p?q#frag'))"
assert (tuple(urldefrag('http://h/p'))) == ('http://h/p', ''), "tuple(urldefrag('http://h/p'))"
assert (urldefrag('http://h/p#f').geturl()) == 'http://h/p#f', "urldefrag('http://h/p#f').geturl()"

# === urljoin (the RFC 3986 examples) ===
base = 'http://a/b/c/d;p?q'
for rel in ['g:h', 'g', './g', 'g/', '/g', '//g', '?y', 'g?y', '#s', 'g#s', 'g?y#s', ';x',
            'g;x', '', '.', './', '..', '../', '../g', '../..', '../../', '../../g',
            '../../../g', '/./g', '/../g', 'g.', '.g', 'g..', '..g', './../g', './g/.',
            'g/./h', 'g/../h', 'g;x=1/./y', 'g;x=1/../y']:
    joined = urljoin(base, rel)
assert ([urljoin(base, rel) for rel in ['g:h', 'g', './g', 'g/', '/g', '//g', '?y', 'g?y', '#s']]) == ['g:h', 'http://a/b/c/g', 'http://a/b/c/g', 'http://a/b/c/g/', 'http://a/g', 'http://g', 'http://a/b/c/d;p?y', 'http://a/b/c/g?y', 'http://a/b/c/d;p?q#s'], "[urljoin(base, rel) for rel in ['g:h', 'g', './g', 'g/', '/g', '//g', '?y', 'g?y', '#s']]"
assert ([urljoin(base, rel) for rel in ['g#s', 'g?y#s', ';x', 'g;x', '', '.', './', '..', '../']]) == ['http://a/b/c/g#s', 'http://a/b/c/g?y#s', 'http://a/b/c/;x', 'http://a/b/c/g;x', 'http://a/b/c/d;p?q', 'http://a/b/c/', 'http://a/b/c/', 'http://a/b/', 'http://a/b/'], "[urljoin(base, rel) for rel in ['g#s', 'g?y#s', ';x', 'g;x', '', '.', './', '..', '../']]"
assert ([urljoin(base, rel) for rel in ['../g', '../..', '../../', '../../g', '../../../g', '/./g']]) == ['http://a/b/g', 'http://a/', 'http://a/', 'http://a/g', 'http://a/g', 'http://a/g'], "[urljoin(base, rel) for rel in ['../g', '../..', '../../', '../../g', '../../../g', '/./g']]"
assert ([urljoin(base, rel) for rel in ['/../g', 'g.', '.g', 'g..', '..g', './../g', './g/.']]) == ['http://a/g', 'http://a/b/c/g.', 'http://a/b/c/.g', 'http://a/b/c/g..', 'http://a/b/c/..g', 'http://a/b/g', 'http://a/b/c/g/'], "[urljoin(base, rel) for rel in ['/../g', 'g.', '.g', 'g..', '..g', './../g', './g/.']]"
assert ([urljoin(base, rel) for rel in ['g/./h', 'g/../h', 'g;x=1/./y', 'g;x=1/../y']]) == ['http://a/b/c/g/h', 'http://a/b/c/h', 'http://a/b/c/g;x=1/y', 'http://a/b/c/y'], "[urljoin(base, rel) for rel in ['g/./h', 'g/../h', 'g;x=1/./y', 'g;x=1/../y']]"
assert (urljoin('https://example.com/docs/', 'api/v1')) == 'https://example.com/docs/api/v1', "urljoin('https://example.com/docs/', 'api/v1')"
assert (urljoin('https://example.com/docs', 'api/v1')) == 'https://example.com/api/v1', "urljoin('https://example.com/docs', 'api/v1')"
assert (urljoin('https://example.com/a//b/', '../c')) == 'https://example.com/a/c', "urljoin('https://example.com/a//b/', '../c')"
assert (urljoin('http://a/b', 'https://other/x')) == 'https://other/x', "urljoin('http://a/b', 'https://other/x')"
assert (urljoin('', 'x')) == 'x', "urljoin('', 'x')"

# === quoting ===
assert (quote('/path with spaces/ü?&=')) == '/path%20with%20spaces/%C3%BC%3F%26%3D', "quote('/path with spaces/ü?&=')"
assert (quote('/path with spaces/', safe='')) == '%2Fpath%20with%20spaces%2F', "quote('/path with spaces/', safe='')"
assert (quote('a+b c', safe='+')) == 'a+b%20c', "quote('a+b c', safe='+')"
assert (quote(b'\x00\xff abc')) == '%00%FF%20abc', "quote(b'\\x00\\xff abc')"
assert (quote_plus('a b&c=d/e')) == 'a+b%26c%3Dd%2Fe', "quote_plus('a b&c=d/e')"
assert (quote_plus('a b', safe='/')) == 'a+b', "quote_plus('a b', safe='/')"
assert (quote('~._-')) == '~._-', "quote('~._-')"
assert (quote('')) == '', "quote('')"
assert (quote_from_bytes(b'abc def?')) == 'abc%20def%3F', "quote_from_bytes(b'abc def?')"
assert (unquote('%2Fpath%20with%20spaces%2F%C3%BC')) == '/path with spaces/ü', "unquote('%2Fpath%20with%20spaces%2F%C3%BC')"
assert (unquote('%e2%82%ac and %zz and %')) == '€ and %zz and %', "unquote('%e2%82%ac and %zz and %')"
assert (unquote('%ff%fe')) == '��', "unquote('%ff%fe')"
assert (unquote(b'abc%20def')) == 'abc def', "unquote(b'abc%20def')"
assert (unquote_plus('a+b%2Bc')) == 'a b+c', "unquote_plus('a+b%2Bc')"
assert (unquote_to_bytes('abc%20d%C3%A9f')) == b'abc d\xc3\xa9f', "unquote_to_bytes('abc%20d%C3%A9f')"
assert (unquote_to_bytes('%G0%4')) == b'%G0%4', "unquote_to_bytes('%G0%4')"
assert (unquote('no escapes')) == 'no escapes', "unquote('no escapes')"

# === query strings ===
assert (urlencode({'q': 'hello world', 'lang': 'en', 'n': 10})) == 'q=hello+world&lang=en&n=10', "urlencode({'q': 'hello world', 'lang': 'en', 'n': 10})"
assert (urlencode([('a', 1), ('a', 2), ('b', 'x&y')])) == 'a=1&a=2&b=x%26y', "urlencode([('a', 1), ('a', 2), ('b', 'x&y')])"
assert (urlencode({'tags': ['a', 'b c'], 'one': 'x'}, doseq=True)) == 'tags=a&tags=b+c&one=x', "urlencode({'tags': ['a', 'b c'], 'one': 'x'}, doseq=True)"
assert (urlencode({'tags': ['a', 'b']})) == 'tags=%5B%27a%27%2C+%27b%27%5D', "urlencode({'tags': ['a', 'b']})"
assert (urlencode({'path': '/a b'}, safe='/')) == 'path=/a+b', "urlencode({'path': '/a b'}, safe='/')"
assert (urlencode({'path': '/a b'}, quote_via=quote)) == 'path=%2Fa%20b', "urlencode({'path': '/a b'}, quote_via=quote)"
assert (urlencode({b'k': b'v w'})) == 'k=v+w', "urlencode({b'k': b'v w'})"
assert (urlencode({})) == '', 'urlencode({})'
try:
    urlencode('abc')
    assert False, "urlencode('abc')"
except TypeError:
    pass
assert (parse_qs('a=1&b=2&a=3&c=&d=hello+world%21')) == {'a': ['1', '3'], 'b': ['2'], 'd': ['hello world!']}, "parse_qs('a=1&b=2&a=3&c=&d=hello+world%21')"
assert (parse_qs('a=1&b=&c', keep_blank_values=True)) == {'a': ['1'], 'b': [''], 'c': ['']}, "parse_qs('a=1&b=&c', keep_blank_values=True)"
assert (parse_qsl('a=1&b=2&a=3&empty=')) == [('a', '1'), ('b', '2'), ('a', '3')], "parse_qsl('a=1&b=2&a=3&empty=')"
assert (parse_qsl('a=1;b=2', separator=';')) == [('a', '1'), ('b', '2')], "parse_qsl('a=1;b=2', separator=';')"
assert (parse_qsl('x=%E2%82%AC&y=a+b')) == [('x', '€'), ('y', 'a b')], "parse_qsl('x=%E2%82%AC&y=a+b')"
assert (parse_qsl(b'a=1&b=x+y')) == [(b'a', b'1'), (b'b', b'x y')], "parse_qsl(b'a=1&b=x+y')"
assert (parse_qsl('')) == [], "parse_qsl('')"
try:
    parse_qsl('a=1&b', strict_parsing=True)
    assert False, "parse_qsl('a=1&b', strict_parsing=True)"
except ValueError:
    pass
try:
    parse_qsl('a=1&b=2&c=3', max_num_fields=2)
    assert False, "parse_qsl('a=1&b=2&c=3', max_num_fields=2)"
except ValueError:
    pass
assert (urllib.parse.parse_qs(urlparse('http://h/?a=1&b=2').query)) == {'a': ['1'], 'b': ['2']}, "urllib.parse.parse_qs(urlparse('http://h/?a=1&b=2').query)"
assert (urllib.parse.urlparse('http://h:99').port) == 99, "urllib.parse.urlparse('http://h:99').port"
