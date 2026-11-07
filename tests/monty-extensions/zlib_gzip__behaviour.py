# Extension: `zlib` and `gzip` — not in upstream Monty.
# Decompression is the sandbox's own decoder: truncated and corrupt streams raise as CPython's
# do, and output stops at a cap (64 MiB) with MemoryError, so a compression bomb cannot
# exhaust the host. Compressed bytes come from .NET's compressor and are not byte-identical to
# CPython's, so the fixture pins round trips and decodes streams CPython produced.
# Expected values produced by CPython 3.13.
import sys
import zlib
import gzip

text = b'The quick brown fox jumps over the lazy dog. ' * 50

# === checksums ===
assert (zlib.crc32(b'hello world')) == 222957957, "zlib.crc32(b'hello world')"
assert (zlib.crc32(b'world', zlib.crc32(b'hello '))) == 222957957, "zlib.crc32(b'world', zlib.crc32(b'hello '))"
assert (zlib.crc32(b'')) == 0, "zlib.crc32(b'')"
assert (zlib.adler32(b'hello world')) == 436929629, "zlib.adler32(b'hello world')"
assert (zlib.adler32(b'world', zlib.adler32(b'hello '))) == 436929629, "zlib.adler32(b'world', zlib.adler32(b'hello '))"
assert (zlib.adler32(b'')) == 1, "zlib.adler32(b'')"
assert (zlib.crc32(text), zlib.adler32(text)) == (1608530683, 629942220), 'zlib.crc32(text), zlib.adler32(text)'

