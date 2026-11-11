# -*- coding: utf-8 -*-

"""Test which .NET overload the method resolver picks for a Python call.

Every overload on the `Resolver*` test types returns a tag naming its own
signature, so each test asserts the tag of the overload it expects.

Most tests follow IronPython's overload rules. `test_lenient_*` tests pin
where the resolver is deliberately more forgiving than IronPython.
"""

import operator
import threading

import pytest
from System import (
    Array,
    Byte,
    Char,
    Decimal,
    Double,
    Int16,
    Int32,
    Int64,
    Object,
    Single,
    String,
)
from System.Runtime.CompilerServices import StrongBox

from Python.Test import (
    ResolverAmbiguous,
    ResolverArity,
    ResolverBase,
    ResolverColor,
    ResolverConvertible,
    ResolverCtor,
    ResolverDerived,
    ResolverGeneric,
    ResolverHierarchy,
    ResolverInheritBase,
    ResolverInheritDerived,
    ResolverMoreDerived,
    ResolverOptional,
    ResolverOutRef,
    ResolverParams,
    ResolverPoint,
    ResolverPrimitives,
    ResolverSequences,
    ResolverShape,
    ResolverShapeDisposable,
    ResolverSolid,
    ResolverStatic,
    ResolverUnrelated,
    ResolverVec,
    ResolverWide,
    ResolverDelegates,
    ResolverNullable,
    ResolverCollections,
    ResolverHideBase,
    ResolverHideDerived,
    IResolverExplicit,
    ResolverIndexer,
    ResolverIndexerValue,
    ResolverEvents,
    ResolverDelegateFactory,
    ResolverOpBase,
    ResolverOpDerived,
    ResolverCtorShapes,
    ResolverCtorStruct,
    ResolverCtorAbstract,
    ResolverCtorGeneric,
)

ARITY = ResolverArity()
PRIM = ResolverPrimitives()
HIER = ResolverHierarchy()
OPT = ResolverOptional()
PARAMS = ResolverParams()
OUTREF = ResolverOutRef()
GENERIC = ResolverGeneric()
STATIC = ResolverStatic()
SEQ = ResolverSequences()
AMBIG = ResolverAmbiguous()
WIDE = ResolverWide()

BASE = ResolverBase()
DERIVED = ResolverDerived()
MORE_DERIVED = ResolverMoreDerived()
UNRELATED = ResolverUnrelated()
POINT = ResolverPoint(1, 2)


def cases(*rows):
    """Parametrize over (args, expected) rows, with readable test ids."""
    return pytest.mark.parametrize(
        "args, expected", rows, ids=[repr(row[0]) for row in rows]
    )


# --------------------------------------------------------------------------
# Number of arguments
# --------------------------------------------------------------------------


@cases(
    ((), ""),
    ((1,), "int"),
    ((1, 2), "int,int"),
    ((1, 2, 3), "int,int,int"),
)
def test_arity_selects_by_argument_count(args, expected):
    assert ARITY.Count(*args) == expected


def test_arity_single_methods():
    assert ARITY.Single() == "single"
    assert ARITY.SingleInt(1) == "single:int"
    assert ARITY.SingleThree(1, 2.5, "s") == "single:int,double,string"


def test_arity_zero_args_prefers_parameterless_over_optional():
    assert ARITY.ZeroOrOptional() == ""
    assert ARITY.ZeroOrOptional(1) == "int="


@pytest.mark.parametrize(
    "call",
    [
        lambda: ARITY.Count(1, 2, 3, 4),
        lambda: ARITY.OnlyTwo(1),
        lambda: ARITY.OnlyTwo(1, 2, 3),
        lambda: ARITY.Single(1),
    ],
    ids=["too-many", "too-few", "one-extra", "arg-to-parameterless"],
)
def test_arity_mismatch_raises(call):
    with pytest.raises(TypeError, match="No overload for method"):
        call()


def test_arity_mismatch_message_lists_argument_types():
    with pytest.raises(TypeError) as err:
        ARITY.Count(1, "s", 2.5, None)
    message = str(err.value)
    assert "takes '4' arguments" in message
    assert "<class 'int'>, <class 'str'>, <class 'float'>" in message


def test_wrong_type_for_single_overload_raises():
    with pytest.raises(TypeError):
        ARITY.SingleInt("a")
    with pytest.raises(TypeError):
        ARITY.SingleInt(None)
    with pytest.raises(TypeError):
        ARITY.OnlyTwo("a", "b")


# --------------------------------------------------------------------------
# Primitive types
# --------------------------------------------------------------------------


@cases(
    ((True,), "bool"),
    ((1,), "int"),
    ((2**40,), "long"),
    ((1.5,), "double"),
    (("s",), "string"),
    (("c",), "string"),
)
def test_primitives_python_values_pick_natural_type(args, expected):
    assert PRIM.Pick(*args) == expected


@pytest.mark.parametrize(
    "value, expected",
    [
        (Byte(1), "byte"),
        (Int16(1), "short"),
        (Int32(1), "int"),
        (Int64(1), "long"),
        (Single(1), "float"),
        (Double(1), "double"),
        (Decimal(1), "decimal"),
        (Char("c"), "char"),
        (String("s"), "string"),
        (Object(), "object"),
    ],
    ids=lambda v: v if isinstance(v, str) else type(v).__name__,
)
def test_primitives_clr_values_pick_exact_type(value, expected):
    assert PRIM.Pick(value) == expected


def test_primitives_unknown_python_object_picks_object():
    assert PRIM.Pick([1]) == "object"
    assert PRIM.Pick(BASE) == "object"


