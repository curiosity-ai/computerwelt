# Extension: `csv` over the sandbox's own files.
# The reader takes lines from anything iterable and the writer calls `write`, so the
# filesystem `open` is the same one every other module sees. (`newline=''` is the one thing a
# CPython script adds that is refused here — see todo.md — and nothing is lost by leaving it
# out: no layer in this sandbox translates line endings.)
import csv

path = '/virtual/people.csv'

# === write a file, read it back as lists and as dicts ===
with open(path, 'w') as f:
    writer = csv.writer(f)
    writer.writerow(['name', 'note', 'score'])
    writer.writerow(['Ann', 'likes "quotes", commas', 1.5])
    writer.writerow(['Bob', 'two\nlines', 7])

with open(path) as f:
    text = f.read()
assert text == 'name,note,score\r\nAnn,"likes ""quotes"", commas",1.5\r\nBob,"two\nlines",7\r\n'

with open(path) as f:
    rows = list(csv.reader(f))
assert rows == [
    ['name', 'note', 'score'],
    ['Ann', 'likes "quotes", commas', '1.5'],
    ['Bob', 'two\nlines', '7'],
]

with open(path) as f:
    reader = csv.DictReader(f)
    assert reader.fieldnames == ['name', 'note', 'score']
    records = list(reader)
assert records[1] == {'name': 'Bob', 'note': 'two\nlines', 'score': '7'}
assert [r['name'] for r in records] == ['Ann', 'Bob']

# === line_num counts physical lines, not records ===
with open(path) as f:
    reader = csv.reader(f)
    for _ in reader:
        pass
    assert reader.line_num == 4

# === DictWriter appends to a file and the reader sees every row ===
with open(path, 'w') as f:
    writer = csv.DictWriter(f, fieldnames=['k', 'v'], lineterminator='\n')
    writer.writeheader()
    writer.writerows([{'k': 'a', 'v': 1}, {'k': 'b', 'v': 2}])
with open(path) as f:
    assert f.read() == 'k,v\na,1\nb,2\n'
with open(path) as f:
    assert {r['k']: int(r['v']) for r in csv.DictReader(f)} == {'a': 1, 'b': 2}

# === a reader is lazy: it consumes lines only as rows are asked for ===
class Source:
    def __init__(self):
        self.taken = 0
    def __iter__(self):
        return self
    def __next__(self):
        self.taken += 1
        if self.taken > 3:
            raise StopIteration
        return 'r%d,x\n' % self.taken

source = Source()
reader = csv.reader(source)
assert next(reader) == ['r1', 'x']
assert source.taken == 1
assert list(reader) == [['r2', 'x'], ['r3', 'x']]

# === a writer hands each row to write() whole ===
class Sink:
    def __init__(self):
        self.chunks = []
    def write(self, s):
        self.chunks.append(s)
        return len(s)

sink = Sink()
w = csv.writer(sink)
assert w.writerow(['a', 'b']) == 5
w.writerows([['c'], ['d']])
assert sink.chunks == ['a,b\r\n', 'c\r\n', 'd\r\n']

# === a Python-defined dialect is any object with the right attributes ===
class Pipes:
    delimiter = '|'
    quotechar = "'"
    escapechar = None
    doublequote = True
    skipinitialspace = False
    lineterminator = '\n'
    quoting = csv.QUOTE_MINIMAL

with open(path, 'w') as f:
    csv.writer(f, Pipes).writerow(['a', 'b|c'])
with open(path) as f:
    assert f.read() == "a|'b|c'\n"
with open(path) as f:
    assert list(csv.reader(f, dialect=Pipes)) == [['a', 'b|c']]

# === each run has its own dialect registry ===
csv.register_dialect('mine', delimiter=':')
assert 'mine' in csv.list_dialects()
assert next(csv.reader(['a:b'], 'mine')) == ['a', 'b']

# === Error is a real exception class ===
assert issubclass(csv.Error, Exception)
try:
    next(csv.reader(['a\rb\n']))
    assert False
except csv.Error as exc:
    assert 'new-line character seen in unquoted field' in str(exc)

# === the field size limit protects against a runaway quoted field ===
huge = '"' + 'x' * 200000 + '"\n'
try:
    list(csv.reader([huge]))
    assert False
except csv.Error as exc:
    assert str(exc) == 'field larger than field limit (131072)'
