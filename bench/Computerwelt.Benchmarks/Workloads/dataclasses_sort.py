from dataclasses import dataclass, field
@dataclass(order=True)
class Item:
    name: str
    qty: int = 0
    tags: list = field(default_factory=list)
items = sorted(Item(f'n{i % 50}', i) for i in range(500))
same = sum(1 for a, b in zip(items, items[1:]) if a == b)