@pytest.mark.parametrize(
    "method, value, expected",
    [
        ("IntOrDouble", 1, "int"),
        ("IntOrDouble", 1.5, "double"),
        ("LongOrString", 1, "long"),
        ("LongOrString", 2**40, "long"),
        ("LongOrString", "s", "string"),
        ("LongOrString", Int64(1), "long"),
        ("FloatOrDouble", 1.5, "double"),
        ("FloatOrDouble", 1, "float"),
        ("FloatOrDouble", Single(1), "float"),
        # an int is an Int32: long takes it as is, short only by narrowing
        ("ShortOrLong", 1, "long"),
        ("ShortOrLong", 2**40, "long"),
        ("ShortOrLong", Int16(1), "short"),
        ("ShortOrLong", Int64(1), "long"),
        ("StringOrObject", "s", "string"),
        ("StringOrObject", 1, "object"),
        ("StringOrObject", 1.5, "object"),
        ("StringOrObject", True, "object"),
        ("BoolOrInt", True, "bool"),
        ("BoolOrInt", 1, "int"),
    ],
)
def test_primitives_two_way_choice(method, value, expected):
    assert getattr(PRIM, method)(value) == expected


@pytest.mark.parametrize(
    "method, value",
    [
        ("IntOrDouble", "s"),
        ("LongOrString", 1.5),
        ("FloatOrDouble", "s"),
        ("ShortOrLong", "s"),
        ("BoolOrInt", "s"),
        ("OnlyDouble", "s"),
        ("OnlyString", 1),
        ("OnlyByte", 1.0),
        ("OnlyLong", 2**70),
    ],
)
def test_primitives_unconvertible_value_raises(method, value):
    with pytest.raises(TypeError):
        getattr(PRIM, method)(value)


def test_primitives_single_overload_converts_python_values():
    assert PRIM.OnlyDouble(1) == "double:1"
    assert PRIM.OnlyDouble(1.5) == "double:1.5"
    assert PRIM.OnlyFloat(1) == "float:1"
    assert PRIM.OnlyFloat(1.5) == "float:1.5"
    assert PRIM.OnlyLong(1) == "long:1"
    assert PRIM.OnlyString("s") == "string:s"
    assert PRIM.OnlyObject(1) == "object:Int32"
    assert PRIM.OnlyObject("s") == "object:String"
    assert PRIM.OnlyObject(1.5) == "object:Double"
    assert PRIM.OnlyObject(BASE) == "object:ResolverBase"
    assert PRIM.OnlyObject([1]) == "object:PyObject"


@cases(
    ((1, 1), "int,int"),
    ((Int16(1), Int16(1)), "short,short"),
    ((Int64(1), Int64(1)), "long,long"),
    ((2**40, 1), "long,long"),
    # Int16 widens to int without narrowing; 1 would need narrowing to short
    ((Int16(1), 1), "int,int"),
    ((Int64(1), 1), "long,long"),
)
def test_primitives_two_parameters(args, expected):
    assert PRIM.Pair(*args) == expected


@cases(
    ((1, 1.5), "int,double"),
    ((1.5, 1), "double,int"),
    (("a", "b"), "string,string"),
)
def test_primitives_mixed_parameters(args, expected):
    assert PRIM.Mixed(*args) == expected


def test_value_out_of_range_does_not_poison_later_calls():
    """A failed conversion must not change how the same types resolve next time."""
    assert PRIM.OnlyByte(5) == "byte:5"
    with pytest.raises(TypeError):
        PRIM.OnlyByte(300)
    with pytest.raises(TypeError):
        PRIM.OnlyByte(-1)
    assert PRIM.OnlyByte(5) == "byte:5"

    assert PRIM.IntOrString(Int64(5)) == "int"
    with pytest.raises(TypeError):
        PRIM.IntOrString(Int64(2**40))
    assert PRIM.IntOrString(Int64(5)) == "int"


def test_explicit_overload_selection():
    assert PRIM.Pick.Overloads[Int32](1) == "int"
    assert PRIM.Pick.Overloads[Int64](1) == "long"
    assert PRIM.Pick.Overloads[Double](1) == "double"
    assert PRIM.Pick.__overloads__[Double](1) == "double"


# --------------------------------------------------------------------------
# Class hierarchy, structs, enums, None
# --------------------------------------------------------------------------


@pytest.mark.parametrize(
    "value, expected",
    [
        (BASE, "base"),
        (DERIVED, "derived"),
        (MORE_DERIVED, "derived"),
        (UNRELATED, "object"),
        (1, "object"),
        ("s", "object"),
    ],
    ids=["base", "derived", "more-derived", "unrelated", "int", "str"],
)
def test_hierarchy_picks_closest_ancestor(value, expected):
    assert HIER.Pick(value) == expected


def test_hierarchy_derived_is_accepted_as_base():
    assert HIER.BaseOnly(DERIVED) == "base"
    assert HIER.BaseOnly(MORE_DERIVED) == "base"
    assert HIER.BaseOrObject(MORE_DERIVED) == "base"
    assert HIER.BaseOrObject(UNRELATED) == "object"


@pytest.mark.parametrize("value", [UNRELATED, 1, "s"], ids=["unrelated", "int", "str"])
def test_hierarchy_unrelated_type_raises(value):
    with pytest.raises(TypeError):
        HIER.BaseOnly(value)


@cases(
    ((BASE, BASE), "base,base"),
    ((DERIVED, BASE), "derived,base"),
    ((DERIVED, DERIVED), "derived,derived"),
    ((BASE, DERIVED), "base,base"),
    ((MORE_DERIVED, MORE_DERIVED), "derived,derived"),
    ((MORE_DERIVED, BASE), "derived,base"),
)
def test_hierarchy_two_parameters(args, expected):
    assert HIER.Two(*args) == expected


@cases(
    ((POINT,), "point"),
    ((POINT, POINT), "point,point"),
    ((1.0, 2.0), "double,double"),
    ((1, 2), "double,double"),
)
def test_struct_overloads(args, expected):
    assert HIER.Point(*args) == expected


def test_enum_overloads():
    assert HIER.Color(ResolverColor.Red) == "color"
    assert HIER.Color("s") == "string"
    with pytest.raises(TypeError):
        HIER.Color(1)


def test_implicit_conversion_operator_is_used():
    assert HIER.Convertible(ResolverConvertible()) == "double:42"
    assert HIER.Convertible(1.5) == "double:1.5"


