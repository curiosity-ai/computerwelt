# Extension: the `csv` module.
# Upstream Monty has no `csv`, so the import raised ModuleNotFoundError. This one is a port of
# CPython's `_csv.c` and `csv.py` — the same character-at-a-time parser, the same quoting
# decisions on the way out — and every expected value below was produced by CPython 3.13
# running the same code. `csv.Error` is named `Error`, as CPython names it. It reads any
# iterable of strings and writes to anything with `write`, so it needs no filesystem:
# `io.StringIO` and lists stand in for files.
import csv
import io


def rows(text, **kw):
    try:
        return list(csv.reader(io.StringIO(text), **kw))
    except Exception as e:
        return (type(e).__name__, str(e))

def lines(lst, **kw):
    try:
        return list(csv.reader(lst, **kw))
    except Exception as e:
        return (type(e).__name__, str(e))

def out(rowlist, **kw):
    buf = io.StringIO()
    try:
        w = csv.writer(buf, **kw)
        w.writerows(rowlist)
        return buf.getvalue()
    except Exception as e:
        return (type(e).__name__, str(e), buf.getvalue())

log = []

def show(label, v):
    log.append((label, v))

# --- reading ---
show('basic', rows('a,b,c\n1,2,3\n'))
show('no trailing newline', rows('a,b\n1,2'))
show('crlf', rows('a,b\r\n1,2\r\n'))
show('blank lines', rows('a,b\n\n1,2\n\n'))
show('empty fields', rows(',,\n,x,\n'))
show('quoted', rows('"a,b","c ""d"" e",f\n'))
show('multiline', rows('"line1\nline2",x\ny,z\n'))
show('unterminated quote', rows('"abc\ndef'))
show('unterminated strict', rows('"abc\ndef', strict=True))
show('quote mid field', rows('ab"cd"ef,g\n'))
show('text after quote', rows('"ab"cd,e\n'))
show('text after quote strict', rows('"ab"cd,e\n', strict=True))
show('spaces', rows('a, b, "c"\n', skipinitialspace=True))
show('spaces no skip', rows('a, b, "c"\n'))
show('tab', rows('a\tb\n', delimiter='\t'))
show('dialect excel-tab', rows('a\tb\n', dialect='excel-tab'))
show('dialect class', rows('a\tb\n', dialect=csv.excel_tab))
show('semicolon', rows('a;b;"c;d"\n', delimiter=';'))
show('escapechar', rows('a\\,b,c\n', escapechar='\\'))
show('escape in quotes', rows('"a\\"b",c\n', escapechar='\\', doublequote=False))
show('escaped newline', rows('a\\\nb,c\n', escapechar='\\'))
show('quote none', rows('"a",b\n', quoting=csv.QUOTE_NONE))
show('nonnumeric', rows('"a",1,2.5,"3"\n', quoting=csv.QUOTE_NONNUMERIC))
show('nonnumeric bad', rows('a,b\n', quoting=csv.QUOTE_NONNUMERIC))
show('strings', rows('"a",1,,""\n', quoting=csv.QUOTE_STRINGS))
show('notnull', rows('"a",1,,""\n', quoting=csv.QUOTE_NOTNULL))
show('cr only', rows('a,b\rc,d\r'))
show('cr in middle', rows('a\rb\n'))
show('list of lines', lines(['a,b', 'c,d']))
show('lines not str', lines([1, 2]))
show('bytes lines', lines([b'a,b']))
show('lone quote', rows('"\n'))
show('quote at end', rows('a,"b"'))
show('empty input', rows(''))
show('only newline', rows('\n'))
show('unicode', rows('é,☃\n'))
show('custom quotechar', rows("'a,b',c\n", quotechar="'"))
show('quotechar none', rows('"a",b\n', quotechar=None))
try:
    csv.reader(5)
except Exception as e:
    show('reader(5)', (type(e).__name__, str(e)))
r = csv.reader(io.StringIO('a,b\nc,d\n'))
show('next', (next(r), r.line_num, next(r), r.line_num))
try:
    next(r)
