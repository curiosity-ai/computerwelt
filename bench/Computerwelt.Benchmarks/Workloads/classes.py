class Base:
    def __init__(self, x):
        self.x = x
    def get(self):
        return self.x
class Point(Base):
    def __init__(self, x, y):
        super().__init__(x)
        self.y = y
    @property
    def total(self):
        return self.get() + self.y
points = [Point(i, i) for i in range(500)]
s = 0
for _ in range(4):
    for p in points:
        s += p.total
