# Extension: `zipfile` over the sandbox's virtual filesystem — not in upstream Monty.
# Archives are read from and written back to the same filesystem `open` uses. Members are
# inflated only when read, through the sandbox's cap and against their CRC-32; extraction
# drops `/`, `.` and `..` from member names, as CPython does, so nothing lands outside the
# target directory. Expected values produced by CPython 3.13.
import os
import sys
import zipfile

# === write an archive three ways ===
with open('report.txt', 'w') as handle:
    handle.write('quarterly numbers\n' * 20)
os.makedirs('assets', exist_ok=True)
with open('assets/logo.bin', 'wb') as handle:
    handle.write(bytes(range(256)))

with zipfile.ZipFile('bundle.zip', 'w', compression=zipfile.ZIP_DEFLATED) as archive:
    archive.writestr('readme.md', '# Bundle\n\nThree files.\n')
    archive.writestr('data/values.csv', b'a,b\n1,2\n3,4\n')
    archive.write('report.txt')
    archive.write('assets/logo.bin', arcname='images/logo.bin')
    archive.writestr('stored.txt', 'not compressed', compress_type=zipfile.ZIP_STORED)
    with archive.open('streamed.txt', 'w') as member:
        member.write(b'written through open()\n')
    archive.mkdir('empty')

assert (zipfile.is_zipfile('bundle.zip')) == True, "zipfile.is_zipfile('bundle.zip')"
assert (zipfile.is_zipfile('report.txt')) == False, "zipfile.is_zipfile('report.txt')"
assert (zipfile.is_zipfile('no-such-file.zip')) == False, "zipfile.is_zipfile('no-such-file.zip')"

# === read it back ===
with zipfile.ZipFile('bundle.zip') as archive:
    names = archive.namelist()
    readme = archive.read('readme.md')
    values = archive.read('data/values.csv').decode()
    report = archive.read('report.txt')
    logo = archive.read('images/logo.bin')
    info = archive.getinfo('report.txt')
    stored = archive.getinfo('stored.txt')
    empty = archive.getinfo('empty/')
    with archive.open('streamed.txt') as member:
        streamed = member.read()
    with archive.open('data/values.csv') as member:
        rows = [line for line in member]
    sizes = [(i.filename, i.file_size) for i in archive.infolist()]
    bad = archive.testzip()
assert (names) == ['readme.md', 'data/values.csv', 'report.txt', 'images/logo.bin', 'stored.txt', 'streamed.txt', 'empty/'], 'names'
assert (readme, values) == (b'# Bundle\n\nThree files.\n', 'a,b\n1,2\n3,4\n'), 'readme, values'
assert (report == b'quarterly numbers\n' * 20, logo == bytes(range(256))) == (True, True), "report == b'quarterly numbers\\n' * 20, logo == bytes(range(256))"
assert (info.filename, info.file_size, info.compress_type, info.CRC) == ('report.txt', 360, 8, 1403847170), 'info.filename, info.file_size, info.compress_type, info.CRC'
assert (info.compress_size < info.file_size) == True, 'info.compress_size < info.file_size'
assert (stored.compress_type, stored.file_size, stored.compress_size) == (0, 14, 14), 'stored.compress_type, stored.file_size, stored.compress_size'
assert (empty.is_dir(), info.is_dir()) == (True, False), 'empty.is_dir(), info.is_dir()'
assert (streamed, rows) == (b'written through open()\n', [b'a,b\n', b'1,2\n', b'3,4\n']), 'streamed, rows'
assert (sizes) == [('readme.md', 23), ('data/values.csv', 12), ('report.txt', 360), ('images/logo.bin', 256), ('stored.txt', 14), ('streamed.txt', 23), ('empty/', 0)], 'sizes'
assert (bad) == None, 'bad'
assert (len(info.date_time), info.date_time[0] >= 1980) == (6, True), 'len(info.date_time), info.date_time[0] >= 1980'

# === errors ===
archive = zipfile.ZipFile('bundle.zip')
try:
    archive.getinfo('missing.txt')
    assert False, "archive.getinfo('missing.txt')"
except KeyError:
    pass
try:
    archive.read('missing.txt')
    assert False, "archive.read('missing.txt')"
except KeyError:
    pass
try:
    archive.writestr('x', 'y')
    assert False, "archive.writestr('x', 'y')"
except ValueError:
    pass
archive.close()
try:
    archive.read("readme.md")
    assert False, 'archive.read("readme.md")'
except ValueError:
    pass
assert (len(archive.namelist())) == 7, 'len(archive.namelist())'
try:
    zipfile.ZipFile('report.txt')
    assert False, "zipfile.ZipFile('report.txt')"
except zipfile.BadZipFile:
    pass
try:
    zipfile.ZipFile('absent.zip')
    assert False, "zipfile.ZipFile('absent.zip')"
except FileNotFoundError:
    pass