def test_none_is_passed_as_null_reference():
    assert PRIM.OnlyString(None) == "string:<null>"
    assert PRIM.OnlyObject(None) == "object:<null>"
    assert HIER.NullOnlyBase(None) == "null"
    assert HIER.NullOnlyBase(BASE) == "base"


def test_none_skips_value_type_overload():
    assert HIER.NullRef(None) == "string"
    assert HIER.NullRef(1) == "int"
    assert HIER.NullRef("s") == "string"


# --------------------------------------------------------------------------
# Optional parameters and keyword arguments
# --------------------------------------------------------------------------


def test_optional_prefers_overload_without_unused_optionals():
    assert OPT.Opt(1) == "int"
    assert OPT.Opt(a=1) == "int"
    assert OPT.Opt(1, 2) == "int,int=2"
    assert OPT.Opt(1, b=2) == "int,int=2"
    assert OPT.Opt(a=1, b=2) == "int,int=2"


@pytest.mark.parametrize(
    "args, kwargs, expected",
    [
        ((1,), {}, "a=1,b=2,c=c"),
        ((1, 3), {}, "a=1,b=3,c=c"),
        ((1, 3, "z"), {}, "a=1,b=3,c=z"),
        ((1,), {"c": "z"}, "a=1,b=2,c=z"),
        ((), {"c": "z", "a": 9}, "a=9,b=2,c=z"),
        ((), {"c": "z", "b": 8, "a": 9}, "a=9,b=8,c=z"),
    ],
)
def test_optional_defaults_and_keywords(args, kwargs, expected):
    assert OPT.Named(*args, **kwargs) == expected


@pytest.mark.parametrize(
    "args, kwargs",
    [
        ((), {}),
        ((1,), {"zzz": 2}),
        ((1, 2, "c", 4), {}),
    ],
    ids=["missing-required", "unknown-keyword", "too-many"],
)
def test_optional_bad_calls_raise(args, kwargs):
    with pytest.raises(TypeError, match="No overload for method"):
        OPT.Named(*args, **kwargs)


@pytest.mark.parametrize(
    "args, kwargs, expected",
    [
        ((1,), {}, "count"),
        (("n",), {}, "name"),
        ((1, "n"), {}, "count,name"),
        ((), {"count": 1}, "count"),
        ((), {"name": "n"}, "name"),
        ((), {"count": 1, "name": "n"}, "count,name"),
        ((), {"name": "n", "count": 1}, "count,name"),
        ((1,), {"name": "n"}, "count,name"),
    ],
)
def test_keywords_select_overload_by_parameter_name(args, kwargs, expected):
    assert OPT.Kw(*args, **kwargs) == expected


def test_optional_attribute_uses_type_default():
    assert OPT.Attr(1) == "int,[opt]int=0"
    assert OPT.Attr(1, 2) == "int,[opt]int=2"


def test_all_optional_parameters():
    assert OPT.Defaults() == "1,2.5,x,True,null"
    assert OPT.Defaults(d=False) == "1,2.5,x,False,null"
    assert OPT.Defaults(5, c="y") == "5,2.5,y,True,null"
    assert OPT.Defaults(e="obj") == "1,2.5,x,True,obj"


def test_unused_optional_does_not_count_against_closer_type():
    assert OPT.FloatOrDoubleOpt(1.5) == "double"
    assert OPT.FloatOrDoubleOpt(Single(1.5)) == "float,float="
    assert OPT.FloatOrDoubleOpt(1.5, 2.5) == "float,float="


# --------------------------------------------------------------------------
# params arrays
# --------------------------------------------------------------------------


def test_params_accepts_any_number_of_arguments():
    assert PARAMS.Ints() == "params int:0"
    assert PARAMS.Ints(1) == "params int:1"
    assert PARAMS.Ints(1, 2, 3) == "params int:3"
    assert PARAMS.Sum(1, 2, 3) == 6
    assert PARAMS.Sum(*range(50)) == sum(range(50))


def test_params_accepts_single_sequence():
    assert PARAMS.Ints([1, 2, 3]) == "params int:3"
    assert PARAMS.Ints((1, 2)) == "params int:2"
    assert PARAMS.Ints(Array[Int32]([1, 2, 3, 4])) == "params int:4"
    assert PARAMS.Ints(values=[1, 2]) == "params int:2"


def test_params_wrong_element_type_raises():
    with pytest.raises(TypeError):
        PARAMS.Ints("a")
    with pytest.raises(TypeError):
        PARAMS.Ints(1, "a")


def test_params_after_leading_parameter():
    assert PARAMS.Lead("a") == "string,params object:0"
    assert PARAMS.Lead("a", 1, "b", 2.0) == "string,params object:3"
    assert PARAMS.Lead("a", BASE) == "string,params object:1"
    with pytest.raises(TypeError):
        PARAMS.Lead()


@cases(
    ((), "params int:0"),
    ((1,), "int"),
    ((1, 2), "int,int"),
    ((1, 2, 3), "params int:3"),
    (([1, 2],), "params int:2"),
)
def test_params_loses_to_exact_parameter_count(args, expected):
    assert PARAMS.Prefer(*args) == expected


@cases(
    ((1, 2), "params int:2"),
    (("a", "b"), "params string:2"),
    ((["a"],), "params string:1"),
    (([1],), "params int:1"),
)
def test_params_selects_by_element_type(args, expected):
    assert PARAMS.Typed(*args) == expected


# --------------------------------------------------------------------------
# out and ref parameters
# --------------------------------------------------------------------------


def test_out_parameter_is_returned_with_result():
    assert OUTREF.TryGet(1) == (True, "v1")
    assert OUTREF.TryGet(-1) == (False, "v-1")


def test_out_parameter_only_is_returned_alone():
    assert OUTREF.OutOnly() == 7
    assert OUTREF.TwoOuts(2) == (4, 6)


def test_out_parameter_fills_strongbox():
    box = StrongBox[String]()
    assert OUTREF.TryGet(3, box) is True
    assert box.Value == "v3"


