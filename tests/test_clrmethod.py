# -*- coding: utf-8 -*-

"""Test clrmethod and clrproperty support for calling methods and getting/setting python properties from CLR."""

import Python.Test as Test  # type: ignore
import System  # type: ignore
import clr  # type: ignore


class ExamplePythonType:
    def __init__(self, value):
        self.value = value

    def __eq__(self, other):
        if isinstance(other, self.__class__):
            return self.value == other.value
        return False


class ExamplePythonClrType(System.Object):
    __namespace__ = "PyTest"

    def __init__(self, value):
        self.value = value

    # TODO: implement support for auto-implementing __eq__
    def Equals(self, other):
        if isinstance(other, self.__class__):
            return self.value == other.value
        return False


class ExampleClrClass(System.Object):
    __namespace__ = "PyTest"

    def __init__(self):
        self._str_x = "3"
        self._int_x = 3
        self._float_x = 3.0
        self._bool_x = True
        self._epyt_x = ExamplePythonType(3)
        self._epytclr_x = ExamplePythonClrType(3)

    # region: CLR Property/Method of type str (System.String)
    def get_STR_X(self):
        return self._str_x

    def set_STR_X(self, value):
        self._str_x = value

    STR_X = clr.clrproperty(str, get_STR_X, set_STR_X)

    @clr.clrproperty(str)
    def STR_Y(self):
        return self._str_x * 2

    @clr.clrmethod(str, [str])
    def test_str(self, x):
        return x * 2

    # endregion

    # region: CLR Property/Method of type int (System.Int32)
    def get_INT_X(self):
        return self._int_x

    def set_INT_X(self, value):
        self._int_x = value

    INT_X = clr.clrproperty(int, get_INT_X, set_INT_X)

    @clr.clrproperty(int)
    def INT_Y(self):
        return self._int_x * 2

    @clr.clrmethod(int, [int])
    def test_int(self, x):
        return x * 2

    # endregion

    # region: CLR Property/Method of type float (System.Double)
    def get_FLOAT_X(self):
        return self._float_x

    def set_FLOAT_X(self, value):
        self._float_x = value

    FLOAT_X = clr.clrproperty(float, get_FLOAT_X, set_FLOAT_X)

    @clr.clrproperty(float)
    def FLOAT_Y(self):
        return self._float_x * 2

    @clr.clrmethod(float, [float])
    def test_float(self, x):
        return x * 2

    # endregion

    # region: CLR Property/Method of type bool (System.Boolean)
    def get_BOOL_X(self):
        return self._bool_x

    def set_BOOL_X(self, value):
        self._bool_x = value

    BOOL_X = clr.clrproperty(bool, get_BOOL_X, set_BOOL_X)

    @clr.clrproperty(bool)
    def BOOL_Y(self):
        return not self._bool_x

    @clr.clrmethod(bool, [bool])
    def test_bool(self, x):
        return not x

    # endregion

    # region: CLR Property/Method of type ExamplePythonType
    def get_EPYT_X(self):
        return self._epyt_x

    def set_EPYT_X(self, value):
        self._epyt_x = value

    EPYT_X = clr.clrproperty(ExamplePythonType, get_EPYT_X, set_EPYT_X)

    @clr.clrproperty(ExamplePythonType)
    def EPYT_Y(self):
        return ExamplePythonType(self._epyt_x.value * 2)

    @clr.clrmethod(ExamplePythonType, [ExamplePythonType])
    def test_epyt(self, x):
        return ExamplePythonType(x.value * 2)

    # endregion

    # region: CLR Property/Method of type ExamplePythonClrType
    def get_EPYTCLR_X(self):
        return self._epytclr_x

    def set_EPYTCLR_X(self, value):
        self._epytclr_x = value

    EPYTCLR_X = clr.clrproperty(ExamplePythonClrType, get_EPYTCLR_X, set_EPYTCLR_X)

    @clr.clrproperty(ExamplePythonClrType)
    def EPYTCLR_Y(self):
        return ExamplePythonClrType(self._epytclr_x.value * 2)

    @clr.clrmethod(ExamplePythonClrType, [ExamplePythonClrType])
    def test_epytclr(self, x):
        return ExamplePythonClrType(x.value * 2)

    # endregion

    # TODO: Numeric Types:  complex
    # TODO: Sequence Types: list, tuple, range
    # TODO: Mapping Type:   dict
    # TODO: Set Types:      set, frozenset
    # TODO: Binary Types:   bytes, bytearray, memoryview


