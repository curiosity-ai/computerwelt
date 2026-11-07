# Extension: `struct` — not in upstream Monty.
# Packing and unpacking with CPython 3.13's sizes, native alignment (a 64-bit little-endian
# Linux host) and error messages. `pack_into` is absent: there is no bytearray to pack into.
# Expected values produced by CPython 3.13.
import struct

# === sizes and alignment ===
assert ([struct.calcsize(f) for f in ['b', 'h', 'i', 'l', 'q', 'n', 'e', 'f', 'd', '?', 'c', 'P']]) == [1, 2, 4, 8, 8, 8, 2, 4, 8, 1, 1, 8], "[struct.calcsize(f) for f in ['b', 'h', 'i', 'l', 'q', 'n', 'e', 'f', 'd', '?', 'c', 'P']]"
assert ([struct.calcsize(f) for f in ['<l', '=l', '>q', '!h', '<e']]) == [4, 4, 8, 2, 2], "[struct.calcsize(f) for f in ['<l', '=l', '>q', '!h', '<e']]"
assert (struct.calcsize('bi'), struct.calcsize('=bi'), struct.calcsize('@bq'), struct.calcsize('ib')) == (8, 5, 16, 5), "struct.calcsize('bi'), struct.calcsize('=bi'), struct.calcsize('@bq'), struct.calcsize('ib')"
assert (struct.calcsize('3s2x4h'), struct.calcsize('10p'), struct.calcsize('0s'), struct.calcsize('i 2h')) == (14, 10, 0, 8), "struct.calcsize('3s2x4h'), struct.calcsize('10p'), struct.calcsize('0s'), struct.calcsize('i 2h')"

# === pack ===
assert (struct.pack('<i', 1)) == b'\x01\x00\x00\x00', "struct.pack('<i', 1)"
assert (struct.pack('>i', 1)) == b'\x00\x00\x00\x01', "struct.pack('>i', 1)"
assert (struct.pack('!H', 0xBEEF)) == b'\xbe\xef', "struct.pack('!H', 0xBEEF)"
assert (struct.pack('<hHbB', -2, 65535, -128, 255)) == b'\xfe\xff\xff\xff\x80\xff', "struct.pack('<hHbB', -2, 65535, -128, 255)"
assert (struct.pack('<qQ', -1, 2**64 - 1)) == b'\xff\xff\xff\xff\xff\xff\xff\xff\xff\xff\xff\xff\xff\xff\xff\xff', "struct.pack('<qQ', -1, 2**64 - 1)"
assert (struct.pack('bi', 1, 2)) == b'\x01\x00\x00\x00\x02\x00\x00\x00', "struct.pack('bi', 1, 2)"
assert (struct.pack('=bi', 1, 2)) == b'\x01\x02\x00\x00\x00', "struct.pack('=bi', 1, 2)"
assert (struct.pack('<?c3s', True, b'x', b'ab')) == b'\x01xab\x00', "struct.pack('<?c3s', True, b'x', b'ab')"
assert (struct.pack('5s', b'hello world')) == b'hello', "struct.pack('5s', b'hello world')"
assert (struct.pack('5p', b'hello world')) == b'\x04hell', "struct.pack('5p', b'hello world')"
assert (struct.pack('<f', 1.5)) == b'\x00\x00\xc0?', "struct.pack('<f', 1.5)"
assert (struct.pack('>d', -0.1)) == b'\xbf\xb9\x99\x99\x99\x99\x99\x9a', "struct.pack('>d', -0.1)"
assert (struct.pack('<e', 0.333)) == b'T5', "struct.pack('<e', 0.333)"
assert (struct.pack('<2x', )) == b'\x00\x00', "struct.pack('<2x', )"
assert (struct.pack('<3h', 1, 2, 3)) == b'\x01\x00\x02\x00\x03\x00', "struct.pack('<3h', 1, 2, 3)"
assert (struct.pack('<d', 3)) == b'\x00\x00\x00\x00\x00\x00\x08@', "struct.pack('<d', 3)"

# === unpack ===
assert (struct.unpack('<i', b'\x01\x00\x00\x00')) == (1,), "struct.unpack('<i', b'\\x01\\x00\\x00\\x00')"
assert (struct.unpack('>i', b'\xff\xff\xff\xfe')) == (-2,), "struct.unpack('>i', b'\\xff\\xff\\xff\\xfe')"
assert (struct.unpack('<hHbB', b'\xfe\xff\xff\xff\x80\xff')) == (-2, 65535, -128, 255), "struct.unpack('<hHbB', b'\\xfe\\xff\\xff\\xff\\x80\\xff')"
assert (struct.unpack('<f', struct.pack('<f', 0.1))) == (0.10000000149011612,), "struct.unpack('<f', struct.pack('<f', 0.1))"
assert (struct.unpack('<e', struct.pack('<e', 0.333))) == (0.3330078125,), "struct.unpack('<e', struct.pack('<e', 0.333))"
assert (struct.unpack('<d', struct.pack('<d', 2.5))) == (2.5,), "struct.unpack('<d', struct.pack('<d', 2.5))"
assert (struct.unpack('<?c3s', b'\x01xab\x00')) == (True, b'x', b'ab\x00'), "struct.unpack('<?c3s', b'\\x01xab\\x00')"
assert (struct.unpack('5p', b'\x03abcd')) == (b'abc',), "struct.unpack('5p', b'\\x03abcd')"
assert (struct.unpack('bi', struct.pack('bi', -1, 70000))) == (-1, 70000), "struct.unpack('bi', struct.pack('bi', -1, 70000))"
assert (struct.unpack_from('<h', b'\x00\x01\x02\x03', 2)) == (770,), "struct.unpack_from('<h', b'\\x00\\x01\\x02\\x03', 2)"
assert (struct.unpack_from('<h', b'\x00\x01\x02\x03', offset=1)) == (513,), "struct.unpack_from('<h', b'\\x00\\x01\\x02\\x03', offset=1)"
assert (struct.unpack_from('<h', b'\x00\x01\x02\x03', -2)) == (770,), "struct.unpack_from('<h', b'\\x00\\x01\\x02\\x03', -2)"
assert (list(struct.iter_unpack('<hb', b'\x01\x00\x02\x03\x00\x04'))) == [(1, 2), (3, 4)], "list(struct.iter_unpack('<hb', b'\\x01\\x00\\x02\\x03\\x00\\x04'))"

