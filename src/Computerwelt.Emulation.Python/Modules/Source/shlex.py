"""shlex: simple shell-like lexical analysis (the split / quote / join helpers)."""

_SAFE = ('abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_@%+=:,./-')


def quote(s):
    """Return a shell-escaped version of the string s."""
    if not s:
        return "''"
    for ch in s:
        if ch not in _SAFE:
            return "'" + s.replace("'", "'\"'\"'") + "'"
    return s


def join(split_command):
    """Return a shell-escaped string from *split_command*."""
    return ' '.join(quote(arg) for arg in split_command)


def split(s, comments=False, posix=True):
    """Split the string s using shell-like syntax."""
    if s is None:
        raise ValueError("s argument must not be None")
    if not posix:
        raise NotImplementedError('only posix=True is supported')
    tokens = []
    token = []
    in_word = False
    quote_char = None
    i = 0
    n = len(s)
    while i < n:
        c = s[i]
        i += 1
        if quote_char == "'":
            if c == "'":
                quote_char = None
            else:
                token.append(c)
        elif quote_char == '"':
            if c == '"':
                quote_char = None
            elif c == '\\':
                if i >= n:
                    raise ValueError('No closing quotation')
                nxt = s[i]
                i += 1
                if nxt == '"' or nxt == '\\':
                    token.append(nxt)
                else:
                    token.append('\\')
                    token.append(nxt)
            else:
                token.append(c)
        elif c == ' ' or c == '\t' or c == '\r' or c == '\n':
            if in_word:
                tokens.append(''.join(token))
                token = []
                in_word = False
        elif comments and c == '#' and not in_word:
            while i < n and s[i] != '\n':
                i += 1
        elif c == "'" or c == '"':
            quote_char = c
            in_word = True
        elif c == '\\':
            if i >= n:
                raise ValueError('No escaped character')
            token.append(s[i])
            i += 1
            in_word = True
        else:
            token.append(c)
            in_word = True
    if quote_char is not None:
        raise ValueError('No closing quotation')
    if in_word:
        tokens.append(''.join(token))
    return tokens