@pytest.mark.parametrize(
    "call",
    [
        lambda: OUTREF.TryGet(1, None),
        lambda: OUTREF.TryGet(1, "x"),
        lambda: OUTREF.OutOnly(0),
        lambda: OUTREF.Over(1, 2),
    ],
    ids=["none-for-out", "value-for-out", "value-for-out-only", "value-for-out-overload"],
)
def test_out_parameter_rejects_plain_value(call):
    with pytest.raises(TypeError, match="No overload for method"):
        call()


def test_ref_parameter_is_required_and_returned():
    assert OUTREF.Ref(1) == (20, 2)
    with pytest.raises(TypeError):
        OUTREF.Ref()


def test_out_overload_does_not_shadow_plain_overloads():
    assert OUTREF.Over(1) == "int"
    assert OUTREF.Over(1, 2, 3) == "int,int,int"


def test_in_attributed_array_is_a_normal_parameter():
    assert OUTREF.InArray(Array[Byte]([1, 2, 3]), 0) == "[in]byte[],long:3"
    assert OUTREF.InArray(b"abc", 0) == "[in]byte[],long:3"


# --------------------------------------------------------------------------
# Generic methods
# --------------------------------------------------------------------------


def test_generic_loses_to_exact_non_generic_match():
    assert GENERIC.Echo(1) == "int"


@pytest.mark.parametrize(
    "value, expected",
    [("s", "T:String"), (1.5, "T:Double"), (BASE, "T:ResolverBase")],
    ids=["str", "float", "clr-object"],
)
def test_generic_type_is_inferred_from_argument(value, expected):
    assert GENERIC.Echo(value) == expected
    assert GENERIC.OnlyT(value) == expected


def test_generic_two_type_parameters_are_inferred():
    assert GENERIC.Two(1, "s") == "T:Int32,U:String"
    assert GENERIC.Two("s", BASE) == "T:String,U:ResolverBase"


def test_generic_explicit_type_arguments():
    assert GENERIC.Echo[str]("s") == "T:String"
    assert GENERIC.Echo[int](1) == "T:Int32"
    assert GENERIC.Echo[Double](1) == "T:Double"
    assert GENERIC.OnlyT[Object](BASE) == "T:Object"
    assert GENERIC.Two[int, str](1, "s") == "T:Int32,U:String"
    assert GENERIC.Second[str](1, "s") == "int,T:String"
    assert GENERIC.Explicit[int]() == "T:Int32"


def test_generic_array_loses_to_exact_array_type():
    assert GENERIC.Arr(Array[Double]([1.0])) == "double[]"
    assert GENERIC.Arr([1.0]) == "double[]"


# --------------------------------------------------------------------------
# Static methods, inheritance, constructors, operators
# --------------------------------------------------------------------------


@pytest.mark.parametrize("target", [ResolverStatic, STATIC], ids=["class", "instance"])
def test_static_overloads(target):
    assert target.OnlyStatic(1) == "static:int"
    assert target.OnlyStatic(1.5) == "static:double"
    assert target.OnlyStatic("a") == "static:string,int="
    assert target.OnlyStatic("a", 1) == "static:string,int="


def test_static_and_instance_overloads_share_a_name():
    assert STATIC.Mix(1) == "instance:int"
    assert STATIC.Mix(1, 2) == "static:int,int"
    assert ResolverStatic.Mix(1, 2) == "static:int,int"


def test_instance_overload_needs_an_instance():
    with pytest.raises(TypeError):
        ResolverStatic.Mix(1)


def test_inherited_overloads_join_derived_ones():
    derived = ResolverInheritDerived()
    assert derived.Pick(1) == "base:int"
    assert derived.Pick("s") == "base:string"
    assert derived.Pick(1.5) == "derived:double"
    assert derived.Virt(1) == "derived:int"
    assert derived.Virt("s") == "derived:string"


@pytest.mark.parametrize(
    "args, kwargs, expected",
    [
        ((), {}, ""),
        ((1,), {}, "int"),
        ((1.5,), {}, "double"),
        (("s",), {}, "string"),
        ((BASE,), {}, "base"),
        ((DERIVED,), {}, "base"),
        ((1, "a"), {}, "int,string"),
        ((1, 2), {}, "int,int,int=3"),
        ((1, 2, 9), {}, "int,int,int=9"),
        ((1, 2), {"c": 9}, "int,int,int=9"),
        ((), {"a": 1}, "int"),
    ],
)
def test_constructor_overloads(args, kwargs, expected):
    assert ResolverCtor(*args, **kwargs).Tag == expected


@pytest.mark.parametrize(
    "args",
    [("a", "b"), (None, None), (1, 2, 3, 4, 5)],
    ids=["wrong-types", "nones", "too-many"],
)
def test_constructor_bad_calls_raise(args):
    with pytest.raises(TypeError):
        ResolverCtor(*args)


def test_operator_overloads():
    a = ResolverVec(1.0)
    b = ResolverVec(2.0)
    assert (a + b).X == 3.0
    assert (a + 3.0).X == 1004.0
    assert (a + 3).X == 1004.0
    assert (3.0 + a).X == 2004.0
    assert (a * 2.0).X == 2.0
    assert (a * b).X == 2.5
    assert (-a).X == -1.0
    assert (b - a).X == 1.0
    assert (b / 2.0).X == 1.0
    assert (a < b) is True
    assert (a > b) is False
    assert (a == ResolverVec(1.0)) is True
    assert (a != b) is True
    assert (a == 1.0) is False


def test_operators_and_member_access_chain():
    a = ResolverVec(1.0)
    b = ResolverVec(2.0)
    assert ((a + b * 2.0) - a).X == 4.0


# --------------------------------------------------------------------------
# Arrays and sequences
# --------------------------------------------------------------------------


def test_clr_arrays_pick_exact_element_type():
    assert SEQ.Arr(Array[Int32]([1, 2])) == "int[]:2"
    assert SEQ.Arr(Array[Double]([1.0])) == "double[]:1"
    assert SEQ.Arr(Array[String](["a"])) == "string[]:1"
    assert SEQ.ArrOrScalar(Array[Double]([1.0])) == "double[]:1"
    assert SEQ.ArrOrScalar(1.0) == "double"