try:
    zipfile.ZipFile('bundle.zip', 'q')
    assert False, "zipfile.ZipFile('bundle.zip', 'q')"
except ValueError:
    pass
try:
    zipfile.ZipFile('bundle.zip', 'x')
    assert False, "zipfile.ZipFile('bundle.zip', 'x')"
except FileExistsError:
    pass
try:
    zipfile.ZipFile('bundle.zip').getinfo('nope')
except KeyError as error:
    message = error.args[0]
assert (message) == "There is no item named 'nope' in the archive", 'message'

# === append ===
with zipfile.ZipFile('bundle.zip', 'a') as archive:
    archive.writestr('appended.txt', 'later')
with zipfile.ZipFile('bundle.zip') as archive:
    after = archive.namelist()
    appended = archive.read('appended.txt')
    still = archive.read('readme.md')
assert (after[-1], len(after), appended, still) == ('appended.txt', 8, b'later', b'# Bundle\n\nThree files.\n'), 'after[-1], len(after), appended, still'

# === extract and extractall ===
with zipfile.ZipFile('bundle.zip') as archive:
    target = archive.extract('data/values.csv', 'out')
    archive.extractall('everything')
    archive.extractall('subset', members=['readme.md'])
assert (os.path.relpath(target)) == 'out/data/values.csv', 'os.path.relpath(target)'
assert (sorted(os.listdir('everything'))) == ['appended.txt', 'data', 'empty', 'images', 'readme.md', 'report.txt', 'stored.txt', 'streamed.txt'], "sorted(os.listdir('everything'))"
assert (sorted(os.listdir('everything/images')), os.path.isdir('everything/empty')) == (['logo.bin'], True), "sorted(os.listdir('everything/images')), os.path.isdir('everything/empty')"
with open('everything/report.txt') as handle:
    extracted_report = handle.read()
assert (extracted_report == 'quarterly numbers\n' * 20) == True, "extracted_report == 'quarterly numbers\\n' * 20"
assert (os.listdir('subset')) == ['readme.md'], "os.listdir('subset')"

# === hostile member names stay inside the target ===
with zipfile.ZipFile('evil.zip', 'w') as archive:
    archive.writestr('../escape.txt', 'up one')
    archive.writestr('/absolute.txt', 'rooted')
    archive.writestr('a/../../b/./c.txt', 'mixed')
with zipfile.ZipFile('evil.zip') as archive:
    evil_names = archive.namelist()
    archive.extractall('jail')
assert (evil_names) == ['../escape.txt', '/absolute.txt', 'a/../../b/./c.txt'], 'evil_names'
assert (sorted(os.listdir('jail'))) == ['a', 'absolute.txt', 'escape.txt'], "sorted(os.listdir('jail'))"
assert (os.path.exists('escape.txt'), os.path.exists('../escape.txt')) == (False, False), "os.path.exists('escape.txt'), os.path.exists('../escape.txt')"
with open('jail/escape.txt') as handle:
    jailed = handle.read()
assert (jailed) == 'up one', 'jailed'
assert (sorted(os.listdir('jail/a/b')) if os.path.isdir('jail/a/b') else sorted(os.listdir('jail/b'))) == ['c.txt'], "sorted(os.listdir('jail/a/b')) if os.path.isdir('jail/a/b') else sorted(os.listdir('jail/b'))"

# === ZipInfo ===
member = zipfile.ZipInfo('dated.txt', date_time=(2024, 5, 17, 13, 45, 30))
member.compress_type = zipfile.ZIP_DEFLATED
with zipfile.ZipFile('dated.zip', 'w') as archive:
    archive.writestr(member, 'with a date')
with zipfile.ZipFile('dated.zip') as archive:
    dated = archive.getinfo('dated.txt')
assert (dated.date_time, dated.compress_type, archive.read('dated.txt') if False else 'closed') == ((2024, 5, 17, 13, 45, 30), 8, 'closed'), "dated.date_time, dated.compress_type, archive.read('dated.txt') if False else 'closed'"
assert (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED, zipfile.ZIP_BZIP2, zipfile.ZIP_LZMA) == (0, 8, 12, 14), 'zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED, zipfile.ZIP_BZIP2, zipfile.ZIP_LZMA'
assert (zipfile.BadZipfile is zipfile.BadZipFile) == True, 'zipfile.BadZipfile is zipfile.BadZipFile'

# === the sandbox's caps (so CPython is not asked) ===
if sys.platform == 'monty':
    with zipfile.ZipFile('bomb.zip', 'w', compression=zipfile.ZIP_DEFLATED) as archive:
        archive.writestr('zeros.bin', b'\0' * ((1 << 26) + 1))
    with zipfile.ZipFile('bomb.zip') as archive:
        try:
            archive.read('zeros.bin')
            capped = False
        except MemoryError:
            capped = True
    assert capped
