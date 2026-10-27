# Extension: `hashlib`, `hmac`, `base64`, `binascii` and `uuid`. Upstream Monty has none.
# Expected output is CPython 3.13's.
_lines = []

def _log(*parts):
    _lines.append(' '.join(str(p) for p in parts))


import hashlib, hmac, base64, binascii, uuid
for name in ('md5', 'sha1', 'sha256', 'sha384', 'sha512', 'sha3_256'):
    h = getattr(hashlib, name)(b'hello'); h.update(b' world')
    _log(name, h.hexdigest(), h.digest_size, h.block_size, h.name, h.digest() == bytes.fromhex(h.hexdigest()))
_log(hashlib.new('sha256', b'abc').hexdigest(), hashlib.sha256(string=b'x').hexdigest() if False else '', hashlib.md5().hexdigest())
c = hashlib.sha256(b'a'); d = c.copy(); d.update(b'b'); _log(c.hexdigest()[:8], d.hexdigest()[:8])
try: hashlib.sha256('text')
except TypeError as e: _log(e)
try: hashlib.new('nope')
except ValueError as e: _log(e)
_log(hashlib.pbkdf2_hmac('sha256', b'pw', b'salt', 1000).hex(), hashlib.pbkdf2_hmac('sha1', b'pw', b'salt', 2, 10).hex(), 'sha256' in hashlib.algorithms_guaranteed)
m = hmac.new(b'key', b'msg', hashlib.sha256); _log(m.hexdigest(), m.name, m.digest_size, hmac.new(b'key', digestmod='sha1').hexdigest(), hmac.digest(b'k', b'm', 'md5').hex())
m2 = hmac.new(b'key', digestmod='sha256'); m2.update(b'ms'); m2.update(b'g'); _log(m2.hexdigest() == m.hexdigest(), hmac.compare_digest(b'abc', b'abc'), hmac.compare_digest('abc', 'abd'), hmac.compare_digest(m.digest(), m2.digest()))
try: hmac.new(b'k', b'm')
except TypeError as e: _log(e)
for data in (b'', b'f', b'fo', b'foo', b'foob', b'fooba', b'foobar', bytes(range(256))[:50]):
    e64 = base64.b64encode(data); _log(e64, base64.b64decode(e64) == data, base64.urlsafe_b64encode(data), base64.b32encode(data), base64.b32decode(base64.b32encode(data)) == data, base64.b16encode(data), base64.b16decode(base64.b16encode(data)) == data)
_log(base64.b64encode(b'\xfb\xff\xfe'), base64.urlsafe_b64encode(b'\xfb\xff\xfe'), base64.b64encode(b'\xfb\xff', altchars=b'-_'), base64.urlsafe_b64decode('-_8='), base64.standard_b64decode(b'aGk='))
_log(base64.encodebytes(b'x' * 100), base64.decodebytes(base64.encodebytes(b'x' * 100)) == b'x' * 100)
for bad in ('abc', 'a', 'YQ=', 'YWJj$', 'Y', '!!!!', 'YQ==YQ==', 'YW Jj'):
    try: _log(repr(bad), base64.b64decode(bad))
    except Exception as e: _log(repr(bad), type(e).__name__, e)
try: base64.b64decode('YWJj$', validate=True)
except binascii.Error as e: _log('validate', e)
try: base64.b64decode('é')
except ValueError as e: _log(e)
try: base64.b64encode('s')
except TypeError as e: _log(e)
_log(base64.b16decode(b'FF00'), base64.b16decode('ab', casefold=True), base64.b32decode('MZXW6===') , base64.b32decode('mzxw6===', casefold=True))
for bad in (lambda: base64.b16decode('ab'), lambda: base64.b32decode('MZXW6'), lambda: base64.b16decode('zz')):
    try: bad()
    except Exception as e: _log(type(e).__name__, e)
_log(binascii.hexlify(b'\x00\xffab'), binascii.unhexlify('00ff6162'), binascii.b2a_hex(b'ab'), binascii.a2b_hex(b'6162'), binascii.crc32(b'hello'), binascii.crc32(b'world', binascii.crc32(b'hello ')), binascii.b2a_base64(b'hi'), binascii.b2a_base64(b'hi', newline=False), binascii.a2b_base64('aGk='), binascii.hexlify(b'abc', b':'))
for bad in ('abc', 'zz'):
    try: binascii.unhexlify(bad)
    except binascii.Error as e: _log(e)
_log(issubclass(binascii.Error, ValueError))
u = uuid.UUID('12345678-1234-5678-1234-567812345678')
_log(u, repr(u), u.hex, u.int, u.version, u.urn, u.bytes, u.variant, str(u) == '12345678-1234-5678-1234-567812345678')
_log(uuid.UUID(int=0), uuid.UUID(bytes=bytes(range(16))), uuid.UUID('{12345678123456781234567812345678}'), uuid.UUID(hex='urn:uuid:12345678-1234-5678-1234-567812345678') == u, u == uuid.UUID(int=u.int), hash(u) == hash(uuid.UUID(int=u.int)), u < uuid.UUID(int=u.int + 1))
_log(uuid.uuid3(uuid.NAMESPACE_DNS, 'python.org'), uuid.uuid5(uuid.NAMESPACE_DNS, 'python.org'), uuid.uuid3(uuid.NAMESPACE_URL, b'x') if False else '', uuid.uuid5(uuid.NAMESPACE_URL, 'http://x').version)
v4 = uuid.uuid4(); _log(v4.version, v4.variant, len(str(v4)), v4 != uuid.uuid4(), isinstance(v4, uuid.UUID), uuid.UUID(str(v4)) == v4, uuid.UUID(version=4, int=0) if False else '')
_log(uuid.NAMESPACE_DNS)
try: uuid.UUID('zz')
except ValueError as e: _log(e)
try: uuid.UUID()
except TypeError as e: _log(e)


