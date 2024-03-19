import System
BASE = System.Object
IBASE = System.Collections.IEnumerable


def test_str_overload():
    class S1(BASE):
        def __str__(self):
            s = super().__str__()
            return f"S1 {s}"


    class S2(S1):
        def __str__(self):
            s = super().__str__()
            return f"S2 {s}"

    s1 = S1()
    assert str(s1).startswith('S1 tests.test_method_str')

    s2 = S2()
    assert str(s2).startswith('S2 S1 tests.test_method_str')


def test_str_overload_no_base_overload():
    class NS1(BASE):
        pass


    class NS2(NS1):
        def __str__(self):
            s = super().__str__()
            return f"NS2 {s}"


    ns1 = NS1()
    assert str(ns1).startswith('tests.test_method_str')

    ns2 = NS2()
    assert str(ns2).startswith('NS2 tests.test_method_str')


def test_str_overload_interface():
    class IS1(IBASE):
        def __str__(self):
            s = super().__str__()
            return f"IS1 {s}"


    class IS2(IS1):
        def __str__(self):
            s = super().__str__()
            return f"IS2 {s}"

    is1 = IS1()
    assert str(is1).startswith('IS1 tests.test_method_str')

    is2 = IS2()
    assert str(is2).startswith('IS2 IS1 tests.test_method_str')


def test_str_overload_interface_no_base_overload():
    class INS1(IBASE):
        pass


    class INS2(INS1):
        def __str__(self):
            s = super().__str__()
            return f"INS2 {s}"

    ins1 = INS1()
    assert str(ins1).startswith('tests.test_method_str')

    ins2 = INS2()
    assert str(ins2).startswith('INS2 tests.test_method_str')


def test_str_overload_missing_middle():
    class S1(BASE):
        def __str__(self):
            s = super().__str__()
            return f"S1 {s}"


    class S2(S1):
        pass


    class S3(S2):
        def __str__(self):
            s = super().__str__()
            return f"S3 {s}"


    s1 = S1()
    assert str(s1).startswith('S1 tests.test_method_str')

    s2 = S2()
    assert str(s2).startswith('S1 tests.test_method_str')

    s3 = S3()
    assert str(s3).startswith('S3 S1 tests.test_method_str')