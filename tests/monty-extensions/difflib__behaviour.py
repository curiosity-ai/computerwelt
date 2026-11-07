# Extension: `difflib` — not in upstream Monty.
# SequenceMatcher follows CPython's algorithm (junk, autojunk popularity), so the blocks,
# opcodes and ratios are CPython's. Expected values produced by CPython 3.13.
import difflib
from difflib import SequenceMatcher, Match

# === SequenceMatcher ===
s = SequenceMatcher(None, 'abxcd', 'abcd')
assert (s.ratio()) == 0.8888888888888888, 's.ratio()'
assert (s.quick_ratio()) == 0.8888888888888888, 's.quick_ratio()'
assert (s.real_quick_ratio()) == 0.8888888888888888, 's.real_quick_ratio()'
assert ([tuple(m) for m in s.get_matching_blocks()]) == [(0, 0, 2), (3, 2, 2), (5, 4, 0)], '[tuple(m) for m in s.get_matching_blocks()]'
assert repr(s.get_matching_blocks()[0]) == 'Match(a=0, b=0, size=2)', 's.get_matching_blocks()[0]'
assert (s.get_opcodes()) == [('equal', 0, 2, 0, 2), ('delete', 2, 3, 2, 2), ('equal', 3, 5, 2, 4)], 's.get_opcodes()'
assert repr(s.find_longest_match(0, 5, 0, 4)) == 'Match(a=0, b=0, size=2)', 's.find_longest_match(0, 5, 0, 4)'
assert (s.find_longest_match().size) == 2, 's.find_longest_match().size'
s2 = SequenceMatcher(lambda x: x == ' ', 'private Thread currentThread;', 'private volatile Thread currentThread;')
assert (round(s2.ratio(), 3)) == 0.866, 'round(s2.ratio(), 3)'
assert (s2.get_opcodes()) == [('equal', 0, 8, 0, 8), ('insert', 8, 8, 8, 17), ('equal', 8, 29, 17, 38)], 's2.get_opcodes()'
s3 = SequenceMatcher(None, 'qabxcd', 'abycdf')
assert (s3.get_opcodes()) == [('delete', 0, 1, 0, 0), ('equal', 1, 3, 0, 2), ('replace', 3, 4, 2, 3), ('equal', 4, 6, 3, 5), ('insert', 6, 6, 5, 6)], 's3.get_opcodes()'
assert (list(s3.get_grouped_opcodes(1))) == [[('delete', 0, 1, 0, 0), ('equal', 1, 3, 0, 2), ('replace', 3, 4, 2, 3), ('equal', 4, 6, 3, 5), ('insert', 6, 6, 5, 6)]], 'list(s3.get_grouped_opcodes(1))'
s4 = SequenceMatcher(None, ' abcd', 'abcd abcd')
assert (s4.find_longest_match(0, 5, 0, 9)) == Match(a=0, b=4, size=5), 's4.find_longest_match(0, 5, 0, 9)'
s5 = SequenceMatcher(None, [1, 2, 3, 4], [1, 3, 4, 5])
assert (s5.ratio()) == 0.75, 's5.ratio()'
assert (s5.get_opcodes()) == [('equal', 0, 1, 0, 1), ('delete', 1, 2, 1, 1), ('equal', 2, 4, 1, 3), ('insert', 4, 4, 3, 4)], 's5.get_opcodes()'
s6 = SequenceMatcher()
s6.set_seqs('abcd', 'bcde')
assert (s6.ratio()) == 0.75, 's6.ratio()'
s6.set_seq1('xbcd')
assert (s6.ratio()) == 0.75, 's6.ratio()'
assert (SequenceMatcher(None, '', '').ratio()) == 1.0, "SequenceMatcher(None, '', '').ratio()"
assert (SequenceMatcher(None, 'a' * 300 + 'b', 'a' * 299 + 'bc').ratio()) == 0.9966777408637874, "SequenceMatcher(None, 'a' * 300 + 'b', 'a' * 299 + 'bc').ratio()"
assert (SequenceMatcher(None, 'a' * 300 + 'b', 'a' * 299 + 'bc', autojunk=False).ratio()) == 0.9966777408637874, "SequenceMatcher(None, 'a' * 300 + 'b', 'a' * 299 + 'bc', autojunk=False).ratio()"

# === get_close_matches ===
assert (difflib.get_close_matches('appel', ['ape', 'apple', 'peach', 'puppy'])) == ['apple', 'ape'], "difflib.get_close_matches('appel', ['ape', 'apple', 'peach', 'puppy'])"
assert (difflib.get_close_matches('wheel', ['while', 'wheat', 'when', 'whale', 'wheels'], n=2)) == ['wheels', 'when'], "difflib.get_close_matches('wheel', ['while', 'wheat', 'when', 'whale', 'wheels'], n=2)"
assert (difflib.get_close_matches('xyz', ['abc'])) == [], "difflib.get_close_matches('xyz', ['abc'])"
assert (difflib.get_close_matches('apple', ['ape', 'apple', 'apply'], cutoff=0.9)) == ['apple'], "difflib.get_close_matches('apple', ['ape', 'apple', 'apply'], cutoff=0.9)"
try:
    difflib.get_close_matches('a', ['a'], n=0)
    assert False, "difflib.get_close_matches('a', ['a'], n=0)"
except ValueError:
    pass

