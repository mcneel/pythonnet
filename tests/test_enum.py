# -*- coding: utf-8 -*-

"""Test clr enum support."""

import pytest
import Python.Test as Test

from .utils import DictProxyType


def test_enum_standard_attrs():
    """Test standard enum attributes."""
    from System import DayOfWeek

    assert DayOfWeek.__name__ == 'DayOfWeek'
    assert DayOfWeek.__module__ == 'System'
    assert isinstance(DayOfWeek.__dict__, DictProxyType)


def test_enum_get_member():
    """Test access to enum members."""
    from System import DayOfWeek

    assert DayOfWeek.Sunday == DayOfWeek(0)
    assert DayOfWeek.Monday == DayOfWeek(1)
    assert DayOfWeek.Tuesday == DayOfWeek(2)
    assert DayOfWeek.Wednesday == DayOfWeek(3)
    assert DayOfWeek.Thursday == DayOfWeek(4)
    assert DayOfWeek.Friday == DayOfWeek(5)
    assert DayOfWeek.Saturday == DayOfWeek(6)


def test_byte_enum():
    """Test byte enum."""
    assert Test.ByteEnum.Zero == Test.ByteEnum(0)
    assert Test.ByteEnum.One == Test.ByteEnum(1)
    assert Test.ByteEnum.Two == Test.ByteEnum(2)


def test_sbyte_enum():
    """Test sbyte enum."""
    assert Test.SByteEnum.Zero == Test.SByteEnum(0)
    assert Test.SByteEnum.One == Test.SByteEnum(1)
    assert Test.SByteEnum.Two == Test.SByteEnum(2)


def test_short_enum():
    """Test short enum."""
    assert Test.ShortEnum.Zero == Test.ShortEnum(0)
    assert Test.ShortEnum.One == Test.ShortEnum(1)
    assert Test.ShortEnum.Two == Test.ShortEnum(2)


def test_ushort_enum():
    """Test ushort enum."""
    assert Test.UShortEnum.Zero == Test.UShortEnum(0)
    assert Test.UShortEnum.One == Test.UShortEnum(1)
    assert Test.UShortEnum.Two == Test.UShortEnum(2)


def test_int_enum():
    """Test int enum."""
    assert Test.IntEnum.Zero == Test.IntEnum(0)
    assert Test.IntEnum.One == Test.IntEnum(1)
    assert Test.IntEnum.Two == Test.IntEnum(2)


def test_uint_enum():
    """Test uint enum."""
    assert Test.UIntEnum.Zero == Test.UIntEnum(0)
    assert Test.UIntEnum.One == Test.UIntEnum(1)
    assert Test.UIntEnum.Two == Test.UIntEnum(2)


def test_long_enum():
    """Test long enum."""
    assert Test.LongEnum.Zero == Test.LongEnum(0)
    assert Test.LongEnum.One == Test.LongEnum(1)
    assert Test.LongEnum.Two == Test.LongEnum(2)


def test_ulong_enum():
    """Test ulong enum."""
    assert Test.ULongEnum.Zero == Test.ULongEnum(0)
    assert Test.ULongEnum.One == Test.ULongEnum(1)
    assert Test.ULongEnum.Two == Test.ULongEnum(2)


def test_long_enum_to_int():
    assert int(Test.LongEnum.Max) == 9223372036854775807
    assert int(Test.LongEnum.Min) == -9223372036854775808


def test_ulong_enum_to_int():
    assert int(Test.ULongEnum.Max) == 18446744073709551615


def test_instantiate_enum_fails():
    """Test that instantiation of an enum class fails."""
    from System import DayOfWeek

    assert DayOfWeek() == DayOfWeek.Sunday


def test_subclass_enum_fails():
    """Test that subclassing of an enumeration fails."""
    from System import DayOfWeek

    with pytest.raises(TypeError):
        class Boom(DayOfWeek):
            pass

        _ = Boom


def test_enum_set_member_fails():
    """Test that setattr operations on enumerations fail."""
    from System import DayOfWeek

    with pytest.raises(TypeError):
        DayOfWeek.Sunday = 13

    with pytest.raises(TypeError):
        del DayOfWeek.Sunday


