import re
log = '\n'.join(f'2026-11-07 12:{i % 60:02d}:00 GET /api/items/{i} 200 {i % 97}ms' for i in range(500))
pattern = re.compile(r'GET (\S+) (\d{3}) (\d+)ms')
slow = [m.group(1) for m in pattern.finditer(log) if int(m.group(3)) > 50]
