# Extension: bytearray, the mutable bytes type. Absent upstream.
b = bytearray(b'abc')
b.append(100); b[0] = 65; b.extend(b'xy')
assert b == b'Abcdxy' and b == bytearray(b'Abcdxy') and repr(b) == "bytearray(b'Abcdxy')"
assert b.upper() == bytearray(b'ABCDXY') and type(b.upper()) is bytearray
assert b[1:3] == bytearray(b'bc') and b.pop() == 121 and len(b) == 5
del b[0]; assert bytes(b) == b'bcdx'
b += b'!'; assert b.decode() == 'bcdx!'
assert bytearray(3) == b'\x00\x00\x00' and bytearray.fromhex('0aff') == b'\n\xff'
assert b.split(b'd') == [bytearray(b'bc'), bytearray(b'x!')]
b[1:3] = b'ZZZ'; assert b == bytearray(b'bZZZx!')
assert b'ZZ' in b and 90 in b and list(bytearray(b'ab')) == [97, 98]
