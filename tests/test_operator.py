# -*- coding: utf-8 -*-

"""Test operator overload support."""

from Python.Test import ( # type: ignore
    OperatorValue,
)

def test_equality_check():
    v1 = OperatorValue()
    v2 = OperatorValue()
    assert v1 == v2

    assert (0 == v1) == False
    assert (v1 == 0) == False

    assert 0 != v1
    assert v1 != 0
