# Extension: the `textwrap` module (wrap, fill, shorten, dedent, indent, TextWrapper).
# Upstream Monty has none. Expected output is CPython 3.13's.
_lines = []

def _log(*parts):
    _lines.append(' '.join(str(p) for p in parts))


import textwrap
t = "The quick brown fox jumps over the lazy dog. It's a well-known pangram -- used everywhere!  Does it end? Yes.  Super-cali-fragilistic-expialidocious words exist too, and https://example.com/a/very/long/url/that/will/need/breaking."
for w in (10, 20, 40, 72):
    _log(w, textwrap.wrap(t, w))
_log(textwrap.fill(t, 30, initial_indent='* ', subsequent_indent='  '))
_log(textwrap.wrap(t, 25, break_long_words=False))
_log(textwrap.wrap(t, 25, break_on_hyphens=False))
_log(textwrap.wrap('a   b\tc\nd', 5), textwrap.wrap('a   b\tc\nd', 5, replace_whitespace=False), textwrap.wrap('a\tb', 20, expand_tabs=False))
_log(textwrap.wrap('Hello.  World. Foo? Bar!', 80, fix_sentence_endings=True))
_log(textwrap.wrap('', 10), textwrap.wrap('   ', 10), textwrap.wrap(' a ', 10, drop_whitespace=False))
_log(textwrap.shorten('Hello  world! This is long', width=12), textwrap.shorten('Hello', 10), textwrap.shorten(t, 40, placeholder='...'))
_log(textwrap.wrap(t, 30, max_lines=2), textwrap.wrap(t, 30, max_lines=1, placeholder=' >>'))
try: textwrap.wrap('x', 0)
except ValueError as e: _log(e)
try: textwrap.wrap('x', 3, max_lines=1, placeholder='toolong')
except ValueError as e: _log(e)
_log(repr(textwrap.dedent('''
    hello
      indented
    back
  ''')))
_log(repr(textwrap.dedent('  a\n\tb\n')), repr(textwrap.dedent('   x\n\n  y\n')), repr(textwrap.dedent('a\n b')), repr(textwrap.dedent('')))
_log(repr(textwrap.indent('a\n\nb\n  \nc', '> ')), repr(textwrap.indent('a\n\nb', '# ', lambda l: True)))
tw = textwrap.TextWrapper(width=15, initial_indent='- ')
_log(tw.wrap('some words to wrap around nicely'), tw.fill('x y z'), tw.width)
tw.width = 8; _log(tw.wrap('some words to wrap'))
_log(textwrap.wrap("self-contained re-entrant e-mail -- dash---triple x-y-z 3-4-5", 8))
_log(textwrap.wrap("café naïve résumé " * 3, 12))


# === compare against what CPython printed ===
EXPECTED = ['10 [\'The quick\', \'brown fox\', \'jumps over\', \'the lazy\', "dog. It\'s", \'a well-\', \'known\', \'pangram --\', \'used every\', \'where!\', \'Does it\', \'end? Yes.\', \'Super-\', \'cali-fragi\', \'listic-exp\', \'ialidociou\', \'s words\', \'exist too,\', \'and https:\', \'//example.\', \'com/a/very\', \'/long/url/\', \'that/will/\', \'need/break\', \'ing.\']', '20 [\'The quick brown fox\', \'jumps over the lazy\', "dog. It\'s a well-", \'known pangram --\', \'used everywhere!\', \'Does it end? Yes.\', \'Super-cali-\', \'fragilistic-\', \'expialidocious words\', \'exist too, and https\', \'://example.com/a/ver\', \'y/long/url/that/will\', \'/need/breaking.\']', '40 [\'The quick brown fox jumps over the lazy\', "dog. It\'s a well-known pangram -- used", \'everywhere!  Does it end? Yes.  Super-\', \'cali-fragilistic-expialidocious words\', \'exist too, and https://example.com/a/ver\', \'y/long/url/that/will/need/breaking.\']', '72 ["The quick brown fox jumps over the lazy dog. It\'s a well-known pangram", \'-- used everywhere!  Does it end? Yes.  Super-cali-fragilistic-\', \'expialidocious words exist too, and\', \'https://example.com/a/very/long/url/that/will/need/breaking.\']', "* The quick brown fox jumps\n  over the lazy dog. It's a\n  well-known pangram -- used\n  everywhere!  Does it end?\n  Yes.  Super-cali-\n  fragilistic-expialidocious\n  words exist too, and https:/\n  /example.com/a/very/long/url\n  /that/will/need/breaking.", '[\'The quick brown fox jumps\', "over the lazy dog. It\'s a", \'well-known pangram --\', \'used everywhere!  Does it\', \'end? Yes.  Super-cali-\', \'fragilistic-\', \'expialidocious words\', \'exist too, and\', \'https://example.com/a/very/long/url/that/will/need/breaking.\']', '[\'The quick brown fox jumps\', "over the lazy dog. It\'s a", \'well-known pangram --\', \'used everywhere!  Does it\', \'end? Yes.  Super-cali-fra\', \'gilistic-expialidocious\', \'words exist too, and http\', \'s://example.com/a/very/lo\', \'ng/url/that/will/need/bre\', \'aking.\']', "['a   b', 'c d'] ['a   b', 'c\\nd'] ['a b']", "['Hello.  World.  Foo?  Bar!']", "[] [] [' a ']", 'Hello [...] Hello The quick brown fox jumps over the...', '[\'The quick brown fox jumps over\', "the lazy dog. It\'s a [...]"] [\'The quick brown fox jumps >>\']', 'invalid width 0 (must be > 0)', 'placeholder too large for max width', "'\\nhello\\n  indented\\nback\\n'", "'  a\\n\\tb\\n' ' x\\n\\ny\\n' 'a\\n b' ''", "'> a\\n\\n> b\\n  \\n> c' '# a\\n# \\n# b'", "['- some words to', 'wrap around', 'nicely'] - x y z 15", "['- some', 'words to', 'wrap']", "['self-con', 'tained', 're-', 'entrant', 'e-mail', '-- dash', '---', 'triple', 'x-y-z', '3-4-5']", "['café naïve', 'résumé café', 'naïve résumé', 'café naïve', 'résumé']"]

assert len(_lines) == len(EXPECTED), (len(_lines), len(EXPECTED))
for got, want in zip(_lines, EXPECTED):
    assert got == want, (got, want)