except StopIteration:
    show('stop', True)
show('limit', csv.field_size_limit())
old = csv.field_size_limit(10)
show('limit hit', rows('x' * 11 + ',a\n'))
show('limit ok', rows('x' * 10 + ',a\n'))
csv.field_size_limit(old)

# --- writing ---
data = [['a', 'b c', 'd,e', 'f"g', 'h\ni', ''], [1, 2.5, None, True, 'x', 'y']]
show('minimal', out(data))
show('all', out(data, quoting=csv.QUOTE_ALL))
show('nonnumeric', out(data, quoting=csv.QUOTE_NONNUMERIC))
show('strings', out(data, quoting=csv.QUOTE_STRINGS))
show('notnull', out(data, quoting=csv.QUOTE_NOTNULL))
show('none no escape', out([['a,b']], quoting=csv.QUOTE_NONE))
show('none escape', out([['a,b', 'c"d']], quoting=csv.QUOTE_NONE, escapechar='\\'))
show('no doublequote', out([['a"b']], doublequote=False, escapechar='\\'))
show('no doublequote no escape', out([['a"b']], doublequote=False))
show('unix', out(data, dialect='unix'))
show('tab', out([['a', 'b\tc']], dialect='excel-tab'))
show('delimiter', out([['a', 'b;c']], delimiter=';'))
show('lineterminator', out([['a', 'b']], lineterminator='|'))
show('single empty', out([['']]))
show('single empty none', out([['']], quoting=csv.QUOTE_NONE))
show('empty row', out([[]]))
show('two empty', out([['', '']]))
show('tuple row', out([(1, 2)]))
show('generator row', out([iter(['a', 'b'])]))
show('not iterable', out([5]))
show('str row', out(['abc']))
show('escapechar itself', out([['a\\b']], escapechar='\\'))
show('quotechar change', out([["a'b"]], quotechar="'"))

class W:
    def __init__(self): self.parts = []
    def write(self, s):
        self.parts.append(s)
        return 99
w = csv.writer(W())
show('writerow returns', w.writerow(['a']))
show('writerows returns', w.writerows([['a']]))
try:
    csv.writer(5)
except Exception as e:
    show('writer(5)', (type(e).__name__, str(e)))

# --- dialects ---
show('list', sorted(csv.list_dialects()))
d = csv.get_dialect('excel')
show('excel attrs', (d.delimiter, d.quotechar, d.escapechar, d.doublequote, d.skipinitialspace, d.lineterminator, d.quoting, d.strict))
csv.register_dialect('pipes', delimiter='|', quoting=csv.QUOTE_ALL)
show('pipes', out([['a', 'b']], dialect='pipes'))
show('pipes attrs', (csv.get_dialect('pipes').delimiter, csv.get_dialect('pipes').quoting, csv.get_dialect('pipes').lineterminator))
csv.unregister_dialect('pipes')
for call in [lambda: csv.get_dialect('pipes'), lambda: csv.unregister_dialect('pipes'), lambda: csv.reader([], dialect='nope'),
             lambda: csv.reader([], delimiter='ab'), lambda: csv.reader([], delimiter=1), lambda: csv.reader([], delimiter=None),
             lambda: csv.reader([], quoting=9), lambda: csv.reader([], quoting='x'), lambda: csv.reader([], lineterminator=1),
             lambda: csv.reader([], quotechar=''), lambda: csv.reader([], quotechar=None, quoting=csv.QUOTE_ALL),
             lambda: csv.reader([], bogus=1), lambda: csv.register_dialect(5), lambda: csv.field_size_limit('x')]:
    try:
        call()
        show('call', 'no error')
    except Exception as e:
        show('call', (type(e).__name__, str(e)))

class MyDialect:
    delimiter = '|'
    quotechar = "'"
    escapechar = None
    doublequote = True
    skipinitialspace = False
    lineterminator = '\n'
    quoting = csv.QUOTE_MINIMAL
