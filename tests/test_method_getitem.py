BASE = object

try:
    import System
    BASE = System.Object
except:
    pass


class T1(BASE):
    def __getitem__(self, key):
        if key == 'answer1':
            return 1
        return super().__getitem__(key)


class T2(T1):
    def __getitem__(self, key):
        if key == 'answer2':
            return 2
        return super().__getitem__(key)


class T3(T2):
    pass


class T4(T3):
    def __getitem__(self, key):
        if key == 'answer4':
            return 4
        return super().__getitem__(key)



def test_method_getattr_self():
    t1 = T1()
    assert t1['answer1'] == 1

    t2 = T2()
    assert t2['answer2'] == 2

    t3 = T3()
    assert t3['answer2'] == 2

    t4 = T4()
    assert t4['answer4'] == 4

    assert t4['answer2'] == 2

    try:
        assert t4['answer3'] == 3
    except AttributeError:
        pass


if __name__ == '__main__':
    test_method_getattr_self()
