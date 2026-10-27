"""textwrap: text wrapping and filling."""

import re

_whitespace = '\t\n\x0b\x0c\r '

_WS = r'[\t\n\x0b\x0c\r ]'
_NWS = r'[^\t\n\x0b\x0c\r ]'
_WORD_PUNCT = r'[\w!"\'&.,?]'
_LETTER = r'[^\d\W]'

_wordsep_re = re.compile(r'''
    ( # any whitespace
      %(ws)s+
    | # em-dash between words
      (?<=%(wp)s) -{2,} (?=\w)
    | # word, possibly hyphenated
      %(nws)s+? (?:
        # hyphenated word
          -(?: (?<=%(lt)s{2}-) | (?<=%(lt)s-%(lt)s-))
          (?= %(lt)s -? %(lt)s)
        | # end of word
          (?=%(ws)s|\Z)
        | # em-dash
          (?<=%(wp)s) (?=-{2,}\w)
        )
    )''' % {'wp': _WORD_PUNCT, 'lt': _LETTER, 'ws': _WS, 'nws': _NWS}, re.VERBOSE)
_wordsep_simple_re = re.compile(r'(%s+)' % _WS)
_sentence_end_re = re.compile(r'[a-z][\.\!\?][\"\']?\Z')


class TextWrapper:
    """Object for wrapping/filling text."""

    def __init__(self, width=70, initial_indent='', subsequent_indent='', expand_tabs=True,
                 replace_whitespace=True, fix_sentence_endings=False, break_long_words=True,
                 drop_whitespace=True, break_on_hyphens=True, tabsize=8, *, max_lines=None,
                 placeholder=' [...]'):
        self.width = width
        self.initial_indent = initial_indent
        self.subsequent_indent = subsequent_indent
        self.expand_tabs = expand_tabs
        self.replace_whitespace = replace_whitespace
        self.fix_sentence_endings = fix_sentence_endings
        self.break_long_words = break_long_words
        self.drop_whitespace = drop_whitespace
        self.break_on_hyphens = break_on_hyphens
        self.tabsize = tabsize
        self.max_lines = max_lines
        self.placeholder = placeholder

    def _munge_whitespace(self, text):
        if self.expand_tabs:
            text = text.expandtabs(self.tabsize)
        if self.replace_whitespace:
            for ch in '\t\n\x0b\x0c\r':
                text = text.replace(ch, ' ')
        return text

    def _split(self, text):
        if self.break_on_hyphens is True:
            chunks = _wordsep_re.split(text)
        else:
            chunks = _wordsep_simple_re.split(text)
        chunks = [c for c in chunks if c]
        return chunks

    def _fix_sentence_endings(self, chunks):
        i = 0
        patsearch = _sentence_end_re.search
        while i < len(chunks) - 1:
            if chunks[i + 1] == ' ' and patsearch(chunks[i]):
                chunks[i + 1] = '  '
                i += 2
            else:
                i += 1

    def _handle_long_word(self, reversed_chunks, cur_line, cur_len, width):
        if width < 1:
            space_left = 1
        else:
            space_left = width - cur_len
        if self.break_long_words:
            end = space_left
            chunk = reversed_chunks[-1]
            if self.break_on_hyphens and len(chunk) > space_left:
                hyphen = chunk.rfind('-', 0, space_left)
                if hyphen > 0 and any(c != '-' for c in chunk[:hyphen]):
                    end = hyphen + 1
            cur_line.append(chunk[:end])
            reversed_chunks[-1] = chunk[end:]
        elif not cur_line:
            cur_line.append(reversed_chunks.pop())

    def _wrap_chunks(self, chunks):
        lines = []
        if self.width <= 0:
            raise ValueError('invalid width %r (must be > 0)' % self.width)
        if self.max_lines is not None:
            if self.max_lines > 1:
                indent = self.subsequent_indent
            else:
                indent = self.initial_indent
            if len(indent) + len(self.placeholder.lstrip()) > self.width:
                raise ValueError('placeholder too large for max width')
        chunks.reverse()
        while chunks:
            cur_line = []
            cur_len = 0
            if lines:
                indent = self.subsequent_indent
            else:
                indent = self.initial_indent
            width = self.width - len(indent)
            if self.drop_whitespace and chunks[-1].strip() == '' and lines:
                del chunks[-1]
            while chunks:
                l = len(chunks[-1])
                if cur_len + l <= width:
                    cur_line.append(chunks.pop())
                    cur_len += l
                else:
                    break
            if chunks and len(chunks[-1]) > width:
                self._handle_long_word(chunks, cur_line, cur_len, width)
                cur_len = sum(map(len, cur_line))
            if self.drop_whitespace and cur_line and cur_line[-1].strip() == '':
                cur_len -= len(cur_line[-1])
                del cur_line[-1]
            if cur_line:
                if (self.max_lines is None or len(lines) + 1 < self.max_lines
                        or (not chunks or self.drop_whitespace and len(chunks) == 1 and not chunks[0].strip())
                        and cur_len <= width):
                    lines.append(indent + ''.join(cur_line))
                else:
                    while cur_line:
                        if cur_line[-1].strip() and cur_len + len(self.placeholder) <= width:
                            cur_line.append(self.placeholder)
                            lines.append(indent + ''.join(cur_line))
                            break
                        cur_len -= len(cur_line[-1])
                        del cur_line[-1]
                    else:
                        if lines:
                            prev_line = lines[-1].rstrip()
                            if len(prev_line) + len(self.placeholder) <= self.width:
                                lines[-1] = prev_line + self.placeholder
                                break
                        lines.append(indent + self.placeholder.lstrip())
                    break
        return lines

    def _split_chunks(self, text):
        text = self._munge_whitespace(text)
        return self._split(text)

    def wrap(self, text):
        chunks = self._split_chunks(text)
        if self.fix_sentence_endings:
            self._fix_sentence_endings(chunks)
        return self._wrap_chunks(chunks)

    def fill(self, text):
        return '\n'.join(self.wrap(text))


def wrap(text, width=70, **kwargs):
    w = TextWrapper(width=width, **kwargs)
    return w.wrap(text)


def fill(text, width=70, **kwargs):
    w = TextWrapper(width=width, **kwargs)
    return w.fill(text)


def shorten(text, width, **kwargs):
    w = TextWrapper(width=width, max_lines=1, **kwargs)
    return w.fill(' '.join(text.strip().split()))


_whitespace_only_re = re.compile('^[ \t]+$', re.MULTILINE)
_leading_whitespace_re = re.compile('(^[ \t]*)(?:[^ \t\n])', re.MULTILINE)


def dedent(text):
    """Remove any common leading whitespace from every line in `text`."""
    margin = None
    text = _whitespace_only_re.sub('', text)
    indents = _leading_whitespace_re.findall(text)
    for indent in indents:
        if margin is None:
            margin = indent
        elif indent.startswith(margin):
            pass
        elif margin.startswith(indent):
            margin = indent
        else:
            for i, (x, y) in enumerate(zip(margin, indent)):
                if x != y:
                    margin = margin[:i]
                    break
    if margin:
        text = re.sub(r'(?m)^' + margin, '', text)
    return text


def indent(text, prefix, predicate=None):
    """Add 'prefix' to the beginning of selected lines in 'text'."""
    if predicate is None:
        def predicate(line):
            return line.strip()
    out = []
    for line in text.splitlines(True):
        out.append(prefix + line if predicate(line) else line)
    return ''.join(out)
