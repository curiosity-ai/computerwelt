def kind(v):
    match v:
        case int(n) if n < 0: return 'neg'
        case [x, y]: return 'pair'
        case {'k': k}: return 'map'
        case _: return 'other'
values = [-1, [1, 2], {'k': 1}, 'x'] * 250
out = [kind(v) for v in values]
