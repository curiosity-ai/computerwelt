# Extension: abc (ABC, abstractmethod, enforced at instantiation) and
# collections.abc (structural isinstance checks). Absent upstream.
from abc import ABC, abstractmethod
class Shape(ABC):
    @abstractmethod
    def area(self): ...
    def describe(self): return f'area {self.area()}'
class Sq(Shape):
    def __init__(self, s): self.s = s
    def area(self): return self.s ** 2
try:
    Shape(); assert False
except TypeError as e:
    assert "Can't instantiate abstract class Shape without an implementation for abstract method 'area'" == str(e), str(e)
assert Sq(3).describe() == 'area 9'
from collections.abc import Mapping, Iterable, Sequence, Callable
assert isinstance({}, Mapping) and isinstance([], Sequence) and not isinstance('x', Mapping) and isinstance(len, Callable)
class Bag:
    def __iter__(self): return iter([])
assert isinstance(Bag(), Iterable)
