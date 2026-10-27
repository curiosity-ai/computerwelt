"""string: constants and the Template class."""

whitespace = ' \t\n\r\x0b\x0c'
ascii_lowercase = 'abcdefghijklmnopqrstuvwxyz'
ascii_uppercase = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ'
ascii_letters = ascii_lowercase + ascii_uppercase
digits = '0123456789'
hexdigits = digits + 'abcdef' + 'ABCDEF'
octdigits = '01234567'
punctuation = r"""!"#$%&'()*+,-./:;<=>?@[\]^_`{|}~"""
printable = digits + ascii_letters + punctuation + whitespace


def capwords(s, sep=None):
    """Split on sep, capitalize each word, and rejoin with sep (a space by default)."""
    return (sep or ' ').join([word.capitalize() for word in s.split(sep)])


_IDENT_START = ascii_letters + '_'
_IDENT_CHARS = ascii_letters + digits + '_'
_MISSING = object()


class Template:
    """A string class for supporting $-substitutions."""

    delimiter = '$'

    def __init__(self, template):
        self.template = template

    def _scan(self):
        """Yield (kind, start, end, name) for every placeholder, as the pattern would match."""
        text = self.template
        n = len(text)
        i = 0
        while True:
            j = text.find('$', i)
            if j < 0:
                return
            k = j + 1
            if k < n and text[k] == '$':
                yield ('escaped', j, k + 1, None)
                i = k + 1
            elif k < n and text[k] in _IDENT_START:
                e = k + 1
                while e < n and text[e] in _IDENT_CHARS:
                    e += 1
                yield ('named', j, e, text[k:e])
                i = e
            elif k < n and text[k] == '{' and k + 1 < n and text[k + 1] in _IDENT_START:
                e = k + 2
                while e < n and text[e] in _IDENT_CHARS:
                    e += 1
                if e < n and text[e] == '}':
                    yield ('braced', j, e + 1, text[k + 1:e])
                    i = e + 1
                else:
                    yield ('invalid', j, k, None)
                    i = k
            else:
                yield ('invalid', j, k, None)
                i = k

    def _invalid(self, position):
        lines = self.template[:position].splitlines(True)
        if not lines:
            colno = 1
            lineno = 1
        else:
            colno = position - len(''.join(lines[:-1]))
            lineno = len(lines)
        raise ValueError('Invalid placeholder in string: line %d, col %d' % (lineno, colno))

    def _render(self, mapping, kws, safe):
        text = self.template
        out = []
        last = 0
        for kind, start, end, name in self._scan():
            out.append(text[last:start])
            last = end
            if kind == 'escaped':
                out.append(self.delimiter)
            elif kind == 'invalid':
                if not safe:
                    self._invalid(end)
                out.append(self.delimiter)
            else:
                try:
                    if name in kws:
                        value = kws[name]
                    else:
                        value = mapping[name]
                except KeyError:
                    if not safe:
                        raise
                    out.append(text[start:end])
                else:
                    out.append(str(value))
        out.append(text[last:])
        return ''.join(out)

    def substitute(self, mapping=_MISSING, **kws):
        if mapping is _MISSING:
            mapping = kws
            kws = {}
        return self._render(mapping, kws, False)

    def safe_substitute(self, mapping=_MISSING, **kws):
        if mapping is _MISSING:
            mapping = kws
            kws = {}
        return self._render(mapping, kws, True)

    def is_valid(self):
        for kind, start, end, name in self._scan():
            if kind == 'invalid':
                return False
        return True

    def get_identifiers(self):
        ids = []
        for kind, start, end, name in self._scan():
            if name is not None and name not in ids:
                ids.append(name)
        return ids
