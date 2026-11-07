"""urllib.parse: CPython 3.13's URL splitting, joining and quoting. No network is involved.

The parsing, joining and quoting rules are CPython's. Two things differ in shape:

- ``SplitResult``, ``ParseResult`` and ``DefragResult`` are not tuple subclasses (classes
  here do not inherit). They iterate, index, unpack, compare equal to the matching tuple
  and have ``_replace``, ``_asdict``, ``_fields`` and ``geturl``.
- ``username``, ``password``, ``hostname`` and ``port`` are computed when the result is
  made, because there are no properties: a port that is not a number in 0-65535 reads as
  ``None`` rather than raising ``ValueError`` on access.

Only ``str`` URLs are parsed; ``quote``, ``quote_from_bytes`` and ``unquote_to_bytes``
take bytes as CPython's do.
"""

import re

__all__ = ["urlparse", "urlunparse", "urljoin", "urldefrag",
           "urlsplit", "urlunsplit", "urlencode", "parse_qs",
           "parse_qsl", "quote", "quote_plus", "quote_from_bytes",
           "unquote", "unquote_plus", "unquote_to_bytes",
           "DefragResult", "ParseResult", "SplitResult"]

uses_relative = ['', 'ftp', 'http', 'gopher', 'nntp', 'imap',
                 'wais', 'file', 'https', 'shttp', 'mms',
                 'prospero', 'rtsp', 'rtsps', 'rtspu', 'sftp',
                 'svn', 'svn+ssh', 'ws', 'wss']

uses_netloc = ['', 'ftp', 'http', 'gopher', 'nntp', 'telnet',
               'imap', 'wais', 'file', 'mms', 'https', 'shttp',
               'snews', 'prospero', 'rtsp', 'rtsps', 'rtspu', 'rsync',
               'svn', 'svn+ssh', 'sftp', 'nfs', 'git', 'git+ssh',
               'ws', 'wss', 'itms-services']

uses_params = ['', 'ftp', 'hdl', 'prospero', 'http', 'imap',
               'https', 'shttp', 'rtsp', 'rtsps', 'rtspu', 'sip',
               'sips', 'mms', 'sftp', 'tel']

non_hierarchical = ['gopher', 'hdl', 'mailto', 'news',
                    'telnet', 'wais', 'imap', 'snews', 'sip', 'sips']

uses_query = ['', 'http', 'wais', 'imap', 'https', 'shttp', 'mms',
              'gopher', 'rtsp', 'rtsps', 'rtspu', 'sip', 'sips']

uses_fragment = ['', 'ftp', 'hdl', 'http', 'gopher', 'news',
                 'nntp', 'wais', 'https', 'shttp', 'snews',
                 'file', 'prospero']

scheme_chars = ('abcdefghijklmnopqrstuvwxyz'
                'ABCDEFGHIJKLMNOPQRSTUVWXYZ'
                '0123456789'
                '+-.')

_WHATWG_C0_CONTROL_OR_SPACE = ''.join([chr(i) for i in range(0, 0x20 + 1)])

_UNSAFE_URL_BYTES_TO_REMOVE = ['\t', '\r', '\n']


# ---- the result types ----

def _userinfo(netloc):
    userinfo, have_info, hostinfo = netloc.rpartition('@')
    if have_info:
        username, have_password, password = userinfo.partition(':')
        if not have_password:
            password = None
    else:
        username = password = None
    return username, password


def _hostinfo(netloc):
    _, _, hostinfo = netloc.rpartition('@')
    _, have_open_br, bracketed = hostinfo.partition('[')
    if have_open_br:
        hostname, _, port = bracketed.partition(']')
        _, _, port = port.partition(':')
    else:
        hostname, _, port = hostinfo.partition(':')
    if not port:
        port = None
    return hostname, port


def _set_netloc_attributes(self, netloc):
    self.username, self.password = _userinfo(netloc)
    hostname, port = _hostinfo(netloc)
    if not hostname:
        self.hostname = None
    else:
        hostname, percent, zone = hostname.partition('%')
        self.hostname = hostname.lower() + percent + zone
    if port is not None:
        if port.isdigit() and port.isascii() and 0 <= int(port) <= 65535:
            port = int(port)
        else:
            port = None
    self.port = port


def _values(self):
    return tuple([getattr(self, name) for name in self._fields])


def _iter(self):
    return iter(_values(self))


def _getitem(self, index):
    return _values(self)[index]


def _len(self):
    return len(self._fields)


