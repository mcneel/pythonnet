import System
BASE = System.Object
IBASE = System.Collections.IEnumerable


def test_instance_get_dict():
    from Python.Test import ClassTest
    class T(ClassTest):
        pass

    t = T()
    assert isinstance(t.__dict__, dict)


def test_instance_get_dict_with_type_field():
    from Python.Test import ClassTest
    from types import MappingProxyType
    class T(ClassTest):
        type_field = 42
        pass

    assert isinstance(T.__dict__, MappingProxyType)
    assert 'type_field' in T.__dict__


def test_instance_get_dict_with_instance_field():
    from Python.Test import ClassTest
    class T(ClassTest):
        def __init__(self):
            self.instance_field = 42

    t = T()
    assert isinstance(t.__dict__, dict)
    assert 'instance_field' in t.__dict__


def test_method_getattribute():
    class S1(BASE):
        def __getattribute__(self, name):
            if name == 'answer1':
                return 1
            return super().__getattribute__(name)


    class S2(S1):
        def __getattribute__(self, name):
            if name == 'answer2':
                return 2
            return super().__getattribute__(name)


    class S3(S2):
        def __getattribute__(self, name):
            if name == 'answer3':
                return 3
            return super().__getattribute__(name)


    s1 = S1()
    assert s1.answer1 == 1
    try:
        assert s1.answer2 == 2
    except AttributeError:
        pass

    s2 = S2()
    assert s2.answer1 == 1
    assert s2.answer2 == 2

    s3 = S3()
    assert s2.answer1 == 1
    assert s2.answer2 == 2
    assert s3.answer3 == 3


def test_method_getattribute_missing_middle():
    class S1(BASE):
        def __getattribute__(self, name):
            if name == 'answer1':
                return 1
            return super().__getattribute__(name)


    class S2(S1):
        pass


    class S3(S2):
        def __getattribute__(self, name):
            if name == 'answer3':
                return 3
            return super().__getattribute__(name)


    s1 = S1()
    assert s1.answer1 == 1
    try:
        assert s1.answer2 == 2
    except AttributeError:
        pass

    s2 = S2()
    assert s2.answer1 == 1
    try:
        assert s2.answer2 == 2
    except AttributeError:
        pass

    s3 = S3()
    assert s2.answer1 == 1
    try:
        assert s3.answer2 == 2
    except AttributeError:
        pass
    assert s3.answer3 == 3
