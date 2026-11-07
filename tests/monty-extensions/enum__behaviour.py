# Extension: enum — Enum, IntEnum, StrEnum, Flag, IntFlag, auto, unique, the
# functional API and members with __init__/properties. Absent upstream.
from enum import Enum, IntEnum, StrEnum, Flag, IntFlag, auto, unique
class Color(Enum):
    RED = 1
    GREEN = 2
    BLUE = 3
    CRIMSON = 1
    def describe(self):
        return f'{self.name}={self.value}'
assert Color.RED.name == 'RED' and Color.RED.value == 1 and Color(2) is Color.GREEN
assert Color['BLUE'] is Color.BLUE and Color.CRIMSON is Color.RED
assert list(Color) == [Color.RED, Color.GREEN, Color.BLUE] and len(Color) == 3
assert repr(Color.RED) == '<Color.RED: 1>' and str(Color.RED) == 'Color.RED' and f'{Color.GREEN}' == 'Color.GREEN'
assert Color.RED.describe() == 'RED=1' and isinstance(Color.RED, Color) and Color.RED in Color
assert repr(Color) == "<enum 'Color'>" and list(Color.__members__) == ['RED', 'GREEN', 'BLUE', 'CRIMSON']
try:
    Color(9)
except ValueError as e:
    assert str(e) == '9 is not a valid Color'
try:
    Color.RED = 5
except AttributeError:
    pass
class Num(IntEnum):
    ONE = 1
    TWO = 2
assert Num.ONE + 1 == 2 and Num.TWO > Num.ONE and Num.ONE == 1 and str(Num.ONE) == '1' and sorted([Num.TWO, Num.ONE]) == [Num.ONE, Num.TWO]
class Mode(StrEnum):
    READ = auto()
    WRITE = auto()
assert Mode.READ == 'read' and Mode.WRITE.upper() == 'WRITE' and str(Mode.READ) == 'read'
class Perm(Flag):
    R = auto()
    W = auto()
    X = auto()
rw = Perm.R | Perm.W
assert Perm.R.value == 1 and Perm.X.value == 4 and rw.value == 3 and Perm.R in rw and Perm.X not in rw
assert repr(rw) == '<Perm.R|W: 3>' and list(rw) == [Perm.R, Perm.W] and bool(Perm(0)) is False
class Bits(IntFlag):
    A = 1
    B = 2
assert Bits.A | Bits.B == 3 and Bits.A | 4 == 5
class Planet(Enum):
    EARTH = (5.97e24, 6.37e6)
    def __init__(self, mass, radius):
        self.mass = mass
    @property
    def heavy(self): return self.mass > 1e24
assert Planet.EARTH.mass == 5.97e24 and Planet.EARTH.heavy and Planet.EARTH.value == (5.97e24, 6.37e6)
try:
    @unique
    class Dup(Enum):
        A = 1
        B = 1
except ValueError as e:
    assert 'B -> A' in str(e)
Animal = Enum('Animal', 'CAT DOG')
assert Animal.DOG.value == 2 and Animal(1).name == 'CAT'
match Color.GREEN:
    case Color.RED: r = 'r'
    case Color.GREEN: r = 'g'
assert r == 'g'
d = {Color.RED: 'x'}
assert d[Color(1)] == 'x'