def _eq(self, other):
    if isinstance(other, tuple):
        return _values(self) == other
    if hasattr(other, '_fields') and hasattr(other, '_asdict'):
        return _values(self) == tuple(other)
    return NotImplemented


def _ne(self, other):
    result = _eq(self, other)
    if result is NotImplemented:
        return result
    return not result


def _hash(self):
    return hash(_values(self))


def _repr(self):
    parts = ['%s=%r' % (name, getattr(self, name)) for name in self._fields]
    return '%s(%s)' % (self._name, ', '.join(parts))


def _asdict(self):
    return dict(zip(self._fields, _values(self)))


def _replace(self, **changes):
    values = []
    for name in self._fields:
        if name in changes:
            values.append(changes.pop(name))
        else:
            values.append(getattr(self, name))
    if changes:
        raise ValueError('Got unexpected field names: %r' % (list(changes),))
    return type(self)(*values)


def _encode(self, encoding='ascii', errors='strict'):
    return tuple([x.encode(encoding, errors) for x in _values(self)])


class SplitResult:
    """SplitResult(scheme, netloc, path, query, fragment)."""

    _fields = ('scheme', 'netloc', 'path', 'query', 'fragment')
    _name = 'SplitResult'

    def __init__(self, scheme, netloc, path, query, fragment):
        self.scheme = scheme
        self.netloc = netloc
        self.path = path
        self.query = query
        self.fragment = fragment
        _set_netloc_attributes(self, netloc)

    def geturl(self):
        return urlunsplit(self)

    __iter__ = _iter
    __getitem__ = _getitem
    __len__ = _len
    __eq__ = _eq
    __ne__ = _ne
    __hash__ = _hash
    __repr__ = _repr
    _asdict = _asdict
    _replace = _replace
    encode = _encode


class ParseResult:
    """ParseResult(scheme, netloc, path, params, query, fragment)."""

    _fields = ('scheme', 'netloc', 'path', 'params', 'query', 'fragment')
    _name = 'ParseResult'

    def __init__(self, scheme, netloc, path, params, query, fragment):
        self.scheme = scheme
        self.netloc = netloc
        self.path = path
        self.params = params
        self.query = query
        self.fragment = fragment
        _set_netloc_attributes(self, netloc)

    def geturl(self):
        return urlunparse(self)

    __iter__ = _iter
    __getitem__ = _getitem
    __len__ = _len
    __eq__ = _eq
    __ne__ = _ne
    __hash__ = _hash
    __repr__ = _repr
    _asdict = _asdict
    _replace = _replace
    encode = _encode


class DefragResult:
    """DefragResult(url, fragment)."""

    _fields = ('url', 'fragment')
    _name = 'DefragResult'

    def __init__(self, url, fragment):
        self.url = url
        self.fragment = fragment

    def geturl(self):
        if self.fragment:
            return self.url + '#' + self.fragment
        return self.url

    __iter__ = _iter
    __getitem__ = _getitem
    __len__ = _len
    __eq__ = _eq
    __ne__ = _ne
    __hash__ = _hash
    __repr__ = _repr
    _asdict = _asdict
    _replace = _replace
    encode = _encode


# ---- splitting and joining ----

def _require_str(*args):
    for arg in args:
        if not isinstance(arg, str):
            raise TypeError("Cannot mix str and non-str arguments")


def urlparse(url, scheme='', allow_fragments=True):
    """Parse a URL into 6 components: <scheme>://<netloc>/<path>;<params>?<query>#<fragment>"""
    _require_str(url, scheme)
    scheme, netloc, url, query, fragment = urlsplit(url, scheme, allow_fragments)
    if scheme in uses_params and ';' in url:
        url, params = _splitparams(url)
    else:
        params = ''
    return ParseResult(scheme, netloc, url, params, query, fragment)


def _splitparams(url):
    if '/' in url:
        i = url.find(';', url.rfind('/'))
        if i < 0:
            return url, ''
    else:
        i = url.find(';')
    return url[:i], url[i + 1:]


def _splitnetloc(url, start=0):
    delim = len(url)
    for c in '/?#':
        wdelim = url.find(c, start)
        if wdelim >= 0:
            delim = min(delim, wdelim)
    return url[start:delim], url[delim:]


