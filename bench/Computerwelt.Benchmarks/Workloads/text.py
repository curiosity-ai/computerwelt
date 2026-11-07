lines = [f'item {i} value {i * 7 % 31}' for i in range(500)]
counts = {}
for line in lines:
    key = line.split(' ')[3]
    counts[key] = counts.get(key, 0) + 1
total = sum(counts.values())
top = sorted(counts.items(), key=lambda kv: -kv[1])[:5]