# === unified_diff and context_diff ===
a = ['one\n', 'two\n', 'three\n', 'four\n', 'five\n', 'six\n', 'seven\n', 'eight\n', 'nine\n', 'ten\n']
b = ['zero\n', 'one\n', 'tree\n', 'four\n', 'five\n', 'six\n', 'seven\n', 'eight\n', 'nine\n', 'ten\n', 'eleven\n']
assert (list(difflib.unified_diff(a, b, fromfile='before.py', tofile='after.py'))) == ['--- before.py\n', '+++ after.py\n', '@@ -1,6 +1,6 @@\n', '+zero\n', ' one\n', '-two\n', '-three\n', '+tree\n', ' four\n', ' five\n', ' six\n', '@@ -8,3 +8,4 @@\n', ' eight\n', ' nine\n', ' ten\n', '+eleven\n'], "list(difflib.unified_diff(a, b, fromfile='before.py', tofile='after.py'))"
assert (list(difflib.unified_diff(a, b, n=1, lineterm=''))) == ['--- ', '+++ ', '@@ -1,4 +1,4 @@', '+zero\n', ' one\n', '-two\n', '-three\n', '+tree\n', ' four\n', '@@ -10 +10,2 @@', ' ten\n', '+eleven\n'], "list(difflib.unified_diff(a, b, n=1, lineterm=''))"
assert (list(difflib.unified_diff(['x'], ['x']))) == [], "list(difflib.unified_diff(['x'], ['x']))"
assert (list(difflib.unified_diff([], ['a\n'], 'old', 'new', '2024-01-01', '2024-01-02'))) == ['--- old\t2024-01-01\n', '+++ new\t2024-01-02\n', '@@ -0,0 +1 @@\n', '+a\n'], "list(difflib.unified_diff([], ['a\\n'], 'old', 'new', '2024-01-01', '2024-01-02'))"
assert (list(difflib.context_diff(a, b, fromfile='before.py', tofile='after.py'))) == ['*** before.py\n', '--- after.py\n', '***************\n', '*** 1,6 ****\n', '  one\n', '! two\n', '! three\n', '  four\n', '  five\n', '  six\n', '--- 1,6 ----\n', '+ zero\n', '  one\n', '! tree\n', '  four\n', '  five\n', '  six\n', '***************\n', '*** 8,10 ****\n', '--- 8,11 ----\n', '  eight\n', '  nine\n', '  ten\n', '+ eleven\n'], "list(difflib.context_diff(a, b, fromfile='before.py', tofile='after.py'))"
assert (list(difflib.context_diff(['a\n', 'b\n'], ['a\n', 'c\n', 'd\n'], n=0))) == ['*** \n', '--- \n', '***************\n', '*** 2 ****\n', '! b\n', '--- 2,3 ----\n', '! c\n', '! d\n'], "list(difflib.context_diff(['a\\n', 'b\\n'], ['a\\n', 'c\\n', 'd\\n'], n=0))"
try:
    list(difflib.unified_diff([b'a'], [b'b']))
    assert False, "list(difflib.unified_diff([b'a'], [b'b']))"
except TypeError:
    pass

# === ndiff, Differ and restore ===
delta = list(difflib.ndiff(['one\n', 'two\n', 'three\n'], ['ore\n', 'tree\n', 'emu\n']))
assert (delta) == ['- one\n', '?  ^\n', '+ ore\n', '?  ^\n', '- two\n', '- three\n', '?  -\n', '+ tree\n', '+ emu\n'], 'delta'
assert (list(difflib.restore(delta, 1))) == ['one\n', 'two\n', 'three\n'], 'list(difflib.restore(delta, 1))'
assert (list(difflib.restore(delta, 2))) == ['ore\n', 'tree\n', 'emu\n'], 'list(difflib.restore(delta, 2))'
assert (list(difflib.Differ().compare(['abcdefg\n', 'same\n'], ['abcXefg\n', 'same\n', 'new\n']))) == ['- abcdefg\n', '?    ^\n', '+ abcXefg\n', '?    ^\n', '  same\n', '+ new\n'], "list(difflib.Differ().compare(['abcdefg\\n', 'same\\n'], ['abcXefg\\n', 'same\\n', 'new\\n']))"
assert (list(difflib.ndiff(['\tindented line\n'], ['\tindentid line\n']))) == ['- \tindented line\n', '? \t      ^\n', '+ \tindentid line\n', '? \t      ^\n'], "list(difflib.ndiff(['\\tindented line\\n'], ['\\tindentid line\\n']))"
assert (list(difflib.ndiff(['a'], ['b']))) == ['- a', '+ b'], "list(difflib.ndiff(['a'], ['b']))"
try:
    list(difflib.restore(delta, 3))
    assert False, 'list(difflib.restore(delta, 3))'
except ValueError:
    pass
assert (difflib.IS_LINE_JUNK('\n')) == True, "difflib.IS_LINE_JUNK('\\n')"
assert (difflib.IS_LINE_JUNK('  #   \n')) == True, "difflib.IS_LINE_JUNK('  #   \\n')"
assert (difflib.IS_LINE_JUNK('hello\n')) == False, "difflib.IS_LINE_JUNK('hello\\n')"
assert (difflib.IS_CHARACTER_JUNK(' ')) == True, "difflib.IS_CHARACTER_JUNK(' ')"
assert (difflib.IS_CHARACTER_JUNK('x')) == False, "difflib.IS_CHARACTER_JUNK('x')"