def _checknetloc(netloc):
    if not netloc or netloc.isascii():
        return
    import unicodedata
    n = netloc.replace('@', '')
    n = n.replace(':', '')
    n = n.replace('#', '')
    n = n.replace('?', '')
    netloc2 = unicodedata.normalize('NFKC', n)
    if n == netloc2:
        return
    for c in '/?#@:':
        if c in netloc2:
            raise ValueError("netloc '" + netloc + "' contains invalid " +
                             "characters under NFKC normalization")


_ipv6_group = re.compile(r'\A[0-9a-fA-F]{1,4}\Z')
_ipv4 = re.compile(r'\A(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])(\.(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])){3}\Z')


def _is_ipv6(text):
    address = text.partition('%')[0]
    if '::' in address:
        if address.count('::') > 1:
            return False
        head, _, tail = address.partition('::')
        groups = (head.split(':') if head else []) + (tail.split(':') if tail else [])
        limit = 7
    else:
        groups = address.split(':')
        limit = 8
    if groups and '.' in groups[-1]:
        if not _ipv4.match(groups[-1]):
            return False
        groups = groups[:-1]
        limit -= 1
    if '::' in address:
        if len(groups) > limit:
            return False
    elif len(groups) != limit:
        return False
    for group in groups:
        if not _ipv6_group.match(group):
            return False
    return True


def _check_bracketed_netloc(netloc):
    hostname_and_port = netloc.rpartition('@')[2]
    before_bracket, have_open_br, bracketed = hostname_and_port.partition('[')
    if have_open_br:
        if before_bracket:
            raise ValueError("Invalid IPv6 URL")
        hostname, _, port = bracketed.partition(']')
        if port and not port.startswith(":"):
            raise ValueError("Invalid IPv6 URL")
    else:
        hostname, _, port = hostname_and_port.partition(':')
    _check_bracketed_host(hostname)


def _check_bracketed_host(hostname):
    if hostname.startswith(('v', 'V')):
        if not re.match(r"\A[vV][a-fA-F0-9]+\..+\Z", hostname):
            raise ValueError("IPvFuture address is invalid")
    elif _ipv4.match(hostname):
        raise ValueError("An IPv4 address cannot be in brackets")
    elif not _is_ipv6(hostname):
        raise ValueError("%r does not appear to be an IPv4 or IPv6 address" % (hostname,))


def urlsplit(url, scheme='', allow_fragments=True):
    """Parse a URL into 5 components: <scheme>://<netloc>/<path>?<query>#<fragment>"""
    _require_str(url, scheme)
    url = url.lstrip(_WHATWG_C0_CONTROL_OR_SPACE)
    scheme = scheme.strip(_WHATWG_C0_CONTROL_OR_SPACE)

    for b in _UNSAFE_URL_BYTES_TO_REMOVE:
        url = url.replace(b, "")
        scheme = scheme.replace(b, "")

    allow_fragments = bool(allow_fragments)
    netloc = query = fragment = ''
    i = url.find(':')
    if i > 0 and url[0].isascii() and url[0].isalpha():
        valid = True
        for c in url[:i]:
            if c not in scheme_chars:
                valid = False
                break
        if valid:
            scheme, url = url[:i].lower(), url[i + 1:]
    if url[:2] == '//':
        netloc, url = _splitnetloc(url, 2)
        if (('[' in netloc and ']' not in netloc) or
                (']' in netloc and '[' not in netloc)):
            raise ValueError("Invalid IPv6 URL")
        if '[' in netloc and ']' in netloc:
            _check_bracketed_netloc(netloc)
    if allow_fragments and '#' in url:
        url, fragment = url.split('#', 1)
    if '?' in url:
        url, query = url.split('?', 1)
    _checknetloc(netloc)
    return SplitResult(scheme, netloc, url, query, fragment)


def urlunparse(components):
    """Put a parsed URL back together again."""
    scheme, netloc, url, params, query, fragment = components
    if params:
        url = "%s;%s" % (url, params)
    return urlunsplit((scheme, netloc, url, query, fragment))


def urlunsplit(components):
    """Combine the elements of a tuple as returned by urlsplit() into a complete URL."""
    scheme, netloc, url, query, fragment = components
    if netloc:
        if url and url[:1] != '/':
            url = '/' + url
        url = '//' + netloc + url
    elif url[:2] == '//':
        url = '//' + url
    elif scheme and scheme in uses_netloc and (not url or url[:1] == '/'):
        url = '//' + url
    if scheme:
        url = scheme + ':' + url
    if query:
        url = url + '?' + query
    if fragment:
        url = url + '#' + fragment
    return url


