import json
records = [{'id': i, 'name': f'user{i}', 'tags': ['a', 'b'], 'score': i * 0.5} for i in range(300)]
text = json.dumps(records)
back = json.loads(text)
assert back[10]['name'] == 'user10'
