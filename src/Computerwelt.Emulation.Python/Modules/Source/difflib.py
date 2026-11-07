"""difflib: sequence comparison, following CPython 3.13's algorithms step for step.

SequenceMatcher is the Ratcliff/Obershelp "gestalt" matcher with CPython's junk and
popularity heuristics, so the blocks, opcodes and ratios it reports are CPython's. HtmlDiff
is not here; everything else a script reaches for is.
"""

from heapq import nlargest as _nlargest
from collections import namedtuple as _namedtuple

__all__ = ['get_close_matches', 'ndiff', 'restore', 'SequenceMatcher',
           'Differ', 'IS_CHARACTER_JUNK', 'IS_LINE_JUNK', 'context_diff',
           'unified_diff', 'Match']

Match = _namedtuple('Match', 'a b size')


def _calculate_ratio(matches, length):
    if length:
        return 2.0 * matches / length
    return 1.0


class SequenceMatcher:
    """Compares two sequences of hashable elements."""

    def __init__(self, isjunk=None, a='', b='', autojunk=True):
        self.isjunk = isjunk
        self.a = None
        self.b = None
        self.autojunk = autojunk
        self.set_seqs(a, b)

    def set_seqs(self, a, b):
        self.set_seq1(a)
        self.set_seq2(b)

    def set_seq1(self, a):
        if a is self.a:
            return
        self.a = a
        self.matching_blocks = None
        self.opcodes = None

    def set_seq2(self, b):
        if b is self.b:
            return
        self.b = b
        self.matching_blocks = None
        self.opcodes = None
        self.fullbcount = None
        self.__chain_b()

    def __chain_b(self):
        b = self.b
        self.b2j = b2j = {}
        for i, elt in enumerate(b):
            indices = b2j.setdefault(elt, [])
            indices.append(i)

        # Purge junk elements.
        self.bjunk = junk = set()
        isjunk = self.isjunk
        if isjunk:
            for elt in b2j.keys():
                if isjunk(elt):
                    junk.add(elt)
            for elt in junk:
                b2j.pop(elt)

        # Purge popular elements that are not junk.
        self.bpopular = popular = set()
        n = len(b)
        if self.autojunk and n >= 200:
            ntest = n // 100 + 1
            for elt, idxs in b2j.items():
                if len(idxs) > ntest:
                    popular.add(elt)
            for elt in popular:
                b2j.pop(elt)

    def find_longest_match(self, alo=0, ahi=None, blo=0, bhi=None):
        a, b, b2j, bjunk = self.a, self.b, self.b2j, self.bjunk
        isbjunk = lambda elt: elt in bjunk
        if ahi is None:
            ahi = len(a)
        if bhi is None:
            bhi = len(b)
        besti, bestj, bestsize = alo, blo, 0
        j2len = {}
        nothing = []
        for i in range(alo, ahi):
            j2lenget = j2len.get
            newj2len = {}
            for j in b2j.get(a[i], nothing):
                if j < blo:
                    continue
                if j >= bhi:
                    break
                k = newj2len[j] = j2lenget(j - 1, 0) + 1
                if k > bestsize:
                    besti, bestj, bestsize = i - k + 1, j - k + 1, k
            j2len = newj2len

        while besti > alo and bestj > blo and \
              not isbjunk(b[bestj - 1]) and \
              a[besti - 1] == b[bestj - 1]:
            besti, bestj, bestsize = besti - 1, bestj - 1, bestsize + 1
        while besti + bestsize < ahi and bestj + bestsize < bhi and \
              not isbjunk(b[bestj + bestsize]) and \
              a[besti + bestsize] == b[bestj + bestsize]:
            bestsize += 1

        while besti > alo and bestj > blo and \
              isbjunk(b[bestj - 1]) and \
              a[besti - 1] == b[bestj - 1]:
            besti, bestj, bestsize = besti - 1, bestj - 1, bestsize + 1
        while besti + bestsize < ahi and bestj + bestsize < bhi and \
              isbjunk(b[bestj + bestsize]) and \
              a[besti + bestsize] == b[bestj + bestsize]:
            bestsize = bestsize + 1

        return Match(besti, bestj, bestsize)

    def get_matching_blocks(self):
        if self.matching_blocks is not None:
            return self.matching_blocks
        la, lb = len(self.a), len(self.b)

        queue = [(0, la, 0, lb)]
        matching_blocks = []
        while queue:
            alo, ahi, blo, bhi = queue.pop()
            i, j, k = x = self.find_longest_match(alo, ahi, blo, bhi)
            if k:
                matching_blocks.append(x)
                if alo < i and blo < j:
                    queue.append((alo, i, blo, j))
                if i + k < ahi and j + k < bhi:
                    queue.append((i + k, ahi, j + k, bhi))
        matching_blocks.sort()

        # Collapse adjacent blocks.
        i1 = j1 = k1 = 0
        non_adjacent = []
        for i2, j2, k2 in matching_blocks:
            if i1 + k1 == i2 and j1 + k1 == j2:
                k1 += k2
            else:
                if k1:
                    non_adjacent.append((i1, j1, k1))
                i1, j1, k1 = i2, j2, k2
        if k1:
            non_adjacent.append((i1, j1, k1))

        non_adjacent.append((la, lb, 0))
        self.matching_blocks = [Match(t[0], t[1], t[2]) for t in non_adjacent]
        return self.matching_blocks

    def get_opcodes(self):
        if self.opcodes is not None:
            return self.opcodes
        i = j = 0
        self.opcodes = answer = []
        for ai, bj, size in self.get_matching_blocks():
            tag = ''
            if i < ai and j < bj:
                tag = 'replace'
            elif i < ai:
                tag = 'delete'
            elif j < bj:
                tag = 'insert'
            if tag:
                answer.append((tag, i, ai, j, bj))
            i, j = ai + size, bj + size
            if size:
                answer.append(('equal', ai, i, bj, j))
        return answer

    def get_grouped_opcodes(self, n=3):
        codes = self.get_opcodes()
        if not codes:
            codes = [("equal", 0, 1, 0, 1)]
        if codes[0][0] == 'equal':
            tag, i1, i2, j1, j2 = codes[0]
            codes[0] = tag, max(i1, i2 - n), i2, max(j1, j2 - n), j2
        if codes[-1][0] == 'equal':
            tag, i1, i2, j1, j2 = codes[-1]
            codes[-1] = tag, i1, min(i2, i1 + n), j1, min(j2, j1 + n)

        nn = n + n
        group = []
        for tag, i1, i2, j1, j2 in codes:
            if tag == 'equal' and i2 - i1 > nn:
                group.append((tag, i1, min(i2, i1 + n), j1, min(j2, j1 + n)))
                yield group
                group = []
                i1, j1 = max(i1, i2 - n), max(j1, j2 - n)
            group.append((tag, i1, i2, j1, j2))
        if group and not (len(group) == 1 and group[0][0] == 'equal'):
            yield group

    def ratio(self):
        matches = sum(triple[-1] for triple in self.get_matching_blocks())
        return _calculate_ratio(matches, len(self.a) + len(self.b))

    def quick_ratio(self):
        if self.fullbcount is None:
            self.fullbcount = fullbcount = {}
            for elt in self.b:
                fullbcount[elt] = fullbcount.get(elt, 0) + 1
        fullbcount = self.fullbcount
        avail = {}
        matches = 0
        for elt in self.a:
            if elt in avail:
                numb = avail[elt]
            else:
                numb = fullbcount.get(elt, 0)
            avail[elt] = numb - 1
            if numb > 0:
                matches = matches + 1
        return _calculate_ratio(matches, len(self.a) + len(self.b))

    def real_quick_ratio(self):
        la, lb = len(self.a), len(self.b)
        return _calculate_ratio(min(la, lb), la + lb)