show('class dialect', rows("a|'b|c'\n", dialect=MyDialect))
show('class dialect w', out([['a', 'b|c']], dialect=MyDialect))
show('quoting consts', (csv.QUOTE_MINIMAL, csv.QUOTE_ALL, csv.QUOTE_NONNUMERIC, csv.QUOTE_NONE, csv.QUOTE_STRINGS, csv.QUOTE_NOTNULL))
show('excel inst', csv.excel().delimiter)

# --- DictReader / DictWriter ---
dr = csv.DictReader(io.StringIO('x,y\n1,2\n3\n4,5,6,7\n\n8,9\n'))
show('fieldnames', dr.fieldnames)
show('dict rows', list(dr))
show('dict line_num', dr.line_num)
dr = csv.DictReader(io.StringIO('1,2\n3,4\n'), fieldnames=['a', 'b'], restkey='rest', restval='?')
show('given fieldnames', list(dr))
dr = csv.DictReader(io.StringIO('1,2,3\n4\n'), fieldnames=['a'], restkey='rest')
show('restkey', list(dr))
dr = csv.DictReader(io.StringIO('a,b\n1\n'), restval='MISSING')
show('restval', list(dr))
dr = csv.DictReader(io.StringIO(''))
show('empty dictreader', (dr.fieldnames, list(dr)))
dr = csv.DictReader(io.StringIO('a;b\n1;2\n'), delimiter=';')
show('dictreader kw', list(dr))
dr = csv.DictReader(io.StringIO('a,a\n1,2\n'))
show('dup', list(dr))
buf = io.StringIO()
dw = csv.DictWriter(buf, fieldnames=['a', 'b', 'c'], restval='-')
dw.writeheader()
dw.writerow({'a': 1, 'b': 'x,y'})
dw.writerows([{'c': 3, 'a': 2, 'b': None}])
show('dictwriter', buf.getvalue())
try:
    dw.writerow({'a': 1, 'z': 2})
except Exception as e:
    show('extras', (type(e).__name__, str(e)))
buf = io.StringIO()
dw = csv.DictWriter(buf, ['a'], extrasaction='ignore')
dw.writerow({'a': 1, 'z': 2})
show('ignore', buf.getvalue())
try:
    csv.DictWriter(io.StringIO(), ['a'], extrasaction='bad')
except Exception as e:
    show('bad extrasaction', (type(e).__name__, str(e)))
show('dw attrs', (dw.fieldnames, dw.restval, dw.extrasaction))
buf = io.StringIO()
dw = csv.DictWriter(buf, ['a', 'b'], delimiter=';', quoting=csv.QUOTE_ALL)
dw.writeheader()
dw.writerow({'a': 1, 'b': 2})
show('dw kw', buf.getvalue())

# --- Sniffer ---
sn = csv.Sniffer()
samples = ['a,b,c\n1,2,3\n4,5,6\n', 'a;b;c\n1;2;3\n', "a\tb\n1\t2\n", "'a','b'\n'c','d'\n", 'x| y| z\n1| 2| 3\n', '"a","b"\n"c","d"\n',
           'name,age\n"Smith, J",30\n"Doe, A",25\n', 'a b c\nd e f\n', 'one\ntwo\n']
for s in samples:
    try:
        d = sn.sniff(s)
        show(('sniff', s), (d.delimiter, d.quotechar, d.doublequote, d.skipinitialspace, d.lineterminator, d.quoting))
    except Exception as e:
        show(('sniff', s), (type(e).__name__, str(e)))
show('sniff delimiters', sn.sniff('a,b;c\n1,2;3\n', delimiters=';').delimiter)
show('header yes', sn.has_header('name,age\nbob,30\nann,25\ncy,41\n'))
show('header no', sn.has_header('1,2\n3,4\n5,6\n'))
show('header strings', sn.has_header('a,b\nc,d\ne,f\n'))
show('sniff then read', rows('a;b\n1;2\n', dialect=sn.sniff('a;b\n1;2\n')))
show('error class', (issubclass(csv.Error, Exception), csv.Error.__name__))


