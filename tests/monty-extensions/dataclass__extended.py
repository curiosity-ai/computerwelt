# Extension: dataclasses beyond upstream — field(default_factory=...), inherited
# fields, __post_init__, order=, frozen= enforced, __match_args__, recursive
# asdict/astuple, replace(), fields().
from dataclasses import dataclass, field, asdict, astuple, replace, fields, FrozenInstanceError
@dataclass
class Item:
    name: str
    tags: list = field(default_factory=list)
    qty: int = 1
a = Item('x'); b = Item('y'); a.tags.append(1)
assert a.tags == [1] and b.tags == [] and a.qty == 1
@dataclass(order=True, frozen=True)
class P:
    x: int
    y: int = 0
assert P(1) < P(2) and P(1, 2) > P(1, 1) and hash(P(1)) == hash(P(1)) and sorted([P(3), P(1)]) == [P(1), P(3)]
try:
    P(1).x = 3; assert False
except FrozenInstanceError as e:
    assert str(e) == "cannot assign to field 'x'"
@dataclass
class P3(P):
    z: int = 9
assert repr(P3(1, 2)) == 'P3(x=1, y=2, z=9)'
@dataclass
class Box:
    items: list
    def __post_init__(self): self.n = len(self.items)
bx = Box([P(1)])
assert bx.n == 1 and asdict(bx) == {'items': [{'x': 1, 'y': 0}]} and astuple(P(1, 2)) == (1, 2)
assert replace(P(1, 2), y=5) == P(1, 5) and [f.name for f in fields(P3)] == ['x', 'y', 'z']
assert P.__match_args__ == ('x', 'y')