def get_close_matches(word, possibilities, n=3, cutoff=0.6):
    """The best "good enough" matches for word among possibilities, best first."""
    if not n > 0:
        raise ValueError("n must be > 0: %r" % (n,))
    if not 0.0 <= cutoff <= 1.0:
        raise ValueError("cutoff must be in [0.0, 1.0]: %r" % (cutoff,))
    result = []
    s = SequenceMatcher()
    s.set_seq2(word)
    for x in possibilities:
        s.set_seq1(x)
        if s.real_quick_ratio() >= cutoff and \
           s.quick_ratio() >= cutoff and \
           s.ratio() >= cutoff:
            result.append((s.ratio(), x))

    result = _nlargest(n, result)
    return [x for score, x in result]


def _keep_original_ws(s, tag_s):
    """Replace whitespace with the original whitespace characters in `s`."""
    return ''.join(
        c if tag_c == " " and c.isspace() else tag_c
        for c, tag_c in zip(s, tag_s)
    )


class Differ:
    """Compares sequences of lines of text and produces human-readable differences."""

    def __init__(self, linejunk=None, charjunk=None):
        self.linejunk = linejunk
        self.charjunk = charjunk

    def compare(self, a, b):
        cruncher = SequenceMatcher(self.linejunk, a, b)
        for tag, alo, ahi, blo, bhi in cruncher.get_opcodes():
            if tag == 'replace':
                g = self._fancy_replace(a, alo, ahi, b, blo, bhi)
            elif tag == 'delete':
                g = self._dump('-', a, alo, ahi)
            elif tag == 'insert':
                g = self._dump('+', b, blo, bhi)
            elif tag == 'equal':
                g = self._dump(' ', a, alo, ahi)
            else:
                raise ValueError('unknown tag %r' % (tag,))

            yield from g

    def _dump(self, tag, x, lo, hi):
        for i in range(lo, hi):
            yield '%s %s' % (tag, x[i])

    def _plain_replace(self, a, alo, ahi, b, blo, bhi):
        assert alo < ahi and blo < bhi
        if bhi - blo < ahi - alo:
            first = self._dump('+', b, blo, bhi)
            second = self._dump('-', a, alo, ahi)
        else:
            first = self._dump('-', a, alo, ahi)
            second = self._dump('+', b, blo, bhi)

        for g in first, second:
            yield from g

    def _fancy_replace(self, a, alo, ahi, b, blo, bhi):
        cutoff = 0.74999
        cruncher = SequenceMatcher(self.charjunk)
        crqr = cruncher.real_quick_ratio
        cqr = cruncher.quick_ratio
        cr = cruncher.ratio

        WINDOW = 10
        best_i = best_j = None
        dump_i, dump_j = alo, blo
        for j in range(blo, bhi):
            cruncher.set_seq2(b[j])
            aequiv = alo + (j - blo)
            arange = range(max(aequiv - WINDOW, dump_i),
                           min(aequiv + WINDOW + 1, ahi))
            if not arange:
                break
            best_ratio = cutoff
            for i in arange:
                cruncher.set_seq1(a[i])
                if (crqr() > best_ratio
                      and cqr() > best_ratio
                      and cr() > best_ratio):
                    best_i, best_j, best_ratio = i, j, cr()

            if best_i is None:
                continue

            yield from self._fancy_helper(a, dump_i, best_i,
                                          b, dump_j, best_j)
            aelt, belt = a[best_i], b[best_j]
            if aelt != belt:
                atags = btags = ""
                cruncher.set_seqs(aelt, belt)
                for tag, ai1, ai2, bj1, bj2 in cruncher.get_opcodes():
                    la, lb = ai2 - ai1, bj2 - bj1
                    if tag == 'replace':
                        atags += '^' * la
                        btags += '^' * lb
                    elif tag == 'delete':
                        atags += '-' * la
                    elif tag == 'insert':
                        btags += '+' * lb
                    elif tag == 'equal':
                        atags += ' ' * la
                        btags += ' ' * lb
                    else:
                        raise ValueError('unknown tag %r' % (tag,))
                yield from self._qformat(aelt, belt, atags, btags)
            else:
                yield '  ' + aelt
            dump_i, dump_j = best_i + 1, best_j + 1
            best_i = best_j = None

        yield from self._fancy_helper(a, dump_i, ahi,
                                      b, dump_j, bhi)

    def _fancy_helper(self, a, alo, ahi, b, blo, bhi):
        g = []
        if alo < ahi:
            if blo < bhi:
                g = self._plain_replace(a, alo, ahi, b, blo, bhi)
            else:
                g = self._dump('-', a, alo, ahi)
        elif blo < bhi:
            g = self._dump('+', b, blo, bhi)

        yield from g

    def _qformat(self, aline, bline, atags, btags):
        atags = _keep_original_ws(aline, atags).rstrip()
        btags = _keep_original_ws(bline, btags).rstrip()

        yield "- " + aline
        if atags:
            yield f"? {atags}\n"

        yield "+ " + bline
        if btags:
            yield f"? {btags}\n"