def test_python_sequences_convert_for_single_overload():
    assert SEQ.OnlyIntArr([1, 2]) == "int[]:2"
    assert SEQ.OnlyIntArr((1, 2)) == "int[]:2"
    assert SEQ.OnlyEnumerable([1, 2]) == "IEnumerable<int>:2"
    assert SEQ.OnlyEnumerable(range(3)) == "IEnumerable<int>:3"
    assert SEQ.OnlyList([1, 2]) == "IList<int>:2"
    assert SEQ.OnlyList(Array[Int32]([1, 2])) == "IList<int>:2"
    with pytest.raises(TypeError):
        SEQ.OnlyIntArr(["a"])


def test_generator_is_not_consumed_while_resolving():
    assert SEQ.OnlyIntArr(i for i in range(3)) == "int[]:3"
    assert SEQ.OnlyEnumerable(i for i in range(3)) == "IEnumerable<int>:3"


# --------------------------------------------------------------------------
# Ambiguity
# --------------------------------------------------------------------------


def test_ambiguous_call_raises_and_names_candidates():
    with pytest.raises(TypeError) as err:
        AMBIG.OptTie()
    message = str(err.value)
    assert "The call is ambiguous" in message
    assert "OptTie(Int32)" in message
    assert "OptTie(System.String)" in message

    with pytest.raises(TypeError, match="The call is ambiguous"):
        PARAMS.Typed()


def test_argument_resolves_otherwise_ambiguous_call():
    assert AMBIG.OptTie(1) == "int="
    assert AMBIG.OptTie("s") == "string="
    assert AMBIG.Iface(ResolverShape()) == "shape"


# --------------------------------------------------------------------------
# Large overload set
# --------------------------------------------------------------------------


@cases(
    ((), ""),
    ((True,), "bool"),
    ((1,), "int"),
    ((1.5,), "double"),
    (("s",), "string"),
    ((BASE,), "base"),
    ((DERIVED,), "derived"),
    ((POINT,), "point"),
    ((1, 1), "int,int"),
    ((1, 1.5), "int,double"),
    ((1.5, 1.5), "double,double"),
    (("a", 1), "string,int"),
    (("a", "b"), "string,string"),
    ((BASE, 1), "base,int"),
    ((POINT, POINT), "point,point"),
    ((1, 2, 3), "int,int,int"),
    ((1.5, 1.5, 1.5), "double,double,double"),
    (("a", 1, 1.5), "string,int,double"),
    ((POINT, POINT, POINT), "point,point,point"),
    ((BASE, BASE, BASE), "base,base,base"),
    ((1, 2, 3, 4), "int,int,int,int"),
    ((1.5, 1.5, 1.5, 1.5), "double,double,double,double"),
    (("a", "a", "a", "a"), "string,string,string,string"),
)
def test_wide_overload_set(args, expected):
    assert WIDE.Wide(*args) == expected


def test_wide_overload_set_no_match():
    with pytest.raises(TypeError, match="No overload for method"):
        WIDE.Wide(1, 1, 1, 1, 1)


# --------------------------------------------------------------------------
# Stability
# --------------------------------------------------------------------------

STABILITY_CALLS = [
    (lambda: PRIM.Pick(1), "int"),
    (lambda: PRIM.Pick(1.5), "double"),
    (lambda: PRIM.Pick("s"), "string"),
    (lambda: HIER.Pick(MORE_DERIVED), "derived"),
    (lambda: OPT.Named(1, c="z"), "a=1,b=2,c=z"),
    (lambda: PARAMS.Prefer(1, 2, 3), "params int:3"),
    (lambda: GENERIC.Echo("s"), "T:String"),
    (lambda: WIDE.Wide("a", 1, 1.5), "string,int,double"),
    (lambda: ResolverCtor(1, "a").Tag, "int,string"),
]


def test_repeated_calls_resolve_the_same_way():
    for _ in range(200):
        for call, expected in STABILITY_CALLS:
            assert call() == expected


def test_concurrent_calls_resolve_correctly():
    errors = []
    start = threading.Barrier(8)

    def worker():
        try:
            start.wait()
            for _ in range(300):
                for call, expected in STABILITY_CALLS:
                    result = call()
                    if result != expected:
                        errors.append((result, expected))
        except Exception as ex:  # report in the main thread
            errors.append(ex)

    threads = [threading.Thread(target=worker) for _ in range(8)]
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join()

    assert not errors


# --------------------------------------------------------------------------
# Lenient rules: accepted where IronPython would refuse
# --------------------------------------------------------------------------


def test_lenient_python_float_is_accepted_for_int_parameter():
    # truncated for `int` only, and only when no overload takes a float
    assert ARITY.SingleInt(1.5) == "single:int"
    assert PRIM.Pair(1.5, 1) == "int,int"
    with pytest.raises(TypeError, match="The call is ambiguous"):
        PRIM.Mixed(1.5, 1.5)
    assert ResolverInheritBase().Pick(1.5) == "base:int"
    assert ResolverCtor(1.5, 1.5).Tag == "int,int,int=3"
    with pytest.raises(TypeError):
        PRIM.OnlyLong(1.5)


def test_lenient_keyword_takes_its_slot_before_positionals():
    # Python raises "got multiple values for argument 'a'";
    # here `a` takes the keyword and 1 moves on to `b`
    assert OPT.Named(1, a=2) == "a=2,b=1,c=c"


def test_lenient_python_bool_is_an_integer_number():
    # a bool overload wins; otherwise integers beat floating point
    assert PRIM.IntOrDouble(True) == "int"
    assert PRIM.FloatOrDouble(True) == "float"
    assert ResolverCtor(True).Tag == "int"
    assert PRIM.LongOrString(True) == "long"
    assert PRIM.ShortOrLong(True) == "long"


# --------------------------------------------------------------------------
# Rules that follow IronPython
# --------------------------------------------------------------------------


