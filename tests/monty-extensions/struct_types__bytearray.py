# Extension: struct.pack_into / unpack_from over a bytearray, and deleting a
# SimpleNamespace attribute — both need features (bytearray, del obj.attr) this
# port added. Expected values from CPython 3.13.
import struct
from types import SimpleNamespace
buf = bytearray(8)
struct.pack_into('<HI', buf, 1, 513, 7)
assert buf == bytearray(b'\x00\x01\x02\x07\x00\x00\x00\x00')
assert struct.unpack_from('<HI', buf, 1) == (513, 7)
try:
    struct.pack_into('<I', buf, 6, 1)
except struct.error:
    pass
else:
    assert False
ns = SimpleNamespace(a=1, b=2)
del ns.a
assert ns == SimpleNamespace(b=2)
try:
    del ns.zz
except AttributeError:
    pass
else:
    assert False
