"""Test pickle, copy and deepcopy of serializable .NET objects (RH-77321)."""

import copy
import pickle

import pytest

import System
from System.Collections.Generic import List, Dictionary
from Python.Test import (
    SerializableStruct,
    SerializableClass,
    DerivedSerializableClass,
    SerializableEnum,
    CustomSerializableClass,
    CustomSerializableStruct,
    NonSerializableClass,
)

PROTOCOLS = range(pickle.HIGHEST_PROTOCOL + 1)


def roundtrip(obj, protocol=pickle.DEFAULT_PROTOCOL):
    return pickle.loads(pickle.dumps(obj, protocol))


@pytest.mark.parametrize("protocol", PROTOCOLS)
def test_struct(protocol):
    p = roundtrip(SerializableStruct(1.5, 2.5), protocol)
    assert isinstance(p, SerializableStruct)
    assert (p.X, p.Y) == (1.5, 2.5)


@pytest.mark.parametrize("protocol", PROTOCOLS)
def test_class_with_fields(protocol):
    obj = SerializableClass()
    obj.Name = "parent"
    obj.Count = 3
    obj.Point = SerializableStruct(1, 2)
    obj.Kind = SerializableEnum.Two
    obj.Cache = "not saved"
    obj.Child = SerializableClass()
    obj.Child.Name = "child"

    loaded = roundtrip(obj, protocol)

    assert loaded.Name == "parent"
    assert loaded.Count == 3
    assert loaded.Point.Y == 2
    assert loaded.Kind == SerializableEnum.Two
    assert loaded.Cache is None
    assert loaded.Child.Name == "child"
    assert loaded.Child.Child is None


def test_derived_class_keeps_base_fields():
    obj = DerivedSerializableClass()
    obj.Name = "base"
    obj.Extra = 4.0

    loaded = roundtrip(obj)

    assert isinstance(loaded, DerivedSerializableClass)
    assert loaded.Name == "base"
    assert loaded.Extra == 4.0


@pytest.mark.parametrize("protocol", PROTOCOLS)
def test_iserializable_class(protocol):
    obj = CustomSerializableClass()
    obj.Data = System.Array[System.Byte](list(range(256)))
    obj.Point = SerializableStruct(3, 4)
    obj.Boxed = System.Int64(42)

    loaded = roundtrip(obj, protocol)

    assert loaded.WasDeserialized
    assert bytes(loaded.Data) == bytes(range(256))
    assert loaded.Point.X == 3
    assert isinstance(loaded.Boxed, int) and loaded.Boxed == 42


def test_iserializable_struct():
    loaded = roundtrip(CustomSerializableStruct(7.25))
    assert isinstance(loaded, CustomSerializableStruct)
    assert loaded.Value == 7.25


def test_enum():
    assert roundtrip(SerializableEnum.One) == SerializableEnum.One


def test_array_of_structs():
    arr = System.Array[SerializableStruct]([SerializableStruct(1, 2), SerializableStruct(3, 4)])
    loaded = roundtrip(arr)
    assert [p.X for p in loaded] == [1, 3]


def test_generic_list():
    items = List[SerializableStruct]()
    items.Add(SerializableStruct(5, 6))
    loaded = roundtrip(items)
    assert loaded.Count == 1
    assert loaded[0].Y == 6


def test_generic_dictionary():
    d = Dictionary[str, int]()
    d["a"] = 1
    loaded = roundtrip(d)
    assert loaded["a"] == 1


def test_copy_returns_new_object():
    obj = SerializableClass()
    obj.Name = "a"
    copied = copy.copy(obj)
    copied.Name = "b"
    assert obj.Name == "a"


def test_deepcopy():
    obj = SerializableClass()
    obj.Child = SerializableClass()
    obj.Child.Name = "child"
    copied = copy.deepcopy(obj)
    copied.Child.Name = "changed"
    assert obj.Child.Name == "child"


def test_copy_struct():
    p = copy.copy(SerializableStruct(1, 2))
    assert (p.X, p.Y) == (1, 2)


def test_non_serializable_raises_type_error():
    with pytest.raises(TypeError, match="cannot pickle"):
        pickle.dumps(NonSerializableClass())


def test_unloaded_assembly_is_not_loaded():
    from clr._extras.serialization import _reconstruct

    with pytest.raises(TypeError, match=r"assembly 'NoSuchAssembly' is not loaded.*clr\.AddReference"):
        _reconstruct("Some.Type, NoSuchAssembly", "fields", ())


def test_unknown_type_in_loaded_assembly():
    from clr._extras.serialization import _reconstruct

    with pytest.raises(TypeError, match="was not found"):
        _reconstruct("Python.Test.NoSuchType, Python.Test", "fields", ())


def test_exceptions_still_pickle_as_before():
    exc = System.Exception("test")
    assert pickle.loads(pickle.dumps(exc)).args == exc.args