def test_set_and_get_property_from_py():
    """Test setting and getting clr-accessible properties from python."""
    t = ExampleClrClass()

    assert t.STR_X == "3"
    assert t.STR_Y == "3" * 2
    t.STR_X = "4"
    assert t.STR_X == "4"
    assert t.STR_Y == "4" * 2

    assert t.INT_X == 3
    assert t.INT_Y == 3 * 2
    t.INT_X = 4
    assert t.INT_X == 4
    assert t.INT_Y == 4 * 2

    assert t.FLOAT_X == 3.0
    assert t.FLOAT_Y == 3.0 * 2
    t.FLOAT_X = 4.0
    assert t.FLOAT_X == 4.0
    assert t.FLOAT_Y == 4.0 * 2

    assert t.BOOL_X == True
    assert t.BOOL_Y == False
    t.BOOL_X = True
    assert t.BOOL_X == True
    assert t.BOOL_Y == False

    assert t.EPYT_X == ExamplePythonType(3)
    assert t.EPYT_Y == ExamplePythonType(6)
    t.EPYT_X = ExamplePythonType(4)
    assert t.EPYT_X == ExamplePythonType(4)
    assert t.EPYT_Y == ExamplePythonType(8)

    assert t.EPYTCLR_X == ExamplePythonClrType(3)
    assert t.EPYTCLR_Y == ExamplePythonClrType(6)
    t.EPYTCLR_X = ExamplePythonClrType(4)
    assert t.EPYTCLR_X == ExamplePythonClrType(4)
    assert t.EPYTCLR_Y == ExamplePythonClrType(8)


def test_set_and_get_property_from_clr():
    """Test setting and getting clr-accessible properties from the clr."""
    t = ExampleClrClass()

    assert t.GetType().GetProperty("STR_X").GetValue(t) == "3"
    assert t.GetType().GetProperty("STR_Y").GetValue(t) == "3" * 2
    t.GetType().GetProperty("STR_X").SetValue(t, "4")
    assert t.GetType().GetProperty("STR_X").GetValue(t) == "4"
    assert t.GetType().GetProperty("STR_Y").GetValue(t) == "4" * 2

    assert t.GetType().GetProperty("INT_X").GetValue(t) == 3
    assert t.GetType().GetProperty("INT_Y").GetValue(t) == 3 * 2
    t.GetType().GetProperty("INT_X").SetValue(t, 4)
    assert t.GetType().GetProperty("INT_X").GetValue(t) == 4
    assert t.GetType().GetProperty("INT_Y").GetValue(t) == 4 * 2

    assert t.GetType().GetProperty("FLOAT_X").GetValue(t) == 3.0
    assert t.GetType().GetProperty("FLOAT_Y").GetValue(t) == 3.0 * 2
    t.GetType().GetProperty("FLOAT_X").SetValue(t, 4.0)
    assert t.GetType().GetProperty("FLOAT_X").GetValue(t) == 4.0
    assert t.GetType().GetProperty("FLOAT_Y").GetValue(t) == 4.0 * 2

    assert t.GetType().GetProperty("BOOL_X").GetValue(t) == True
    assert t.GetType().GetProperty("BOOL_Y").GetValue(t) == False
    t.GetType().GetProperty("BOOL_X").SetValue(t, False)
    assert t.GetType().GetProperty("BOOL_X").GetValue(t) == False
    assert t.GetType().GetProperty("BOOL_Y").GetValue(t) == True

    assert t.GetType().GetProperty("EPYT_X").GetValue(t) == ExamplePythonType(3)
    assert t.GetType().GetProperty("EPYT_Y").GetValue(t) == ExamplePythonType(6)
    t.GetType().GetProperty("EPYT_X").SetValue(t, ExamplePythonType(4))
    assert t.GetType().GetProperty("EPYT_X").GetValue(t) == ExamplePythonType(4)
    assert t.GetType().GetProperty("EPYT_Y").GetValue(t) == ExamplePythonType(8)

    assert t.GetType().GetProperty("EPYTCLR_X").GetValue(t) == ExamplePythonClrType(3)
    assert t.GetType().GetProperty("EPYTCLR_Y").GetValue(t) == ExamplePythonClrType(6)
    t.GetType().GetProperty("EPYTCLR_X").SetValue(t, ExamplePythonClrType(4))
    assert t.GetType().GetProperty("EPYTCLR_X").GetValue(t) == ExamplePythonClrType(4)
    assert t.GetType().GetProperty("EPYTCLR_Y").GetValue(t) == ExamplePythonClrType(8)


