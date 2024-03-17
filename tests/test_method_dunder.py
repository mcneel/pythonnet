import System
from System.Collections import IEnumerable, IEnumerator


class CustomIterator(IEnumerator):
    def __init__(self, enumerable):
        self.enumerable = enumerable
        self.position = -1

    def get_Current(self):
        if self.position == -1: return None
        if self.position >= len(self.enumerable): return None
        return self.enumerable[self.position]

    def MoveNext(self):
        self.position += 1
        return self.position < len(self.enumerable)

    def Reset(self):
        self.position = -1


class CustomEnumerable(IEnumerable):
    def __init__(self, values, attributes):
        self.values = values
        self.myattributes = attributes

    def __getitem__(self, key):
        return self.values[key]

    def __len__(self):
        return len(self.values)

    def __iter__(self):
       for a in self.myattributes:
          yield a

    def GetEnumerator(self):
        return CustomIterator(self)


def test_custom_enumerable():
    e = CustomEnumerable([1, 2, 3], ['a', 'b'])

    assert len(e) == 3

    assert e[0] == 1
    assert e[1] == 2
    assert e[2] == 3

    l = list([x for x in e])
    assert len(l) == 2
    assert l[0] == 'a'
    assert l[1] == 'b'


def test_custom_enumerator():
    e = CustomIterator([1, 2, 3])
    l = list([x for x in e])
    assert len(l) == 3
    assert l[0] == 1
    assert l[1] == 2
    assert l[2] == 3


def test_custom_enumerable_getEnumerator():
    e = CustomEnumerable([1, 2, 3], ['a', 'b'])

    l = list(e.GetEnumerator())
    assert len(l) == 3
    assert l[0] == 1
    assert l[1] == 2
    assert l[2] == 3