def test_enum_undefined_value():
    """Test enumeration conversion with FlagsAttribute set."""
    # This works because the FlagsField enum has FlagsAttribute.
    Test.FieldTest().FlagsField = Test.FlagsEnum(99)

    # This should fail because our test enum doesn't have it.
    with pytest.raises(ValueError):
        Test.FieldTest().EnumField = Test.ShortEnum(20)

    # explicitly permit undefined values
    Test.FieldTest().EnumField = Test.ShortEnum(20, True)

    # second argument must be a boolean
    with pytest.raises(TypeError):
        Test.FieldTest().EnumField = Test.ShortEnum(2, 2)

    # do not allow more than two arguments
    with pytest.raises(TypeError):
        Test.FieldTest().EnumField = Test.ShortEnum(2, 2, 3)


def test_enum_repr():
    """Test enumeration repr."""
    from System import DayOfWeek

    assert repr(DayOfWeek.Monday) == "<DayOfWeek.Monday: 1>"

    assert repr(Test.FlagsEnum(7)) == "<FlagsEnum.Two, Five: 0x00000007>"
    assert repr(Test.FlagsEnum(8)) == "<FlagsEnum.8: 0x00000008>"


def test_enum_conversion():
    """Test enumeration conversion."""
    ob = Test.FieldTest()
    assert ob.EnumField == Test.ShortEnum(0)

    ob.EnumField = Test.ShortEnum.One
    assert ob.EnumField == Test.ShortEnum(1)

    with pytest.raises(OverflowError):
        Test.FieldTest().EnumField = Test.ShortEnum(100000)

    with pytest.raises(TypeError):
        Test.FieldTest().EnumField = "str"

    with pytest.raises(TypeError):
        Test.FieldTest().EnumField = 1


def test_byte_enum_to_int():
    """Test byte enum to int"""
    assert bool(Test.ByteEnum.Zero) == 0
    assert bool(Test.ByteEnum.One) == 1


def test_sbyte_enum_to_int():
    """Test sbyte enum to int"""
    assert bool(Test.SByteEnum.Zero) == 0
    assert bool(Test.SByteEnum.One) == 1


def test_short_enum_to_int():
    """Test short enum to int"""
    assert bool(Test.ShortEnum.Zero) == 0
    assert bool(Test.ShortEnum.One) == 1


def test_ushort_enum_to_int():
    """Test ushort enum to int"""
    assert bool(Test.UShortEnum.Zero) == 0
    assert bool(Test.UShortEnum.One) == 1


def test_int_enum_to_int():
    """Test int enum to int"""
    assert bool(Test.IntEnum.Zero) == 0
    assert bool(Test.IntEnum.One) == 1


def test_uint_enum_to_int():
    """Test uint enum to int"""
    assert bool(Test.UIntEnum.Zero) == 0
    assert bool(Test.UIntEnum.One) == 1


def test_long_enum_to_int():
    """Test long enum to int"""
    assert bool(Test.LongEnum.Zero) == 0
    assert bool(Test.LongEnum.One) == 1


def test_ulong_enum_to_int():
    """Test ulong enum to int"""
    assert bool(Test.ULongEnum.Zero) == 0
    assert bool(Test.ULongEnum.One) == 1


def test_byte_enum_to_bool():
    """Test byte enum to bool"""
    assert bool(Test.ByteEnum.Zero) == False
    assert bool(Test.ByteEnum.One) == True


def test_sbyte_enum_to_bool():
    """Test sbyte enum to bool"""
    assert bool(Test.SByteEnum.Zero) == False
    assert bool(Test.SByteEnum.One) == True


def test_short_enum_to_bool():
    """Test short enum to bool"""
    assert bool(Test.ShortEnum.Zero) == False
    assert bool(Test.ShortEnum.One) == True


def test_ushort_enum_to_bool():
    """Test ushort enum to bool"""
    assert bool(Test.UShortEnum.Zero) == False
    assert bool(Test.UShortEnum.One) == True


def test_int_enum_to_bool():
    """Test int enum to bool"""
    assert bool(Test.IntEnum.Zero) == False
    assert bool(Test.IntEnum.One) == True


def test_uint_enum_to_bool():
    """Test uint enum to bool"""
    assert bool(Test.UIntEnum.Zero) == False
    assert bool(Test.UIntEnum.One) == True


def test_long_enum_to_bool():
    """Test long enum to bool"""
    assert bool(Test.LongEnum.Zero) == False
    assert bool(Test.LongEnum.One) == True


def test_ulong_enum_to_bool():
    """Test ulong enum to bool"""
    assert bool(Test.ULongEnum.Zero) == False
    assert bool(Test.ULongEnum.One) == True