def urljoin(base, url, allow_fragments=True):
    """Join a base URL and a possibly relative URL to form an absolute interpretation of the latter."""
    if not base:
        return url
    if not url:
        return base

    _require_str(base, url)
    bscheme, bnetloc, bpath, bparams, bquery, bfragment = \
        urlparse(base, '', allow_fragments)
    scheme, netloc, path, params, query, fragment = \
        urlparse(url, bscheme, allow_fragments)

    if scheme != bscheme or scheme not in uses_relative:
        return url
    if scheme in uses_netloc:
        if netloc:
            return urlunparse((scheme, netloc, path, params, query, fragment))
        netloc = bnetloc

    if not path and not params:
        path = bpath
        params = bparams
        if not query:
            query = bquery
        return urlunparse((scheme, netloc, path, params, query, fragment))

    base_parts = bpath.split('/')
    if base_parts[-1] != '':
        base_parts.pop()

    if path[:1] == '/':
        segments = path.split('/')
    else:
        segments = base_parts + path.split('/')
        # Drop the empty segments that would double a slash on re-joining.
        segments = segments[:1] + [s for s in segments[1:-1] if s] + segments[-1:] \
            if len(segments) > 1 else segments

    resolved_path = []

    for seg in segments:
        if seg == '..':
            if resolved_path:
                resolved_path.pop()
        elif seg == '.':
            continue
        else:
            resolved_path.append(seg)

    if segments[-1] in ('.', '..'):
        resolved_path.append('')

    return urlunparse((scheme, netloc, '/'.join(resolved_path) or '/',
                       params, query, fragment))


def urldefrag(url):
    """Removes any existing fragment from URL, returning (url, fragment)."""
    _require_str(url)
    if '#' in url:
        s, n, p, a, q, frag = urlparse(url)
        defrag = urlunparse((s, n, p, a, q, ''))
    else:
        frag = ''
        defrag = url
    return DefragResult(defrag, frag)


# ---- quoting ----

_hexdig = '0123456789ABCDEFabcdef'


def unquote_to_bytes(string):
    """unquote_to_bytes('abc%20def') -> b'abc def'."""
    if not string:
        string.split
        return b''
    if isinstance(string, str):
        string = string.encode('utf-8')
    bits = string.split(b'%')
    if len(bits) == 1:
        return string
    res = [bits[0]]
    for item in bits[1:]:
        code = item[:2]
        if len(code) == 2 and chr(code[0]) in _hexdig and chr(code[1]) in _hexdig:
            res.append(bytes([int(code.decode('ascii'), 16)]))
            res.append(item[2:])
        else:
            res.append(b'%')
            res.append(item)
    return b''.join(res)


_asciire = re.compile('([\x00-\x7f]+)')


def unquote(string, encoding='utf-8', errors='replace'):
    """Replace %xx escapes by their single-character equivalent."""
    if isinstance(string, bytes):
        return unquote_to_bytes(string).decode(encoding, errors)
    if '%' not in string:
        string.split
        return string
    if encoding is None:
        encoding = 'utf-8'
    if errors is None:
        errors = 'replace'
    parts = []
    previous_match_end = 0
    for ascii_match in _asciire.finditer(string):
        start, end = ascii_match.span()
        parts.append(string[previous_match_end:start])
        parts.append(unquote_to_bytes(ascii_match.group(1)).decode(encoding, errors))
        previous_match_end = end
    parts.append(string[previous_match_end:])
    return ''.join(parts)


def unquote_plus(string, encoding='utf-8', errors='replace'):
    """Like unquote(), but also replace plus signs by spaces."""
    string = string.replace('+', ' ')
    return unquote(string, encoding, errors)


def parse_qs(qs, keep_blank_values=False, strict_parsing=False,
             encoding='utf-8', errors='replace', max_num_fields=None, separator='&'):
    """Parse a query string into a dict of lists."""
    parsed_result = {}
    pairs = parse_qsl(qs, keep_blank_values, strict_parsing,
                      encoding=encoding, errors=errors,
                      max_num_fields=max_num_fields, separator=separator)
    for name, value in pairs:
        if name in parsed_result:
            parsed_result[name].append(value)
        else:
            parsed_result[name] = [value]
    return parsed_result