def test_overloads_better_on_different_arguments_are_ambiguous():
    # each overload is better on one argument
    with pytest.raises(TypeError, match="The call is ambiguous"):
        PRIM.Mixed(1, 1)
    # Int16 and 1 both widen to int; short would narrow 1
    assert PRIM.Pair(1, Int16(1)) == "int,int"


def test_least_narrowing_wins():
    # a Python float is a Double: float needs narrowing
    assert PRIM.FloatOrDouble(1.5) == "double"
    # a Python int is an Int32: float takes it without narrowing
    assert PRIM.FloatOrInt(1) == "int"
    assert PRIM.FloatOrInt(1.5) == "float"


def test_signed_beats_unsigned():
    assert PRIM.SignedOrUnsigned(1) == "sbyte"
    assert PRIM.SignedOrUnsigned(200) == "byte"


def test_clr_numbers_narrow_when_value_fits():
    assert PRIM.IntOrString(Int64(5)) == "int"
    assert PRIM.OnlyByte(Int32(7)) == "byte:7"
    with pytest.raises(TypeError):
        PRIM.IntOrString(Int64(2**40))


def test_clr_char_and_string_convert():
    assert PRIM.OnlyString(Char("c")) == "string:c"


def test_list_prefers_ilist_over_ienumerable():
    assert SEQ.ListOrEnumerable([1, 2]) == "IList<int>"


def test_large_int_falls_back_to_floating_point():
    # 2**40 does not fit `int`; `double` takes it without narrowing
    assert PRIM.IntOrDouble(2**40) == "double"
    assert ResolverCtor(2**70).Tag == "double"


def test_clr_numbers_widen():
    assert PRIM.IntOrDouble(Int16(1)) == "int"
    assert PRIM.OnlyDouble(Int32(3)) == "double:3"


def test_none_picks_most_specific_reference_type():
    # value types cannot take None
    assert PRIM.Pick(None) == "string"
    assert HIER.Pick(None) == "derived"
    assert HIER.BaseOrObject(None) == "base"
    assert PRIM.StringOrObject(None) == "string"


@pytest.mark.parametrize(
    "value, expected",
    [(ResolverShape(), "shape"), (ResolverSolid(), "solid")],
    ids=["shape", "solid"],
)
def test_implemented_interface_beats_object(value, expected):
    assert HIER.Shape(value) == expected


def test_two_implemented_interfaces_are_ambiguous():
    with pytest.raises(TypeError, match="The call is ambiguous"):
        AMBIG.Iface(ResolverShapeDisposable())


def test_python_list_is_matched_to_array_by_items():
    assert SEQ.Arr([1, 2]) == "int[]:2"
    assert SEQ.Arr([1.0]) == "double[]:1"
    assert SEQ.Arr(["a"]) == "string[]:1"
    # every array type takes an empty list equally well
    with pytest.raises(TypeError, match="The call is ambiguous"):
        SEQ.Arr([])


def test_list_skips_scalar_overloads():
    assert SEQ.ArrOrScalar([1.0]) == "double[]:1"
    assert WIDE.Wide([1]) == "int[]"


def test_generic_array_takes_other_element_type():
    assert GENERIC.Arr(Array[Int32]([1])) == "T[]:Int32"


# --------------------------------------------------------------------------
# Fixed defects
# --------------------------------------------------------------------------


@pytest.mark.parametrize(
    "value, expected", [("s", "int,T:String"), (2, "int,T:Int32")], ids=["str", "int"]
)
def test_generic_parameter_not_in_first_position(value, expected):
    assert GENERIC.Second(1, value) == expected


def test_generic_accepts_none():
    assert GENERIC.Echo(None) == "T:Object"


def test_generic_without_type_arguments_raises_type_error():
    with pytest.raises(TypeError):
        GENERIC.Explicit()


@pytest.mark.parametrize(
    "target",
    [ResolverStatic, STATIC],
    ids=["class", "instance"],
)
def test_static_overload_reachable_next_to_instance_overload(target):
    assert target.Mix("a") == "static:string"


def test_operator_without_reverse_overload_raises_type_error():
    with pytest.raises(TypeError):
        operator.mul(2.0, ResolverVec(1.0))


def test_operator_with_unsupported_operand_raises_type_error():
    with pytest.raises(TypeError):
        operator.add(ResolverVec(1.0), "s")
    with pytest.raises(TypeError):
        operator.add("s", ResolverVec(1.0))


def test_none_is_not_passed_to_value_type_parameter():
    with pytest.raises(TypeError):
        ARITY.SingleInt(None)
    with pytest.raises(TypeError):
        GENERIC.Echo[Int32](None)


# --------------------------------------------------------------------------
# Delegate parameters
# --------------------------------------------------------------------------

DELEG = ResolverDelegates()


def test_python_function_fills_action_parameter():
    calls = []
    assert DELEG.OnlyAction(lambda: calls.append(1)) == "Action"
    assert calls == [1]


def test_python_function_fills_func_parameter():
    assert DELEG.OnlyFunc(lambda x: x * 10) == "Func<int,int>:20"


def test_python_function_fills_custom_delegate_parameter():
    assert DELEG.OnlyCustom(lambda x: x + 1) == "IntTransform:4"


def test_clr_delegate_is_passed_as_is():
    from System import Func

    f = Func[Int32, Int32](lambda x: x - 1)
    assert DELEG.OnlyFunc(f) == "Func<int,int>:1"


def test_python_function_picks_delegate_overload():
    assert DELEG.ActionOrInt(lambda: None) == "Action"
    assert DELEG.ActionOrInt(5) == "int"
    assert DELEG.FuncOrString(lambda: 9) == "Func<int>:9"
    assert DELEG.FuncOrString("s") == "string"


# --------------------------------------------------------------------------
# Nullable value types
# --------------------------------------------------------------------------

NULLABLE = ResolverNullable()


def test_nullable_accepts_value_and_none():
    assert NULLABLE.Only(3) == "int?:3"
    assert NULLABLE.Only(None) == "int?:<null>"