def IS_LINE_JUNK(line, pat=None):
    """True for a line that is blank or holds only a '#'."""
    if pat is not None:
        return pat(line) is not None
    stripped = line.strip()
    return stripped == '' or stripped == '#'


def IS_CHARACTER_JUNK(ch, ws=" \t"):
    """True for a space or a tab."""
    return ch in ws


def _format_range_unified(start, stop):
    beginning = start + 1
    length = stop - start
    if length == 1:
        return '{}'.format(beginning)
    if not length:
        beginning -= 1
    return '{},{}'.format(beginning, length)


def unified_diff(a, b, fromfile='', tofile='', fromfiledate='',
                 tofiledate='', n=3, lineterm='\n'):
    """Compares two sequences of lines and yields the delta in unified diff format."""
    _check_types(a, b, fromfile, tofile, fromfiledate, tofiledate, lineterm)
    started = False
    for group in SequenceMatcher(None, a, b).get_grouped_opcodes(n):
        if not started:
            started = True
            fromdate = '\t{}'.format(fromfiledate) if fromfiledate else ''
            todate = '\t{}'.format(tofiledate) if tofiledate else ''
            yield '--- {}{}{}'.format(fromfile, fromdate, lineterm)
            yield '+++ {}{}{}'.format(tofile, todate, lineterm)

        first, last = group[0], group[-1]
        file1_range = _format_range_unified(first[1], last[2])
        file2_range = _format_range_unified(first[3], last[4])
        yield '@@ -{} +{} @@{}'.format(file1_range, file2_range, lineterm)

        for tag, i1, i2, j1, j2 in group:
            if tag == 'equal':
                for line in a[i1:i2]:
                    yield ' ' + line
                continue
            if tag == 'replace' or tag == 'delete':
                for line in a[i1:i2]:
                    yield '-' + line
            if tag == 'replace' or tag == 'insert':
                for line in b[j1:j2]:
                    yield '+' + line