# === compare against what CPython produced ===
EXPECTED = [('basic', [['a', 'b', 'c'], ['1', '2', '3']]), ('no trailing newline', [['a', 'b'], ['1', '2']]), ('crlf', [['a', 'b'], ['1', '2']]), ('blank lines', [['a', 'b'], [], ['1', '2'], []]), ('empty fields', [['', '', ''], ['', 'x', '']]), ('quoted', [['a,b', 'c "d" e', 'f']]), ('multiline', [['line1\nline2', 'x'], ['y', 'z']]), ('unterminated quote', [['abc\ndef']]), ('unterminated strict', ('Error', 'unexpected end of data')), ('quote mid field', [['ab"cd"ef', 'g']]), ('text after quote', [['abcd', 'e']]), ('text after quote strict', ('Error', '\',\' expected after \'"\'')), ('spaces', [['a', 'b', 'c']]), ('spaces no skip', [['a', ' b', ' "c"']]), ('tab', [['a', 'b']]), ('dialect excel-tab', [['a', 'b']]), ('dialect class', [['a', 'b']]), ('semicolon', [['a', 'b', 'c;d']]), ('escapechar', [['a,b', 'c']]), ('escape in quotes', [['a"b', 'c']]), ('escaped newline', [['a\nb', 'c']]), ('quote none', [['"a"', 'b']]), ('nonnumeric', [['a', 1.0, 2.5, '3']]), ('nonnumeric bad', ('ValueError', "could not convert string to float: 'a'")), ('strings', [['a', 1.0, None, '']]), ('notnull', [['a', '1', None, '']]), ('cr only', ('Error', "new-line character seen in unquoted field - do you need to open the file with newline=''?")), ('cr in middle', ('Error', "new-line character seen in unquoted field - do you need to open the file with newline=''?")), ('list of lines', [['a', 'b'], ['c', 'd']]), ('lines not str', ('Error', 'iterator should return strings, not int (the file should be opened in text mode)')), ('bytes lines', ('Error', 'iterator should return strings, not bytes (the file should be opened in text mode)')), ('lone quote', [['\n']]), ('quote at end', [['a', 'b']]), ('empty input', []), ('only newline', [[]]), ('unicode', [['é', '☃']]), ('custom quotechar', [['a,b', 'c']]), ('quotechar none', [['"a"', 'b']]), ('reader(5)', ('TypeError', "'int' object is not iterable")), ('next', (['a', 'b'], 1, ['c', 'd'], 2)), ('stop', True), ('limit', 131072), ('limit hit', ('Error', 'field larger than field limit (10)')), ('limit ok', [['xxxxxxxxxx', 'a']]), ('minimal', 'a,b c,"d,e","f""g","h\ni",\r\n1,2.5,,True,x,y\r\n'), ('all', '"a","b c","d,e","f""g","h\ni",""\r\n"1","2.5","","True","x","y"\r\n'), ('nonnumeric', '"a","b c","d,e","f""g","h\ni",""\r\n1,2.5,"",True,"x","y"\r\n'), ('strings', '"a","b c","d,e","f""g","h\ni",""\r\n1,2.5,,True,"x","y"\r\n'), ('notnull', '"a","b c","d,e","f""g","h\ni",""\r\n"1","2.5",,"True","x","y"\r\n'), ('none no escape', ('Error', 'need to escape, but no escapechar set', '')), ('none escape', 'a\\,b,c\\"d\r\n'), ('no doublequote', 'a\\"b\r\n'), ('no doublequote no escape', ('Error', 'need to escape, but no escapechar set', '')), ('unix', '"a","b c","d,e","f""g","h\ni",""\n"1","2.5","","True","x","y"\n'), ('tab', 'a\t"b\tc"\r\n'), ('delimiter', 'a;"b;c"\r\n'), ('lineterminator', 'a,b|'), ('single empty', '""\r\n'), ('single empty none', ('Error', 'single empty field record must be quoted', '')), ('empty row', '\r\n'), ('two empty', ',\r\n'), ('tuple row', '1,2\r\n'), ('generator row', 'a,b\r\n'), ('not iterable', ('Error', 'iterable expected, not int', '')), ('str row', 'a,b,c\r\n'), ('escapechar itself', 'a\\\\b\r\n'), ('quotechar change', "'a''b'\r\n"), ('writerow returns', 99), ('writerows returns', None), ('writer(5)', ('TypeError', 'argument 1 must have a "write" method')), ('list', ['excel', 'excel-tab', 'unix']), ('excel attrs', (',', '"', None, True, False, '\r\n', 0, False)), ('pipes', '"a"|"b"\r\n'), ('pipes attrs', ('|', 1, '\r\n')), ('call', ('Error', 'unknown dialect')), ('call', ('Error', 'unknown dialect')), ('call', ('Error', 'unknown dialect')), ('call', ('TypeError', '"delimiter" must be a 1-character string')), ('call', ('TypeError', '"delimiter" must be string, not int')), ('call', ('TypeError', '"delimiter" must be string, not NoneType')), ('call', ('TypeError', 'bad "quoting" value')), ('call', ('TypeError', '"quoting" must be an integer')), ('call', ('TypeError', '"lineterminator" must be a string')), ('call', ('TypeError', '"quotechar" must be a 1-character string')), ('call', ('TypeError', 'quotechar must be set if quoting enabled')), ('call', ('TypeError', "this function got an unexpected keyword argument 'bogus'")), ('call', ('TypeError', 'dialect name must be a string')), ('call', ('TypeError', 'limit must be an integer')), ('class dialect', [['a', 'b|c']]), ('class dialect w', "a|'b|c'\n"), ('quoting consts', (0, 1, 2, 3, 4, 5)), ('excel inst', ','), ('fieldnames', ['x', 'y']), ('dict rows', [{'x': '1', 'y': '2'}, {'x': '3', 'y': None}, {'x': '4', 'y': '5', None: ['6', '7']}, {'x': '8', 'y': '9'}]), ('dict line_num', 6), ('given fieldnames', [{'a': '1', 'b': '2'}, {'a': '3', 'b': '4'}]), ('restkey', [{'a': '1', 'rest': ['2', '3']}, {'a': '4'}]), ('restval', [{'a': '1', 'b': 'MISSING'}]), ('empty dictreader', (None, [])), ('dictreader kw', [{'a': '1', 'b': '2'}]), ('dup', [{'a': '2'}]), ('dictwriter', 'a,b,c\r\n1,"x,y",-\r\n2,,3\r\n'), ('extras', ('ValueError', "dict contains fields not in fieldnames: 'z'")), ('ignore', '1\r\n'), ('bad extrasaction', ('ValueError', "extrasaction (bad) must be 'raise' or 'ignore'")), ('dw attrs', (['a'], '', 'ignore')), ('dw kw', '"a";"b"\r\n"1";"2"\r\n'), (('sniff', 'a,b,c\n1,2,3\n4,5,6\n'), (',', '"', False, False, '\r\n', 0)), (('sniff', 'a;b;c\n1;2;3\n'), (';', '"', False, False, '\r\n', 0)), (('sniff', 'a\tb\n1\t2\n'), ('\t', '"', False, False, '\r\n', 0)), (('sniff', "'a','b'\n'c','d'\n"), (',', "'", False, False, '\r\n', 0)), (('sniff', 'x| y| z\n1| 2| 3\n'), (' ', '"', False, False, '\r\n', 0)), (('sniff', '"a","b"\n"c","d"\n'), (',', '"', False, False, '\r\n', 0)), (('sniff', 'name,age\n"Smith, J",30\n"Doe, A",25\n'), (',', '"', False, False, '\r\n', 0)), (('sniff', 'a b c\nd e f\n'), (' ', '"', False, False, '\r\n', 0)), (('sniff', 'one\ntwo\n'), ('o', '"', False, False, '\r\n', 0)), ('sniff delimiters', ';'), ('header yes', True), ('header no', False), ('header strings', False), ('sniff then read', [['a', 'b'], ['1', '2']]), ('error class', (True, 'Error'))]

assert len(log) == len(EXPECTED), (len(log), len(EXPECTED))
for got, want in zip(log, EXPECTED):
    assert got == want, (got, want)