def test_set_and_get_property_from_clr_and_py():
    """Test setting and getting clr-accessible properties alternatingly from the clr and from python."""
    t = ExampleClrClass()

    assert t.GetType().GetProperty("STR_X").GetValue(t) == "3"
    assert t.GetType().GetProperty("STR_Y").GetValue(t) == "3" * 2
    assert t.STR_X == "3"
    assert t.STR_Y == "3" * 2
    t.GetType().GetProperty("STR_X").SetValue(t, "4")
    assert t.GetType().GetProperty("STR_X").GetValue(t) == "4"
    assert t.GetType().GetProperty("STR_Y").GetValue(t) == "4" * 2
    assert t.STR_X == "4"
    assert t.STR_Y == "4" * 2
    t.STR_X = "5"
    assert t.GetType().GetProperty("STR_X").GetValue(t) == "5"
    assert t.GetType().GetProperty("STR_Y").GetValue(t) == "5" * 2
    assert t.STR_X == "5"
    assert t.STR_Y == "5" * 2

    assert t.GetType().GetProperty("INT_X").GetValue(t) == 3
    assert t.GetType().GetProperty("INT_Y").GetValue(t) == 3 * 2
    assert t.INT_X == 3
    assert t.INT_Y == 3 * 2
    t.GetType().GetProperty("INT_X").SetValue(t, 4)
    assert t.GetType().GetProperty("INT_X").GetValue(t) == 4
    assert t.GetType().GetProperty("INT_Y").GetValue(t) == 4 * 2
    assert t.INT_X == 4
    assert t.INT_Y == 4 * 2
    t.INT_X = 5
    assert t.GetType().GetProperty("INT_X").GetValue(t) == 5
    assert t.GetType().GetProperty("INT_Y").GetValue(t) == 5 * 2
    assert t.INT_X == 5
    assert t.INT_Y == 5 * 2

    assert t.GetType().GetProperty("FLOAT_X").GetValue(t) == 3.0
    assert t.GetType().GetProperty("FLOAT_Y").GetValue(t) == 3.0 * 2
    assert t.FLOAT_X == 3.0
    assert t.FLOAT_Y == 3.0 * 2
    t.GetType().GetProperty("FLOAT_X").SetValue(t, 4.0)
    assert t.GetType().GetProperty("FLOAT_X").GetValue(t) == 4.0
    assert t.GetType().GetProperty("FLOAT_Y").GetValue(t) == 4.0 * 2
    assert t.FLOAT_X == 4.0
    assert t.FLOAT_Y == 4.0 * 2
    t.FLOAT_X = 5.0
    assert t.GetType().GetProperty("FLOAT_X").GetValue(t) == 5.0
    assert t.GetType().GetProperty("FLOAT_Y").GetValue(t) == 5.0 * 2
    assert t.FLOAT_X == 5.0
    assert t.FLOAT_Y == 5.0 * 2

    assert t.GetType().GetProperty("BOOL_X").GetValue(t) == True
    assert t.GetType().GetProperty("BOOL_Y").GetValue(t) == False
    assert t.BOOL_X == True
    assert t.BOOL_Y == False
    t.GetType().GetProperty("BOOL_X").SetValue(t, True)
    assert t.GetType().GetProperty("BOOL_X").GetValue(t) == True
    assert t.GetType().GetProperty("BOOL_Y").GetValue(t) == False

    assert t.GetType().GetProperty("EPYT_X").GetValue(t) == ExamplePythonType(3)
    assert t.GetType().GetProperty("EPYT_Y").GetValue(t) == ExamplePythonType(6)
    assert t.EPYT_X == ExamplePythonType(3)
    assert t.EPYT_Y == ExamplePythonType(6)
    t.GetType().GetProperty("EPYT_X").SetValue(t, ExamplePythonType(4))
    assert t.GetType().GetProperty("EPYT_X").GetValue(t) == ExamplePythonType(4)
    assert t.GetType().GetProperty("EPYT_Y").GetValue(t) == ExamplePythonType(8)

    assert t.GetType().GetProperty("EPYTCLR_X").GetValue(t) == ExamplePythonClrType(3)
    assert t.GetType().GetProperty("EPYTCLR_Y").GetValue(t) == ExamplePythonClrType(6)
    assert t.EPYTCLR_X == ExamplePythonClrType(3)
    assert t.EPYTCLR_Y == ExamplePythonClrType(6)
    t.GetType().GetProperty("EPYTCLR_X").SetValue(t, ExamplePythonClrType(4))
    assert t.GetType().GetProperty("EPYTCLR_X").GetValue(t) == ExamplePythonClrType(4)
    assert t.GetType().GetProperty("EPYTCLR_Y").GetValue(t) == ExamplePythonClrType(8)


def test_method_invocation_from_py():
    """Test calling a clr-accessible method from python."""
    t = ExampleClrClass()

    assert t.test_str("41") == "41" * 2

    assert t.test_int(41) == 41 * 2

    assert t.test_float(41.0) == 41.0 * 2

    assert t.test_bool(True) == False

    assert t.test_epyt(ExamplePythonType(41)) == ExamplePythonType(41 * 2)

    assert t.test_epytclr(ExamplePythonClrType(41)) == ExamplePythonClrType(41 * 2)


def test_method_invocation_from_clr():
    """Test calling a clr-accessible method from the clr."""
    t = ExampleClrClass()

    assert t.GetType().GetMethod("test_str").Invoke(t, ["37"]) == "37" * 2

    assert t.GetType().GetMethod("test_int").Invoke(t, [37]) == 37 * 2

    assert t.GetType().GetMethod("test_float").Invoke(t, [37.0]) == 37.0 * 2

    assert t.GetType().GetMethod("test_bool").Invoke(t, [True]) == False

    assert t.GetType().GetMethod("test_epyt").Invoke(
        t, [ExamplePythonType(37)]
    ) == ExamplePythonType(37 * 2)

    assert t.GetType().GetMethod("test_epytclr").Invoke(
        t, [ExamplePythonClrType(37)]
    ) == ExamplePythonClrType(37 * 2)