def _format_range_context(start, stop):
    beginning = start + 1
    length = stop - start
    if not length:
        beginning -= 1
    if length <= 1:
        return '{}'.format(beginning)
    return '{},{}'.format(beginning, beginning + length - 1)


def context_diff(a, b, fromfile='', tofile='',
                 fromfiledate='', tofiledate='', n=3, lineterm='\n'):
    """Compares two sequences of lines and yields the delta in context diff format."""
    _check_types(a, b, fromfile, tofile, fromfiledate, tofiledate, lineterm)
    prefix = dict(insert='+ ', delete='- ', replace='! ', equal='  ')
    started = False
    for group in SequenceMatcher(None, a, b).get_grouped_opcodes(n):
        if not started:
            started = True
            fromdate = '\t{}'.format(fromfiledate) if fromfiledate else ''
            todate = '\t{}'.format(tofiledate) if tofiledate else ''
            yield '*** {}{}{}'.format(fromfile, fromdate, lineterm)
            yield '--- {}{}{}'.format(tofile, todate, lineterm)

        first, last = group[0], group[-1]
        yield '***************' + lineterm

        file1_range = _format_range_context(first[1], last[2])
        yield '*** {} ****{}'.format(file1_range, lineterm)

        if any(tag == 'replace' or tag == 'delete' for tag, _, _, _, _ in group):
            for tag, i1, i2, _, _ in group:
                if tag != 'insert':
                    for line in a[i1:i2]:
                        yield prefix[tag] + line

        file2_range = _format_range_context(first[3], last[4])
        yield '--- {} ----{}'.format(file2_range, lineterm)

        if any(tag == 'replace' or tag == 'insert' for tag, _, _, _, _ in group):
            for tag, _, _, j1, j2 in group:
                if tag != 'delete':
                    for line in b[j1:j2]:
                        yield prefix[tag] + line


def _check_types(a, b, *args):
    if a and not isinstance(a[0], str):
        raise TypeError('lines to compare must be str, not %s (%r)' %
                        (type(a[0]).__name__, a[0]))
    if b and not isinstance(b[0], str):
        raise TypeError('lines to compare must be str, not %s (%r)' %
                        (type(b[0]).__name__, b[0]))
    for arg in args:
        if not isinstance(arg, str):
            raise TypeError('all arguments must be str, not: %r' % (arg,))


def ndiff(a, b, linejunk=None, charjunk=IS_CHARACTER_JUNK):
    """Compares a and b (lists of strings) and returns a Differ-style delta."""
    return Differ(linejunk, charjunk).compare(a, b)


def restore(delta, which):
    """Yields one of the two sequences that generated a delta."""
    try:
        tag = {1: "- ", 2: "+ "}[int(which)]
    except KeyError:
        raise ValueError('unknown delta choice (must be 1 or 2): %r'
                         % which) from None
    prefixes = ("  ", tag)
    for line in delta:
        if line[:2] in prefixes:
            yield line[2:]