# === compare against what CPython printed ===
EXPECTED = ['md5 5eb63bbbe01eeed093cb22bb8f5acdc3 16 64 md5 True', 'sha1 2aae6c35c94fcfb415dbe95f408b9ce91ee846ed 20 64 sha1 True', 'sha256 b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9 32 64 sha256 True', 'sha384 fdbd8e75a67f29f701a4e040385e2e23986303ea10239211af907fcbb83578b3e417cb71ce646efd0819dd8c088de1bd 48 128 sha384 True', 'sha512 309ecc489c12d6eb4cc40f50c902f2b4d0ed77ee511a7c7a9bcd3ca86d4cd86f989dd35bc5ff499670da34255b45b0cfd830e81f605dcf7dc5542e93ae9cd76f 64 128 sha512 True', 'sha3_256 644bcc7e564373040999aac89e7622f3ca71fba1d972fd94a31c3bfbf24e3938 32 136 sha3_256 True', 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad  d41d8cd98f00b204e9800998ecf8427e', 'ca978112 fb8e20fc', 'Strings must be encoded before hashing', 'unsupported hash type nope', '0a38253555ce37f5c72a6b703f996814ebf241f203af146e93dcdeb031c5567e 133a0b823b029801576d True', '2d93cbc1be167bcb1637a4a23cbff01a7878f0c50ee833954ea5221bb1b8c628 hmac-sha256 32 f42bb0eeb018ebbd4597ae7213711ec60760843f ed7e724d3a91554aaa2043041d9c5305', 'True True False True', "Missing required argument 'digestmod'.", "b'' True b'' b'' True b'' True", "b'Zg==' True b'Zg==' b'MY======' True b'66' True", "b'Zm8=' True b'Zm8=' b'MZXQ====' True b'666F' True", "b'Zm9v' True b'Zm9v' b'MZXW6===' True b'666F6F' True", "b'Zm9vYg==' True b'Zm9vYg==' b'MZXW6YQ=' True b'666F6F62' True", "b'Zm9vYmE=' True b'Zm9vYmE=' b'MZXW6YTB' True b'666F6F6261' True", "b'Zm9vYmFy' True b'Zm9vYmFy' b'MZXW6YTBOI======' True b'666F6F626172' True", "b'AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8gISIjJCUmJygpKissLS4vMDE=' True b'AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8gISIjJCUmJygpKissLS4vMDE=' b'AAAQEAYEAUDAOCAJBIFQYDIOB4IBCEQTCQKRMFYYDENBWHA5DYPSAIJCEMSCKJRHFAUSUKZMFUXC6MBR' True b'000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F202122232425262728292A2B2C2D2E2F3031' True", "b'+//+' b'-__-' b'-_8=' b'\\xfb\\xff' b'hi'", "b'eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4\\neHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eA==\\n' True", "'abc' Error Incorrect padding", "'a' Error Invalid base64-encoded string: number of data characters (1) cannot be 1 more than a multiple of 4", "'YQ=' Error Incorrect padding", "'YWJj$' b'abc'", "'Y' Error Invalid base64-encoded string: number of data characters (1) cannot be 1 more than a multiple of 4", "'!!!!' b''", "'YQ==YQ==' b'a\\x06\\x10'", "'YW Jj' b'abc'", 'validate Only base64 data is allowed', 'string argument should contain only ASCII characters', "a bytes-like object is required, not 'str'", "b'\\xff\\x00' b'\\xab' b'foo' b'foo'", 'Error Non-base16 digit found', 'Error Incorrect padding', 'Error Non-base16 digit found', "b'00ff6162' b'\\x00\\xffab' b'6162' b'ab' 907060870 222957957 b'aGk=\\n' b'aGk=' b'hi' b'61:62:63'", 'Odd-length string', 'Non-hexadecimal digit found', 'True', "12345678-1234-5678-1234-567812345678 UUID('12345678-1234-5678-1234-567812345678') 12345678123456781234567812345678 24197857161011715162171839636988778104 None urn:uuid:12345678-1234-5678-1234-567812345678 b'\\x124Vx\\x124Vx\\x124Vx\\x124Vx' reserved for NCS compatibility True", '00000000-0000-0000-0000-000000000000 00010203-0405-0607-0809-0a0b0c0d0e0f 12345678-1234-5678-1234-567812345678 True True True True', '6fa459ea-ee8a-3ca4-894e-db77e160355e 886313e1-3b8a-5372-9b90-0c9aee199e5d  5', '4 specified in RFC 4122 36 True True True ', '6ba7b810-9dad-11d1-80b4-00c04fd430c8', 'badly formed hexadecimal UUID string', 'one of the hex, bytes, bytes_le, fields, or int arguments must be given']

assert len(_lines) == len(EXPECTED), (len(_lines), len(EXPECTED))
for got, want in zip(_lines, EXPECTED):
    assert got == want, (got, want)