# === Struct objects ===
record = struct.Struct('<I2sf')
assert (record.size, record.format) == (10, '<I2sf'), 'record.size, record.format'
assert (record.pack(7, b'ok', 0.5)) == b'\x07\x00\x00\x00ok\x00\x00\x00?', "record.pack(7, b'ok', 0.5)"
assert (record.unpack(record.pack(7, b'ok', 0.5))) == (7, b'ok', 0.5), "record.unpack(record.pack(7, b'ok', 0.5))"
assert (record.unpack_from(b'xx' + record.pack(1, b'ab', 2.0), 2)) == (1, b'ab', 2.0), "record.unpack_from(b'xx' + record.pack(1, b'ab', 2.0), 2)"
assert (list(struct.Struct('<B').iter_unpack(b'\x01\x02\x03'))) == [(1,), (2,), (3,)], "list(struct.Struct('<B').iter_unpack(b'\\x01\\x02\\x03'))"

# === errors ===
try:
    struct.pack('b', 200)
    assert False, "struct.pack('b', 200)"
except struct.error:
    pass
try:
    struct.pack('<L', -1)
    assert False, "struct.pack('<L', -1)"
except struct.error:
    pass
try:
    struct.pack('B', 1.5)
    assert False, "struct.pack('B', 1.5)"
except struct.error:
    pass
try:
    struct.pack('c', b'ab')
    assert False, "struct.pack('c', b'ab')"
except struct.error:
    pass
try:
    struct.pack('s', 'text')
    assert False, "struct.pack('s', 'text')"
except struct.error:
    pass
try:
    struct.pack('<ii', 1)
    assert False, "struct.pack('<ii', 1)"
except struct.error:
    pass
try:
    struct.calcsize('z')
    assert False, "struct.calcsize('z')"
except struct.error:
    pass
try:
    struct.calcsize('<n')
    assert False, "struct.calcsize('<n')"
except struct.error:
    pass
try:
    struct.unpack('<i', b'abc')
    assert False, "struct.unpack('<i', b'abc')"
except struct.error:
    pass
try:
    struct.unpack_from('<i', b'abcdef', 4)
    assert False, "struct.unpack_from('<i', b'abcdef', 4)"
except struct.error:
    pass
try:
    list(struct.iter_unpack('<i', b'abcdef'))
    assert False, "list(struct.iter_unpack('<i', b'abcdef'))"
except struct.error:
    pass
try:
    struct.pack('<f', 1e300)
    assert False, "struct.pack('<f', 1e300)"
except OverflowError:
    pass
for spec, value in [('b', 200), ('<L', -1), ('B', 1.5), ('c', b'ab'), ('s', 'x'), ('q', 2**63)]:
    try:
        struct.pack(spec, value)
    except struct.error as error:
        last = str(error)
messages = []
for spec, value in [('b', 200), ('<L', -1), ('B', 1.5), ('c', b'ab'), ('s', 'x'), ('q', 2**63), ('>H', 70000)]:
    try:
        struct.pack(spec, value)
    except struct.error as error:
        messages.append(str(error))
assert (messages) == ["'b' format requires -128 <= number <= 127", "'L' format requires 0 <= number <= 4294967295", 'required argument is not an integer', 'char format requires a bytes object of length 1', "argument for 's' must be a bytes object", "'q' format requires -9223372036854775808 <= number <= 9223372036854775807", "'H' format requires 0 <= number <= 65535"], 'messages'
messages = []
for call in [lambda: struct.unpack('<i', b'abc'), lambda: struct.unpack_from('<i', b'abcdef', 4),
             lambda: list(struct.iter_unpack('<i', b'abcdef')), lambda: struct.pack('<ii', 1),
             lambda: struct.calcsize('3i4'), lambda: struct.calcsize('z'),
             lambda: struct.unpack_from('<i', b'abcdef', -10)]:
    try:
        call()
    except struct.error as error:
        messages.append(str(error))
assert (messages) == ['unpack requires a buffer of 4 bytes', 'unpack_from requires a buffer of at least 8 bytes for unpacking 4 bytes at offset 4 (actual buffer size is 6)', 'iterative unpacking requires a buffer of a multiple of 4 bytes', 'pack expected 2 items for packing (got 1)', 'repeat count given without format specifier', 'bad char in struct format', 'offset -10 out of range for 6-byte buffer'], 'messages'
