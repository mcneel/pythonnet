from typing import Any

import System


class T1(System.Object):
    def __init__(self, value) -> None:
        self.answer = value

    def __getattr__(self, key):
        if key == 'answer':
            return self.answer
        return super().__getattr__()


class T2(T1):
    def __init__(self, value) -> None:
        super().__init__(value)
        self.answer1 = value + 1

    def __getattr__(self, key):
        if key == 'answer1':
            return self.answer1
        return super().__getattr__()


class T3(System.Object):
    answer = 44

    def __getattr__(self, key):
        if key == 'answer':
            return self.answer
        return super().__getattr__()


class T4(T3):
    answer1 = 45

    def __getattr__(self, key):
        if key == 'answer1':
            return self.answer1
        return super().__getattr__()


def test_method_getattr_self():
    t1 = T1(42)
    assert t1.answer == 42

    t2 = T2(42)
    assert t2.answer1 == 43

    t3 = T3()
    assert t3.answer == 44

    t4 = T4()
    assert t4.answer1 == 45