def test_nullable_loses_to_exact_value_type():
    assert NULLABLE.IntOrNullable(3) == "int"
    assert NULLABLE.IntOrNullable(None) == "int?"


def test_nullable_or_reference_type():
    assert NULLABLE.NullableOrString(3) == "int?"
    assert NULLABLE.NullableOrString("a") == "string"


# --------------------------------------------------------------------------
# Collections and python objects
# --------------------------------------------------------------------------

COLL = ResolverCollections()


def test_python_bytes_fill_byte_array():
    assert COLL.OnlyBytes(b"abc") == "byte[]:3"


def test_pyobject_parameter_takes_any_python_value():
    assert COLL.OnlyPyObject(object()) == "PyObject"
    assert COLL.OnlyPyObject([1, 2]) == "PyObject"


def test_big_python_int_fills_biginteger():
    assert COLL.OnlyBig(2**80) == "BigInteger:" + str(2**80)


def test_int_prefers_int_over_pyobject():
    assert COLL.PyObjectOrInt(1) == "int"
    assert COLL.PyObjectOrInt("x") == "PyObject"


# --------------------------------------------------------------------------
# Hidden and explicit interface members
# --------------------------------------------------------------------------


def test_new_method_hides_base_method():
    assert ResolverHideDerived().Hidden(1) == "derived:int"
    assert ResolverHideBase().Hidden(1) == "base:int"


def test_new_static_method_hides_base_method():
    assert ResolverHideDerived.StaticHidden(1) == "derived:static"


def test_explicit_interface_member_through_interface():
    ob = ResolverHideDerived()
    assert IResolverExplicit(ob).Explicit(1) == "explicit:int"
    assert ob.Explicit("a") == "public:string"


def test_unbound_instance_method_takes_instance_first():
    assert ResolverHideBase.Hidden(ResolverHideBase(), 1) == "base:int"


# --------------------------------------------------------------------------
# Indexers
# --------------------------------------------------------------------------


def test_indexer_getter_picks_overload_by_key_type():
    ob = ResolverIndexer()
    assert ob[1] == "get:int:1"
    assert ob["k"] == "get:string:k"
    assert ob[1, 2] == "get:int,int"


def test_indexer_setter_picks_overload_by_key_type():
    ob = ResolverIndexer()
    ob[1] = "a"
    ob["k"] = "b"
    ob[1, 2] = "c"
    assert ob.Stored("int") == "a"
    assert ob.Stored("string") == "b"
    assert ob.Stored("int,int") == "c"


def test_indexer_bad_key_raises():
    ob = ResolverIndexer()
    with pytest.raises(TypeError):
        ob[ResolverUnrelated()]


def test_indexer_setter_picks_overload_by_key_and_value():
    ob = ResolverIndexerValue()
    ob[1] = "x"
    assert ob.Last == "set:object:x"
    ob[1.5] = 2.5
    assert ob.Last == "set:double:2.5"


# --------------------------------------------------------------------------
# Event accessors
# --------------------------------------------------------------------------


def test_event_add_and_remove_accessors():
    from System import EventHandler

    ob = ResolverEvents()
    seen = []
    handler = EventHandler(lambda s, e: seen.append(s))
    ob.add_Changed(handler)
    assert ob.Fire() == 1
    assert len(seen) == 1
    ob.remove_Changed(handler)
    assert ob.Fire() == 0


def test_static_event_add_and_remove_accessors():
    from System import EventHandler

    handler = EventHandler(lambda s, e: None)
    ResolverEvents.add_StaticChanged(handler)
    assert ResolverEvents.FireStatic() == 1
    ResolverEvents.remove_StaticChanged(handler)
    assert ResolverEvents.FireStatic() == 0


def test_event_accessor_rejects_python_function():
    with pytest.raises(TypeError):
        ResolverEvents().add_Changed(lambda s, e: None)


def test_event_operator_accepts_python_function():
    ob = ResolverEvents()
    seen = []

    def handler(s, e):
        seen.append(1)

    ob.Changed += handler
    ob.Fire()
    ob.Changed -= handler
    assert ob.Fire() == 0
    assert seen == [1]


# --------------------------------------------------------------------------
# Calling delegates
# --------------------------------------------------------------------------


def test_delegate_call_converts_arguments():
    d = ResolverDelegateFactory.Double()
    assert d(1, 2) == "double,int:1,2"
    assert d(1.5, 2) == "double,int:1.5,2"


def test_delegate_call_wrong_arguments_raise():
    d = ResolverDelegateFactory.Double()
    with pytest.raises(TypeError):
        d(1)
    with pytest.raises(TypeError):
        d("a", 2)


def test_delegate_call_returns_out_parameter():
    d = ResolverDelegateFactory.Out()
    assert d(4) == (True, 8)


# --------------------------------------------------------------------------
# Operators
# --------------------------------------------------------------------------


def test_operator_picks_overload_by_right_operand():
    a = ResolverOpBase(1)
    assert (a + 2).V == 3
    assert (a + "abc").V == 301


def test_reverse_operator_with_python_number():
    assert (5 + ResolverOpBase(1)).V == 1006


def test_unary_operators():
    a = ResolverOpBase(5)
    assert (-a).V == -5
    assert (+a).V == 6
    assert (~a).V == ~5


def test_bitwise_shift_and_modulo_operators():
    a, b = ResolverOpBase(6), ResolverOpBase(3)
    assert (a & b).V == 2
    assert (a | b).V == 7
    assert (a ^ b).V == 5
    assert (a << 1).V == 12
    assert (a >> 1).V == 3
    assert (a % 4).V == 2


def test_comparison_operators():
    a, b = ResolverOpBase(1), ResolverOpBase(2)
    assert a < b and b > a
    assert a <= b and b >= a
    assert a <= ResolverOpBase(1)
    assert not (a >= b)


def test_operators_inherited_by_derived_type():
    d = ResolverOpDerived(1)
    assert (d + 2).V == 3
    assert (ResolverOpBase(1) + 0).V == 1
    assert (d & ResolverOpDerived(3)).V == 1
    assert ResolverOpDerived(1) < ResolverOpDerived(2)