def parse_qsl(qs, keep_blank_values=False, strict_parsing=False,
              encoding='utf-8', errors='replace', max_num_fields=None, separator='&'):
    """Parse a query string into a list of (name, value) pairs."""
    if not separator or not isinstance(separator, (str, bytes)):
        raise ValueError("Separator must be of type string or bytes.")
    if isinstance(qs, str):
        if not isinstance(separator, str):
            separator = str(separator, 'ascii')
        eq = '='

        def _unquote(s):
            return unquote_plus(s, encoding=encoding, errors=errors)
    else:
        if not qs:
            return []
        qs = bytes(qs)
        if isinstance(separator, str):
            separator = bytes(separator, 'ascii')
        eq = b'='

        def _unquote(s):
            return unquote_to_bytes(s.replace(b'+', b' '))

    if not qs:
        return []

    if max_num_fields is not None:
        num_fields = 1 + qs.count(separator)
        if max_num_fields < num_fields:
            raise ValueError('Max number of fields exceeded')

    r = []
    for name_value in qs.split(separator):
        if name_value or strict_parsing:
            name, has_eq, value = name_value.partition(eq)
            if not has_eq and strict_parsing:
                raise ValueError("bad query field: %r" % (name_value,))
            if value or keep_blank_values:
                name = _unquote(name)
                value = _unquote(value)
                r.append((name, value))
    return r


_ALWAYS_SAFE = frozenset(b'ABCDEFGHIJKLMNOPQRSTUVWXYZ'
                         b'abcdefghijklmnopqrstuvwxyz'
                         b'0123456789'
                         b'_.-~')


def quote(string, safe='/', encoding=None, errors=None):
    """quote('abc def') -> 'abc%20def'"""
    if isinstance(string, str):
        if not string:
            return string
        if encoding is None:
            encoding = 'utf-8'
        if errors is None:
            errors = 'strict'
        string = string.encode(encoding, errors)
    else:
        if encoding is not None:
            raise TypeError("quote() doesn't support 'encoding' for bytes")
        if errors is not None:
            raise TypeError("quote() doesn't support 'errors' for bytes")
    return quote_from_bytes(string, safe)


def quote_plus(string, safe='', encoding=None, errors=None):
    """Like quote(), but also replace ' ' with '+'."""
    if ((isinstance(string, str) and ' ' not in string) or
            (isinstance(string, bytes) and b' ' not in string)):
        return quote(string, safe, encoding, errors)
    if isinstance(safe, str):
        space = ' '
    else:
        space = b' '
    string = quote(string, safe + space, encoding, errors)
    return string.replace(' ', '+')


def quote_from_bytes(bs, safe='/'):
    """Like quote(), but accepts a bytes object rather than a str."""
    if not isinstance(bs, bytes):
        raise TypeError("quote_from_bytes() expected bytes")
    if not bs:
        return ''
    if isinstance(safe, str):
        safe = [ord(c) for c in safe if ord(c) < 128]
    else:
        safe = [c for c in safe if c < 128]
    allowed = _ALWAYS_SAFE.union(safe)
    return ''.join([chr(b) if b in allowed else '%{:02X}'.format(b) for b in bs])


def urlencode(query, doseq=False, safe='', encoding=None, errors=None,
              quote_via=quote_plus):
    """Encode a dict or sequence of two-element tuples into a URL query string."""
    if hasattr(query, "items"):
        query = query.items()
    else:
        try:
            if len(query) and not isinstance(query[0], tuple):
                raise TypeError
        except TypeError:
            raise TypeError("not a valid non-string sequence "
                            "or mapping object") from None

    l = []
    if not doseq:
        for k, v in query:
            if isinstance(k, bytes):
                k = quote_via(k, safe)
            else:
                k = quote_via(str(k), safe, encoding, errors)

            if isinstance(v, bytes):
                v = quote_via(v, safe)
            else:
                v = quote_via(str(v), safe, encoding, errors)
            l.append(k + '=' + v)
    else:
        for k, v in query:
            if isinstance(k, bytes):
                k = quote_via(k, safe)
            else:
                k = quote_via(str(k), safe, encoding, errors)

            if isinstance(v, bytes):
                v = quote_via(v, safe)
                l.append(k + '=' + v)
            elif isinstance(v, str):
                v = quote_via(v, safe, encoding, errors)
                l.append(k + '=' + v)
            else:
                try:
                    len(v)
                except TypeError:
                    v = quote_via(str(v), safe, encoding, errors)
                    l.append(k + '=' + v)
                else:
                    for elt in v:
                        if isinstance(elt, bytes):
                            elt = quote_via(elt, safe)
                        else:
                            elt = quote_via(str(elt), safe, encoding, errors)
                        l.append(k + '=' + elt)
    return '&'.join(l)
