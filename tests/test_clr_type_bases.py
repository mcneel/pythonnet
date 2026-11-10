"""Tests for adding python mixins to CLR types by assigning __bases__, as hosts do at startup."""

import System
import pytest

from Python.Test import (
    BasesTestPoint,
    BasesTestVector,
    BasesTestClass,
    BasesTestMethods,
)


class Clonable:
    def Clone(self):
        return System.ICloneable.Clone(self)


class Coord3dExtensions:
    def __len__(self):
        return 3

    def __iter__(self):
        yield self.X
        yield self.Y
        yield self.Z


@pytest.fixture
def patched():
    """Prepends mixins to CLR types and restores the original bases afterwards."""
    originals = {}

    def patch(clr_type, *mixins):
        originals.setdefault(clr_type, clr_type.__bases__)
        clr_type.__bases__ = mixins + clr_type.__bases__

    yield patch

    for clr_type, bases in originals.items():
        clr_type.__bases__ = bases


def test_explicit_interface_member_hidden_without_mixin():
    assert not hasattr(BasesTestPoint(1, 2, 3), "Clone")


def test_bases_assignment_changes_bases(patched):
    original = BasesTestPoint.__bases__
    patched(BasesTestPoint, Clonable)

    assert BasesTestPoint.__bases__ == (Clonable,) + original
    assert Clonable in BasesTestPoint.__mro__


def test_bases_assignment_can_be_restored():
    original = BasesTestPoint.__bases__
    BasesTestPoint.__bases__ = (Clonable,) + original
    BasesTestPoint.__bases__ = original

    assert BasesTestPoint.__bases__ == original
    assert not hasattr(BasesTestPoint(1, 2, 3), "Clone")


def test_mixin_exposes_explicit_interface_member_on_struct(patched):
    patched(BasesTestPoint, Clonable)

    p = BasesTestPoint(1, 2, 3)
    clone = p.Clone()

    assert isinstance(clone, BasesTestPoint)
    assert (clone.X, clone.Y, clone.Z) == (1, 2, 3)


def test_mixin_exposes_explicit_interface_member_on_class(patched):
    patched(BasesTestClass, Clonable)

    original = BasesTestClass(42)
    clone = original.Clone()

    assert isinstance(clone, BasesTestClass)
    assert clone.Value == 42
    assert not clone.Equals(original)


def test_mixins_stack_on_same_type(patched):
    # each assignment prepends to the bases left by the previous one
    patched(BasesTestVector, Clonable)
    patched(BasesTestVector, Coord3dExtensions)

    assert BasesTestVector.__bases__[:2] == (Coord3dExtensions, Clonable)

    v = BasesTestVector(1, 2, 3)
    assert len(v) == 3
    assert list(v) == [1, 2, 3]
    x, y, z = v
    assert (x, y, z) == (1, 2, 3)
    assert isinstance(v.Clone(), BasesTestVector)


def test_patched_struct_still_converts_to_and_from_clr(patched):
    patched(BasesTestVector, Clonable)
    patched(BasesTestVector, Coord3dExtensions)

    v = BasesTestVector(1, 2, 3)
    assert BasesTestMethods.SumVector(v) == 6

    echoed = BasesTestMethods.EchoVector(v)
    assert isinstance(echoed, BasesTestVector)
    assert list(echoed) == [1, 2, 3]


def test_patching_one_type_does_not_affect_another(patched):
    patched(BasesTestVector, Coord3dExtensions)

    with pytest.raises(TypeError):
        len(BasesTestPoint(1, 2, 3))


def test_regular_type_attribute_assignment_still_works():
    BasesTestPoint.python_attr = "value"
    try:
        assert BasesTestPoint.python_attr == "value"
        assert BasesTestPoint(1, 2, 3).python_attr == "value"
    finally:
        del BasesTestPoint.python_attr

    assert not hasattr(BasesTestPoint, "python_attr")