def test_in_place_operator_falls_back_to_binary():
    a = ResolverOpBase(1)
    a += 2
    assert a.V == 3


def test_operator_bad_operand_raises():
    with pytest.raises(TypeError):
        ResolverOpBase(1) + 1.5j


# --------------------------------------------------------------------------
# Constructor shapes
# --------------------------------------------------------------------------


@pytest.mark.parametrize(
    "args, kwargs, expected",
    [
        ((), {}, "params int:0"),
        ((1, 2, 3), {}, "params int:3"),
        (("a",), {}, "string,double=1"),
        (("a", 2.5), {}, "string,double=2.5"),
        (("a",), {"scale": 3.0}, "string,double=3"),
        ((), {"name": "a"}, "string,double=1"),
    ],
)
def test_constructor_shapes(args, kwargs, expected):
    assert ResolverCtorShapes(*args, **kwargs).Tag == expected


def test_constructor_with_python_function():
    assert ResolverCtorShapes(lambda: 4).Tag == "Func<int>:4"


def test_struct_has_implicit_parameterless_constructor():
    assert ResolverCtorStruct().Tag is None
    assert ResolverCtorStruct(1).Tag == "int"


def test_abstract_class_cannot_be_constructed():
    with pytest.raises(TypeError):
        ResolverCtorAbstract()


def test_generic_type_constructor_overloads():
    assert ResolverCtorGeneric[String]("a").Tag == "T:String"
    assert ResolverCtorGeneric[String](1, "a").Tag == "int,T:String"
    assert ResolverCtorGeneric[Int32](1).Tag == "T:Int32"
    assert ResolverCtorGeneric[Int32](1, 2).Tag == "int,T:Int32"



# --------------------------------------------------------------------------
# Found by comparing calls against IronPython in Rhino
# --------------------------------------------------------------------------


def test_big_python_int_fills_object_parameter():
    prim = ResolverPrimitives()
    assert prim.OnlyObject(2**31) == "object:Int64"
    assert prim.OnlyObject(2**63) == "object:UInt64"
    assert prim.OnlyObject(2**64) == "object:BigInteger"
    assert ResolverHierarchy().Pick(2**40) == "object"


def test_big_python_int_sets_generic_type_by_size():
    gen = ResolverGeneric()
    assert gen.OnlyT(2**40) == "T:Int64"
    assert gen.OnlyT(2**64) == "T:BigInteger"


def test_clr_numbers_fill_biginteger():
    assert ResolverCollections().OnlyBig(Int32(7)) == "BigInteger:7"
    assert ResolverCollections().OnlyBig(Double(2.9)) == "BigInteger:2"


def test_clr_float_into_int_drops_fraction():
    prim = ResolverPrimitives()
    assert prim.OnlyLong(Double(1.5)) == "long:1"
    assert prim.OnlyByte(Single(2.9)) == "byte:2"


def test_strongbox_fills_ref_parameter():
    box = StrongBox[Int32](5)
    assert ResolverOutRef().Ref(box) == 60
    assert box.Value == 6


def test_none_fills_delegate_parameter():
    assert ResolverDelegates().ActionOrInt(None) == "Action"


def test_generic_with_exact_type_beats_widening():
    gen = ResolverGeneric()
    assert gen.Echo(True) == "T:Boolean"
    assert gen.Echo(Int16(1)) == "T:Int16"
    assert gen.Echo(1) == "int"


def test_iterator_prefers_ienumerable_over_ilist():
    assert ResolverSequences().ListOrEnumerable(iter([1, 2])) == "IEnumerable<int>"


@pytest.mark.parametrize(
    "call",
    [
        lambda: ResolverOpBase(5) + 1.5,
        lambda: 1.5 + ResolverOpBase(5),
        lambda: ResolverOpBase(5) + Int64(1),
        lambda: ResolverVec(1.0) + ResolverConvertible(),
    ],
)
def test_operator_operand_converts_only_in_safe_ways(call):
    with pytest.raises(TypeError):
        call()


def test_operator_operand_widens():
    assert (ResolverVec(1.0) + 2).X == 1003.0
    assert (ResolverOpBase(5) + Int16(1)).V == 6


def test_generic_rules_for_t_let_another_overload_win():
    gen = ResolverGeneric()
    # T would be Int32, which breaks `where T : class`
    assert gen.Constrained(1) == "long"
    assert gen.Constrained("a") == "T:String"
    # T would be String, which breaks `where T : struct`
    assert gen.ValueOnly("a") == "object"
    assert gen.ValueOnly(Int16(1)) == "T:Int16"


def test_bitwise_operator_on_plain_enum_raises():
    with pytest.raises(TypeError):
        ResolverColor.Red & ResolverColor.Green
    with pytest.raises(TypeError):
        ResolverOpBase(5) & ResolverColor.Red


def test_bitwise_operator_on_flags_enum():
    from System import AttributeTargets

    both = AttributeTargets.Class | AttributeTargets.Method
    assert both & AttributeTargets.Method == AttributeTargets.Method


@pytest.mark.parametrize("other", ["a", 1, ResolverUnrelated()])
def test_ordering_against_unrelated_type_raises_python_error(other):
    with pytest.raises(TypeError, match="not supported between instances"):
        ResolverOpBase(1) < other


def test_lenient_iterator_fills_array():
    assert ResolverSequences().OnlyIntArr(iter([1, 2])) == "int[]:2"


def test_int_does_not_go_into_enum():
    with pytest.raises(TypeError):
        ResolverHierarchy().Color(0)


def test_python_value_does_not_go_into_interface():
    with pytest.raises(TypeError):
        ResolverAmbiguous().Iface(1)


def test_lenient_single_list_fills_params_array():
    assert ResolverParams().Lead("x", [1, 2]) == "string,params object:2"
    assert ResolverParams().Lead("x", [1, 2], 3) == "string,params object:2"


def test_non_bool_value_does_not_go_into_bool():
    with pytest.raises(TypeError):
        ResolverPrimitives().BoolOrInt("a")