# === round trips at every level and container ===
assert (all(zlib.decompress(zlib.compress(text, level)) == text for level in range(-1, 10))) == True, 'all(zlib.decompress(zlib.compress(text, level)) == text for level in range(-1, 10))'
assert (zlib.decompress(zlib.compress(b'')) == b'') == True, "zlib.decompress(zlib.compress(b'')) == b''"
assert (zlib.compress(text)[:1], zlib.compress(text, 1)[:2], zlib.compress(text, 9)[:2]) == (b'x', b'x\x01', b'x\xda'), 'zlib.compress(text)[:1], zlib.compress(text, 1)[:2], zlib.compress(text, 9)[:2]'
assert (zlib.decompress(zlib.compress(text, wbits=-15), wbits=-15) == text) == True, 'zlib.decompress(zlib.compress(text, wbits=-15), wbits=-15) == text'
assert (zlib.decompress(zlib.compress(text, wbits=31), wbits=31) == text) == True, 'zlib.decompress(zlib.compress(text, wbits=31), wbits=31) == text'
assert (zlib.decompress(zlib.compress(text, wbits=31), wbits=47) == text) == True, 'zlib.decompress(zlib.compress(text, wbits=31), wbits=47) == text'
assert (zlib.decompress(zlib.compress(text), wbits=47) == text) == True, 'zlib.decompress(zlib.compress(text), wbits=47) == text'
assert (len(zlib.compress(text)) < len(text) // 10) == True, 'len(zlib.compress(text)) < len(text) // 10'
assert (zlib.compress(bytes(range(256)), 0)[:2]) == b'x\x01', 'zlib.compress(bytes(range(256)), 0)[:2]'

# === streams CPython made ===
assert (zlib.decompress(b'x\x9c\xcbH\xcd\xc9\xc9W(\xcf/\xcaI\x01\x00\x1a\x0b\x04]')) == b'hello world', "zlib.decompress(b'x\\x9c\\xcbH\\xcd\\xc9\\xc9W(\\xcf/\\xcaI\\x01\\x00\\x1a\\x0b\\x04]')"
assert (zlib.decompress(b'x\x01\x01\x05\x00\xfa\xffhello\x06,\x02\x15')) == b'hello', "zlib.decompress(b'x\\x01\\x01\\x05\\x00\\xfa\\xffhello\\x06,\\x02\\x15')"
assert (zlib.decompress(b'x\x9c\xcbH\xcd\xc9\xc9W(\xcf/\xcaI\x01\x00\x1a\x0b\x04]extra bytes')) == b'hello world', "zlib.decompress(b'x\\x9c\\xcbH\\xcd\\xc9\\xc9W(\\xcf/\\xcaI\\x01\\x00\\x1a\\x0b\\x04]extra bytes')"
assert (zlib.decompress(b'\xcbH\xcd\xc9\xc9\x07\x00', wbits=-15)) == b'hello', "zlib.decompress(b'\\xcbH\\xcd\\xc9\\xc9\\x07\\x00', wbits=-15)"
assert (gzip.decompress(b'\x1f\x8b\x08\x00\x00\x00\x00\x00\x02\xff\xcbH\xcd\xc9\xc9\x07\x00\x86\xa6\x106\x05\x00\x00\x00')) == b'hello', "gzip.decompress(b'\\x1f\\x8b\\x08\\x00\\x00\\x00\\x00\\x00\\x02\\xff\\xcbH\\xcd\\xc9\\xc9\\x07\\x00\\x86\\xa6\\x106\\x05\\x00\\x00\\x00')"
assert (gzip.decompress(gzip.compress(b'one') + gzip.compress(b'two'))) == b'onetwo', "gzip.decompress(gzip.compress(b'one') + gzip.compress(b'two'))"
assert (gzip.decompress(gzip.compress(b'padded') + b'\x00\x00\x00')) == b'padded', "gzip.decompress(gzip.compress(b'padded') + b'\\x00\\x00\\x00')"
assert (gzip.decompress(gzip.compress(text, compresslevel=1)) == text) == True, 'gzip.decompress(gzip.compress(text, compresslevel=1)) == text'
assert (gzip.compress(b'abc', mtime=0)[:10]) == b'\x1f\x8b\x08\x00\x00\x00\x00\x00\x02\xff', "gzip.compress(b'abc', mtime=0)[:10]"
assert (gzip.compress(b'abc', 1, mtime=0)[:10]) == b'\x1f\x8b\x08\x00\x00\x00\x00\x00\x04\xff', "gzip.compress(b'abc', 1, mtime=0)[:10]"
assert (gzip.compress(b'abc', 5, mtime=1234)[:10]) == b'\x1f\x8b\x08\x00\xd2\x04\x00\x00\x00\xff', "gzip.compress(b'abc', 5, mtime=1234)[:10]"
assert (gzip.decompress(b'') == b'') == True, "gzip.decompress(b'') == b''"

# === corrupt and truncated streams ===
good = zlib.compress(text)
try:
    zlib.decompress(good[:-3])
    assert False, 'zlib.decompress(good[:-3])'
except zlib.error:
    pass
try:
    zlib.decompress(good[:len(good) // 2])
    assert False, 'zlib.decompress(good[:len(good) // 2])'
except zlib.error:
    pass
try:
    zlib.decompress(b'xyz')
    assert False, "zlib.decompress(b'xyz')"
except zlib.error:
    pass
try:
    zlib.decompress(good[:-1] + bytes([good[-1] ^ 1]))
    assert False, 'zlib.decompress(good[:-1] + bytes([good[-1] ^ 1]))'
except zlib.error:
    pass
messages = []
for data in [good[:-3], b'xyz', good[:-1] + bytes([good[-1] ^ 1])]:
    try:
        zlib.decompress(data)
    except zlib.error as error:
        messages.append(str(error))
assert (messages) == ['Error -5 while decompressing data: incomplete or truncated stream', 'Error -3 while decompressing data: incorrect header check', 'Error -3 while decompressing data: incorrect data check'], 'messages'
packed = gzip.compress(text)
try:
    gzip.decompress(packed[:-3])
    assert False, 'gzip.decompress(packed[:-3])'
except EOFError:
    pass
try:
    gzip.decompress(b'not gzip at all')
    assert False, "gzip.decompress(b'not gzip at all')"
except gzip.BadGzipFile:
    pass
try:
    gzip.decompress(packed + b'xx')
    assert False, "gzip.decompress(packed + b'xx')"
except gzip.BadGzipFile:
    pass
broken = packed[:-8] + bytes([packed[-8] ^ 1]) + packed[-7:]
try:
    gzip.decompress(broken)
    assert False, 'gzip.decompress(broken)'
except gzip.BadGzipFile:
    pass
assert (issubclass(gzip.BadGzipFile, OSError)) == True, 'issubclass(gzip.BadGzipFile, OSError)'
try:
    gzip.decompress(b'nope')
except gzip.BadGzipFile as error:
    message = str(error)
assert (message) == "Not a gzipped file (b'no')", 'message'

# === compressobj and decompressobj ===
compressor = zlib.compressobj(6)
pieces = [compressor.compress(b'part one, '), compressor.compress(b'part two'), compressor.flush()]
assert (zlib.decompress(b''.join(pieces))) == b'part one, part two', "zlib.decompress(b''.join(pieces))"
stream = zlib.compress(text)
decompressor = zlib.decompressobj()
out = b''
for i in range(0, len(stream), 7):
    out += decompressor.decompress(stream[i:i + 7])
out += decompressor.flush()
assert (out == text) == True, 'out == text'
assert (decompressor.eof) == True, 'decompressor.eof'
tail = zlib.decompressobj()
assert (tail.decompress(zlib.compress(b'data') + b'rest')) == b'data', "tail.decompress(zlib.compress(b'data') + b'rest')"
assert (tail.unused_data, tail.eof) == (b'rest', True), 'tail.unused_data, tail.eof'

# === gzip files in the virtual filesystem ===
with gzip.open('notes.txt.gz', 'wt') as handle:
    handle.write('line one\n')
    handle.write('line two\n')
with gzip.open('notes.txt.gz', 'rt') as handle:
    lines = handle.readlines()
assert (lines) == ['line one\n', 'line two\n'], 'lines'
with gzip.open('notes.txt.gz') as handle:
    raw = handle.read()
assert (raw) == b'line one\nline two\n', 'raw'
with gzip.open('notes.txt.gz', 'ab') as handle:
    handle.write(b'line three\n')
with gzip.open('notes.txt.gz', 'rb') as handle:
    appended = [line for line in handle]
assert (appended) == [b'line one\n', b'line two\n', b'line three\n'], 'appended'
with open('notes.txt.gz', 'rb') as handle:
    on_disk = handle.read()
assert (on_disk[:2], gzip.decompress(on_disk)) == (b'\x1f\x8b', b'line one\nline two\nline three\n'), 'on_disk[:2], gzip.decompress(on_disk)'
with gzip.open('data.bin.gz', 'wb') as handle:
    handle.write(bytes(range(256)) * 4)
with gzip.open('data.bin.gz', 'rb') as handle:
    first = handle.read(10)
    rest = handle.read()
assert (first, len(rest)) == (b'\x00\x01\x02\x03\x04\x05\x06\x07\x08\t', 1014), 'first, len(rest)'
try:
    gzip.open('x.gz', 'rbt')
    assert False, "gzip.open('x.gz', 'rbt')"
except ValueError:
    pass
try:
    gzip.open('missing.gz')
    assert False, "gzip.open('missing.gz')"
except FileNotFoundError:
    pass
handle = gzip.GzipFile('data.bin.gz', 'rb')
assert (len(handle.read())) == 1024, 'len(handle.read())'
handle.close()
assert (handle.closed) == True, 'handle.closed'

# === the decompression cap (the sandbox's own, so CPython is not asked) ===
if sys.platform == 'monty':
    bomb = zlib.compress(b'\0' * ((1 << 26) + 1))
    try:
        zlib.decompress(bomb)
        capped = False
    except MemoryError:
        capped = True
    assert capped
