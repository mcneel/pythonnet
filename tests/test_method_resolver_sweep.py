# -*- coding: utf-8 -*-

"""Pin which overload the resolver picks for thousands of calls.

Every overloaded `Resolver*` member is called with a wide set of Python and
.NET values. The outcome of each call must match `fixtures/method_resolver_sweep.txt`.

After an intended change in overload choice, regenerate the file and review its diff:

    UPDATE_RESOLVER_SWEEP=1 pytest --runtime coreclr tests/test_method_resolver_sweep.py
"""

import os
import sys
import pathlib

import pytest
import System
from System import (
    Array, Byte, Char, Decimal, Double, Int16, Int32, Int64, Object,
    SByte, Single, String, UInt32, UInt64,
)
from System.Numerics import BigInteger
from System.Runtime.CompilerServices import StrongBox

from Python.Test import (
    ResolverAmbiguous, ResolverArity, ResolverBase, ResolverCollections,
    ResolverColor, ResolverConvertible, ResolverCtor, ResolverCtorGeneric,
    ResolverCtorShapes, ResolverCtorStruct, ResolverDelegateFactory,
    ResolverDelegates, ResolverDerived, ResolverEvents, ResolverGeneric,
    ResolverHideDerived, ResolverHierarchy, ResolverIndexer, ResolverIndexerValue,
    ResolverInheritDerived, ResolverMoreDerived, ResolverNullable, ResolverOpBase,
    ResolverOpDerived, ResolverOptional, ResolverOutRef, ResolverParams,
    ResolverPoint, ResolverPrimitives, ResolverSequences, ResolverShape,
    ResolverShapeDisposable, ResolverSolid, ResolverStatic, ResolverUnrelated,
    ResolverVec, ResolverWide,
)

EXPECTED = pathlib.Path(__file__).parent / "fixtures" / "method_resolver_sweep.txt"


def describe(value):
    """Text for a call result."""
    if value is None:
        return "None"
    if isinstance(value, bool):
        return str(value)
    if isinstance(value, tuple):
        return "(" + ", ".join(describe(v) for v in value) + ")"
    if isinstance(value, ResolverVec):
        return "Vec({0})".format(describe(value.X))
    if isinstance(value, ResolverOpBase):
        return "Op({0})".format(value.V)
    if isinstance(value, float):
        return repr(value)
    if hasattr(value, "Tag"):
        return str(value.Tag)
    return str(value)


def describe_error(error):
    """Kind of failure; message wording is not pinned."""
    message = str(error)
    kind = type(error).__name__
    if "ambiguous" in message:
        return kind + ": ambiguous"
    if message.startswith("No overload"):
        return kind + ": no overload"
    if "not supported between" in message or "unsupported operand" in message:
        return kind + ": unsupported operand"
    return kind


# Each value as (label, factory), so every call gets a fresh one
VALUES = [
    ("0", lambda: 0),
    ("1", lambda: 1),
    ("-1", lambda: -1),
    ("255", lambda: 255),
    ("256", lambda: 256),
    ("2**31", lambda: 2 ** 31),
    ("2**40", lambda: 2 ** 40),
    ("2**63", lambda: 2 ** 63),
    ("2**64", lambda: 2 ** 64),
    ("True", lambda: True),
    ("False", lambda: False),
    ("1.0", lambda: 1.0),
    ("1.5", lambda: 1.5),
    ("'a'", lambda: "a"),
    ("'abc'", lambda: "abc"),
    ("''", lambda: ""),
    ("b'a'", lambda: b"a"),
    ("None", lambda: None),
    ("[1, 2]", lambda: [1, 2]),
    ("[1.0, 2.0]", lambda: [1.0, 2.0]),
    ("['a', 'b']", lambda: ["a", "b"]),
    ("[1, 'a']", lambda: [1, "a"]),
    ("[]", lambda: []),
    ("(1, 2)", lambda: (1, 2)),
    ("iter([1, 2])", lambda: iter([1, 2])),
    ("lambda: 1", lambda: (lambda: 1)),
    ("lambda x: x", lambda: (lambda x: x)),
    ("object()", lambda: object()),
    ("Int16(1)", lambda: Int16(1)),
    ("Int32(1)", lambda: Int32(1)),
    ("Int64(1)", lambda: Int64(1)),
    ("Int64(2**40)", lambda: Int64(2 ** 40)),
    ("Byte(1)", lambda: Byte(1)),
    ("SByte(1)", lambda: SByte(1)),
    ("UInt32(1)", lambda: UInt32(1)),
    ("UInt64(1)", lambda: UInt64(1)),
    ("Single(1.5)", lambda: Single(1.5)),
    ("Double(1.5)", lambda: Double(1.5)),
    ("Decimal(1.5)", lambda: Decimal(1.5)),
    ("BigInteger(5)", lambda: BigInteger(5)),
    ("Base()", lambda: ResolverBase()),
    ("Derived()", lambda: ResolverDerived()),
    ("MoreDerived()", lambda: ResolverMoreDerived()),
    ("Unrelated()", lambda: ResolverUnrelated()),
    ("Shape()", lambda: ResolverShape()),
    ("Solid()", lambda: ResolverSolid()),
    ("ShapeDisposable()", lambda: ResolverShapeDisposable()),
    ("Color.Red", lambda: ResolverColor.Red),
    ("Convertible()", lambda: ResolverConvertible()),
    ("Point(1, 2)", lambda: ResolverPoint(1.0, 2.0)),
    ("Array[Int32]", lambda: Array[Int32]([1, 2])),
    ("Array[Double]", lambda: Array[Double]([1.0, 2.0])),
    ("Array[String]", lambda: Array[String](["a", "b"])),
    ("Array[Byte]", lambda: Array[Byte]([1, 2])),
    ("Type", lambda: ResolverBase),
]

PAIR_LABELS = ["1", "1.5", "True", "'a'", "None", "Int16(1)", "Int64(1)",
               "Derived()", "Point(1, 2)", "[1, 2]", "lambda: 1"]
PAIRS = [v for v in VALUES if v[0] in PAIR_LABELS]


def one_arg_members():
    prim = ResolverPrimitives()
    hier = ResolverHierarchy()
    arity = ResolverArity()
    opt = ResolverOptional()
    prm = ResolverParams()
    outref = ResolverOutRef()
    gen = ResolverGeneric()
    stat = ResolverStatic()
    inh = ResolverInheritDerived()
    seq = ResolverSequences()
    amb = ResolverAmbiguous()
    wide = ResolverWide()
    dlg = ResolverDelegates()
    nul = ResolverNullable()
    col = ResolverCollections()
    hide = ResolverHideDerived()
    ix = ResolverIndexer()
    ixv = ResolverIndexerValue()
    return [
        ("Primitives.Pick", prim.Pick),
        ("Primitives.IntOrDouble", prim.IntOrDouble),
        ("Primitives.LongOrString", prim.LongOrString),
        ("Primitives.FloatOrDouble", prim.FloatOrDouble),
        ("Primitives.ShortOrLong", prim.ShortOrLong),
        ("Primitives.StringOrObject", prim.StringOrObject),
        ("Primitives.BoolOrInt", prim.BoolOrInt),
        ("Primitives.SignedOrUnsigned", prim.SignedOrUnsigned),
        ("Primitives.IntOrString", prim.IntOrString),
        ("Primitives.FloatOrInt", prim.FloatOrInt),
        ("Primitives.OnlyDouble", prim.OnlyDouble),
        ("Primitives.OnlyFloat", prim.OnlyFloat),
        ("Primitives.OnlyByte", prim.OnlyByte),
        ("Primitives.OnlyLong", prim.OnlyLong),
        ("Primitives.OnlyString", prim.OnlyString),
        ("Primitives.OnlyObject", prim.OnlyObject),
        ("Hierarchy.Pick", hier.Pick),
        ("Hierarchy.BaseOnly", hier.BaseOnly),
        ("Hierarchy.BaseOrObject", hier.BaseOrObject),
        ("Hierarchy.Shape", hier.Shape),
        ("Hierarchy.Point", hier.Point),
        ("Hierarchy.Color", hier.Color),
        ("Hierarchy.Convertible", hier.Convertible),
        ("Hierarchy.NullRef", hier.NullRef),
        ("Hierarchy.NullOnlyBase", hier.NullOnlyBase),
        ("Arity.SingleInt", arity.SingleInt),
        ("Arity.Count", arity.Count),
        ("Arity.ZeroOrOptional", arity.ZeroOrOptional),
        ("Optional.Opt", opt.Opt),
        ("Optional.Kw", opt.Kw),
        ("Optional.Attr", opt.Attr),
        ("Optional.Defaults", opt.Defaults),
        ("Optional.FloatOrDoubleOpt", opt.FloatOrDoubleOpt),
        ("Params.Ints", prm.Ints),
        ("Params.Lead", prm.Lead),
        ("Params.Prefer", prm.Prefer),
        ("Params.Typed", prm.Typed),
        ("Params.Sum", prm.Sum),
        ("OutRef.Over", outref.Over),
        ("OutRef.TryGet", outref.TryGet),
        ("OutRef.Ref", outref.Ref),
        ("Generic.Echo", gen.Echo),
        ("Generic.OnlyT", gen.OnlyT),
        ("Generic.Arr", gen.Arr),
        ("Generic.Constrained", gen.Constrained),
        ("Generic.ValueOnly", gen.ValueOnly),
        ("Static.Mix", stat.Mix),
        ("Static.MixOnType", ResolverStatic.Mix),
        ("Static.OnlyStatic", ResolverStatic.OnlyStatic),
        ("InheritDerived.Pick", inh.Pick),
        ("InheritDerived.Virt", inh.Virt),
        ("Sequences.Arr", seq.Arr),
        ("Sequences.OnlyIntArr", seq.OnlyIntArr),
        ("Sequences.OnlyEnumerable", seq.OnlyEnumerable),
        ("Sequences.OnlyList", seq.OnlyList),
        ("Sequences.ListOrEnumerable", seq.ListOrEnumerable),
        ("Sequences.ArrOrScalar", seq.ArrOrScalar),
        ("Ambiguous.Iface", amb.Iface),
        ("Ambiguous.OptTie", amb.OptTie),
        ("Wide.Wide", wide.Wide),
        ("Delegates.OnlyAction", dlg.OnlyAction),
        ("Delegates.OnlyFunc", dlg.OnlyFunc),
        ("Delegates.OnlyCustom", dlg.OnlyCustom),
        ("Delegates.ActionOrInt", dlg.ActionOrInt),
        ("Delegates.FuncOrString", dlg.FuncOrString),
        ("Nullable.Only", nul.Only),
        ("Nullable.IntOrNullable", nul.IntOrNullable),
        ("Nullable.NullableOrString", nul.NullableOrString),
        ("Collections.OnlyBytes", col.OnlyBytes),
        ("Collections.OnlyPyObject", col.OnlyPyObject),
        ("Collections.PyObjectOrInt", col.PyObjectOrInt),
        ("Collections.OnlyBig", col.OnlyBig),
        ("HideDerived.Hidden", hide.Hidden),
        ("HideDerived.StaticHidden", ResolverHideDerived.StaticHidden),
        ("HideDerived.Explicit", hide.Explicit),
        ("Ctor", ResolverCtor),
        ("CtorShapes", ResolverCtorShapes),
        ("CtorStruct", ResolverCtorStruct),
        ("CtorGeneric[Int32]", ResolverCtorGeneric[Int32]),
        ("CtorGeneric[String]", ResolverCtorGeneric[String]),
        ("CtorGeneric[Object]", ResolverCtorGeneric[Object]),
        ("DelegateFactory.Double()", ResolverDelegateFactory.Double()),
        ("Indexer.get", lambda a: ix[a]),
        ("IndexerValue.get", lambda a: ixv[a]),
    ]


def two_arg_members():
    prim = ResolverPrimitives()
    hier = ResolverHierarchy()
    arity = ResolverArity()
    opt = ResolverOptional()
    prm = ResolverParams()
    outref = ResolverOutRef()
    gen = ResolverGeneric()
    stat = ResolverStatic()
    wide = ResolverWide()
    ix = ResolverIndexer()
    return [
        ("Primitives.Pair", prim.Pair),
        ("Primitives.Mixed", prim.Mixed),
        ("Hierarchy.Two", hier.Two),
        ("Hierarchy.Point", hier.Point),
        ("Arity.Count", arity.Count),
        ("Arity.OnlyTwo", arity.OnlyTwo),
        ("Optional.Opt", opt.Opt),
        ("Optional.Kw", opt.Kw),
        ("Optional.Named", opt.Named),
        ("Optional.FloatOrDoubleOpt", opt.FloatOrDoubleOpt),
        ("Params.Prefer", prm.Prefer),
        ("Params.Typed", prm.Typed),
        ("Params.Lead", prm.Lead),
        ("OutRef.Over", outref.Over),
        ("OutRef.InArray", outref.InArray),
        ("Generic.Second", gen.Second),
        ("Generic.Two", gen.Two),
        ("Static.Mix", stat.Mix),
        ("Static.MixOnType", ResolverStatic.Mix),
        ("Wide.Wide", wide.Wide),
        ("Ctor", ResolverCtor),
        ("CtorShapes", ResolverCtorShapes),
        ("CtorGeneric[Int32]", ResolverCtorGeneric[Int32]),
        ("DelegateFactory.Double()", ResolverDelegateFactory.Double()),
        ("Indexer.get", lambda a, b: ix[a, b]),
    ]


OPERATORS = [
    ("Vec(1) + {0}", lambda v: ResolverVec(1.0) + v),
    ("{0} + Vec(1)", lambda v: v + ResolverVec(1.0)),
    ("Vec(2) * {0}", lambda v: ResolverVec(2.0) * v),
    ("Vec(2) / {0}", lambda v: ResolverVec(2.0) / v),
    ("Vec(2) - {0}", lambda v: ResolverVec(2.0) - v),
    ("Vec(1) == {0}", lambda v: ResolverVec(1.0) == v),
    ("Vec(1) != {0}", lambda v: ResolverVec(1.0) != v),
    ("Vec(1) < {0}", lambda v: ResolverVec(1.0) < v),
    ("Op(5) + {0}", lambda v: ResolverOpBase(5) + v),
    ("{0} + Op(5)", lambda v: v + ResolverOpBase(5)),
    ("Op(5) % {0}", lambda v: ResolverOpBase(5) % v),
    ("Op(5) << {0}", lambda v: ResolverOpBase(5) << v),
    ("Op(5) & {0}", lambda v: ResolverOpBase(5) & v),
    ("{0} & Op(5)", lambda v: v & ResolverOpBase(5)),
    ("Op(5) < {0}", lambda v: ResolverOpBase(5) < v),
    ("Op(5) >= {0}", lambda v: ResolverOpBase(5) >= v),
    ("OpDerived(5) + {0}", lambda v: ResolverOpDerived(5) + v),
    ("OpDerived(5) < {0}", lambda v: ResolverOpDerived(5) < v),
    ("Color.Red & {0}", lambda v: ResolverColor.Red & v),
]


def with_box(call, box):
    return (call(box), box.Value)


def set_index(target, key, value, read):
    target[key] = value
    return read(target)


def fire_event():
    target = ResolverEvents()
    hits = []

    def handler(sender, args):
        hits.append(1)

    target.Changed += handler
    fired = target.Fire()
    target.Changed -= handler
    return (fired, len(hits), target.Fire())


def single_calls():
    arity = ResolverArity()
    opt = ResolverOptional()
    prm = ResolverParams()
    outref = ResolverOutRef()
    gen = ResolverGeneric()
    stat = ResolverStatic()
    wide = ResolverWide()
    hide = ResolverHideDerived()
    return [
        ("Arity.Single()", lambda: arity.Single()),
        ("Arity.Count()", lambda: arity.Count()),
        ("Arity.Count(1, 2, 3)", lambda: arity.Count(1, 2, 3)),
        ("Arity.Count(1, 2, 3, 4)", lambda: arity.Count(1, 2, 3, 4)),
        ("Arity.ZeroOrOptional()", lambda: arity.ZeroOrOptional()),
        ("Arity.SingleThree(1, 2.5, 'c')", lambda: arity.SingleThree(1, 2.5, "c")),
        ("Arity.SingleThree(1, 2, 'c')", lambda: arity.SingleThree(1, 2, "c")),
        ("Arity.OnlyTwo(1)", lambda: arity.OnlyTwo(1)),
        ("Optional.Opt(1, b=7)", lambda: opt.Opt(1, b=7)),
        ("Optional.Opt(a=1)", lambda: opt.Opt(a=1)),
        ("Optional.Opt(b=1)", lambda: opt.Opt(b=1)),
        ("Optional.Named(1)", lambda: opt.Named(1)),
        ("Optional.Named(1, c='z')", lambda: opt.Named(1, c="z")),
        ("Optional.Named(a=1, b=3)", lambda: opt.Named(a=1, b=3)),
        ("Optional.Named(c='z', a=1)", lambda: opt.Named(c="z", a=1)),
        ("Optional.Named(1, a=2)", lambda: opt.Named(1, a=2)),
        ("Optional.Named(b=2)", lambda: opt.Named(b=2)),
        ("Optional.Named(1, d=2)", lambda: opt.Named(1, d=2)),
        ("Optional.Named(1, 2, 'c', 4)", lambda: opt.Named(1, 2, "c", 4)),
        ("Optional.Kw(count=1)", lambda: opt.Kw(count=1)),
        ("Optional.Kw(name='n')", lambda: opt.Kw(name="n")),
        ("Optional.Kw(1, name='n')", lambda: opt.Kw(1, name="n")),
        ("Optional.Kw(name='n', count=1)", lambda: opt.Kw(name="n", count=1)),
        ("Optional.Kw(count='n')", lambda: opt.Kw(count="n")),
        ("Optional.Attr(1, 2)", lambda: opt.Attr(1, 2)),
        ("Optional.Defaults()", lambda: opt.Defaults()),
        ("Optional.Defaults(2, e='x')", lambda: opt.Defaults(2, e="x")),
        ("Optional.Defaults(d=False)", lambda: opt.Defaults(d=False)),
        ("Optional.Defaults(1, 2.5, 'x', True, None)", lambda: opt.Defaults(1, 2.5, "x", True, None)),
        ("Optional.FloatOrDoubleOpt(1.5, b=2.5)", lambda: opt.FloatOrDoubleOpt(1.5, b=2.5)),
        ("Params.Ints()", lambda: prm.Ints()),
        ("Params.Ints(1, 2, 3)", lambda: prm.Ints(1, 2, 3)),
        ("Params.Ints(1, 'a')", lambda: prm.Ints(1, "a")),
        ("Params.Ints(values=[1, 2])", lambda: prm.Ints(values=[1, 2])),
        ("Params.Ints(values=1)", lambda: prm.Ints(values=1)),
        ("Params.Lead('x')", lambda: prm.Lead("x")),
        ("Params.Lead('x', 1, 'a', None)", lambda: prm.Lead("x", 1, "a", None)),
        ("Params.Lead('x', [1, 2], 3)", lambda: prm.Lead("x", [1, 2], 3)),
        ("Params.Lead(lead='x')", lambda: prm.Lead(lead="x")),
        ("Params.Prefer()", lambda: prm.Prefer()),
        ("Params.Prefer(1, 2, 3)", lambda: prm.Prefer(1, 2, 3)),
        ("Params.Typed()", lambda: prm.Typed()),
        ("Params.Typed('a', 'b', 'c')", lambda: prm.Typed("a", "b", "c")),
        ("Params.Sum(1, 2, 3)", lambda: prm.Sum(1, 2, 3)),
        ("Wide.Wide()", lambda: wide.Wide()),
        ("Wide.Wide(1, 2, 3)", lambda: wide.Wide(1, 2, 3)),
        ("Wide.Wide(1.5, 2, 3)", lambda: wide.Wide(1.5, 2, 3)),
        ("Wide.Wide('a', 1, 2)", lambda: wide.Wide("a", 1, 2)),
        ("Wide.Wide('a', 1, 2.5)", lambda: wide.Wide("a", 1, 2.5)),
        ("Wide.Wide(Base, Derived, None)", lambda: wide.Wide(ResolverBase(), ResolverDerived(), None)),
        ("Wide.Wide(Point, Point, Point)", lambda: wide.Wide(ResolverPoint(1.0, 2.0), ResolverPoint(1.0, 2.0), ResolverPoint(1.0, 2.0))),
        ("Wide.Wide(1, 2, 3, 4)", lambda: wide.Wide(1, 2, 3, 4)),
        ("Wide.Wide(1, 2, 3, 4.5)", lambda: wide.Wide(1, 2, 3, 4.5)),
        ("Wide.Wide('a', 'b', 'c', 'd')", lambda: wide.Wide("a", "b", "c", "d")),
        ("Ctor()", lambda: ResolverCtor()),
        ("Ctor(1, 2, 3)", lambda: ResolverCtor(1, 2, 3)),
        ("Ctor(1, 2, c=9)", lambda: ResolverCtor(1, 2, c=9)),
        ("CtorShapes()", lambda: ResolverCtorShapes()),
        ("CtorShapes(1, 2, 3)", lambda: ResolverCtorShapes(1, 2, 3)),
        ("CtorShapes('n', scale=2.5)", lambda: ResolverCtorShapes("n", scale=2.5)),
        ("CtorStruct()", lambda: ResolverCtorStruct()),
        ("OutRef.TryGet(1, StrongBox[String])", lambda: with_box(lambda b: outref.TryGet(1, b), StrongBox[String]())),
        ("OutRef.TryGet(1, StrongBox[Int32])", lambda: with_box(lambda b: outref.TryGet(1, b), StrongBox[Int32]())),
        ("OutRef.OutOnly()", lambda: outref.OutOnly()),
        ("OutRef.OutOnly(StrongBox[Int32])", lambda: with_box(lambda b: outref.OutOnly(b), StrongBox[Int32]())),
        ("OutRef.TwoOuts(2)", lambda: outref.TwoOuts(2)),
        ("OutRef.TwoOuts(2, StrongBox, StrongBox)", lambda: outref.TwoOuts(2, StrongBox[Int32](), StrongBox[Int32]())),
        ("OutRef.Ref(StrongBox[Int32](5))", lambda: with_box(lambda b: outref.Ref(b), StrongBox[Int32](5))),
        ("OutRef.Ref(StrongBox[Int64](5))", lambda: with_box(lambda b: outref.Ref(b), StrongBox[Int64](5))),
        ("OutRef.Over(1, 2, 3)", lambda: outref.Over(1, 2, 3)),
        ("OutRef.Over(1, StrongBox[Int32])", lambda: with_box(lambda b: outref.Over(1, b), StrongBox[Int32]())),
        ("OutRef.InArray(Array[Byte], 0)", lambda: outref.InArray(Array[Byte]([1, 2, 3]), 0)),
        ("Generic.Echo[Int32](1)", lambda: gen.Echo[Int32](1)),
        ("Generic.Echo[String]('a')", lambda: gen.Echo[String]("a")),
        ("Generic.Echo[Double](1)", lambda: gen.Echo[Double](1)),
        ("Generic.Echo[Int64](1)", lambda: gen.Echo[Int64](1)),
        ("Generic.Echo[Int32]('a')", lambda: gen.Echo[Int32]("a")),
        ("Generic.OnlyT[Object](1)", lambda: gen.OnlyT[Object](1)),
        ("Generic.Explicit[Int32]()", lambda: gen.Explicit[Int32]()),
        ("Generic.Explicit[String]()", lambda: gen.Explicit[String]()),
        ("Generic.Explicit()", lambda: gen.Explicit()),
        ("Generic.Two[Int32, String](1, 'a')", lambda: gen.Two[Int32, String](1, "a")),
        ("Generic.Arr[Int32]([1, 2])", lambda: gen.Arr[Int32]([1, 2])),
        ("Generic.Constrained[String]('a')", lambda: gen.Constrained[String]("a")),
        ("ResolverStatic.Mix(stat, 1)", lambda: ResolverStatic.Mix(stat, 1)),
        ("ResolverStatic.Mix(stat, 'a')", lambda: ResolverStatic.Mix(stat, "a")),
        ("ResolverStatic.OnlyStatic('a', b=1)", lambda: ResolverStatic.OnlyStatic("a", b=1)),
        ("ResolverHideDerived.Hidden(hide, 1)", lambda: ResolverHideDerived.Hidden(hide, 1)),
        ("Indexer[1] = 'v'", lambda: set_index(ResolverIndexer(), 1, "v", lambda t: t.Stored("int"))),
        ("Indexer['k'] = 'v'", lambda: set_index(ResolverIndexer(), "k", "v", lambda t: t.Stored("string"))),
        ("Indexer[1, 2] = 'v'", lambda: set_index(ResolverIndexer(), (1, 2), "v", lambda t: t.Stored("int,int"))),
        ("Indexer[1.5] = 'v'", lambda: set_index(ResolverIndexer(), 1.5, "v", lambda t: t.Stored("int"))),
        ("Indexer[None] = 'v'", lambda: set_index(ResolverIndexer(), None, "v", lambda t: t.Stored("string"))),
        ("IndexerValue[1] = 'v'", lambda: set_index(ResolverIndexerValue(), 1, "v", lambda t: t.Last)),
        ("IndexerValue[1] = 2.5", lambda: set_index(ResolverIndexerValue(), 1, 2.5, lambda t: t.Last)),
        ("IndexerValue[1.5] = 2", lambda: set_index(ResolverIndexerValue(), 1.5, 2, lambda t: t.Last)),
        ("IndexerValue[1.5] = 'v'", lambda: set_index(ResolverIndexerValue(), 1.5, "v", lambda t: t.Last)),
        ("Events.Changed += handler", fire_event),
        ("DelegateFactory.Out()(3)", lambda: ResolverDelegateFactory.Out()(3)),
        ("-Vec(1)", lambda: -ResolverVec(1.0)),
        ("-Op(5)", lambda: -ResolverOpBase(5)),
        ("+Op(5)", lambda: +ResolverOpBase(5)),
        ("~Op(5)", lambda: ~ResolverOpBase(5)),
        ("-OpDerived(5)", lambda: -ResolverOpDerived(5)),
        ("Op(5) | Op(3)", lambda: ResolverOpBase(5) | ResolverOpBase(3)),
        ("Op(5) ^ Op(3)", lambda: ResolverOpBase(5) ^ ResolverOpBase(3)),
        ("Op(5) >> 1", lambda: ResolverOpBase(5) >> 1),
        ("Op(5) <= Op(3)", lambda: ResolverOpBase(5) <= ResolverOpBase(3)),
        ("Op(5) > Op(3)", lambda: ResolverOpBase(5) > ResolverOpBase(3)),
        ("OpDerived(5) & OpDerived(3)", lambda: ResolverOpDerived(5) & ResolverOpDerived(3)),
        ("OpDerived(5) <= OpBase(3)", lambda: ResolverOpDerived(5) <= ResolverOpBase(3)),
        ("~Color.Red", lambda: ~ResolverColor.Red),
        ("AttributeTargets.Class | Method", lambda: System.AttributeTargets.Class | System.AttributeTargets.Method),
    ]


def run_sweep():
    """Calls every case; returns one 'name<TAB>outcome' line per call."""
    rows = []
    seen = set()
    duplicates = []

    def run(name, call):
        if name in seen:
            duplicates.append(name)
            return
        seen.add(name)
        sys.__stderr__.write("SWEEP " + name + "\n"); sys.__stderr__.flush()  # TEMP: find Linux segfault
        try:
            outcome = "OK\t" + describe(call())
        except Exception as e:
            outcome = "ERR\t" + describe_error(e)
        rows.append(name + "\t" + outcome)

    for name, member in one_arg_members():
        for label, make in VALUES:
            run("{0}({1})".format(name, label), lambda: member(make()))

    for name, member in two_arg_members():
        for la, ma in PAIRS:
            for lb, mb in PAIRS:
                run("{0}({1}, {2})".format(name, la, lb), lambda: member(ma(), mb()))

    for pattern, apply in OPERATORS:
        for label, make in VALUES:
            run(pattern.format(label), lambda: apply(make()))

    for name, call in single_calls():
        run(name, call)

    assert not duplicates, "duplicate sweep cases: " + ", ".join(duplicates)
    return rows


def test_resolver_sweep_matches_expected():
    actual = run_sweep()

    if os.environ.get("UPDATE_RESOLVER_SWEEP"):
        EXPECTED.write_text("\n".join(actual) + "\n", encoding="utf-8")
        pytest.skip("wrote " + str(EXPECTED))

    expected = EXPECTED.read_text(encoding="utf-8").splitlines()
    expected_by_name = dict(line.split("\t", 1) for line in expected)
    actual_by_name = dict(line.split("\t", 1) for line in actual)

    changed = [
        "{0}\n    expected: {1}\n    actual:   {2}".format(
            name, expected_by_name.get(name, "<missing>"), actual_by_name.get(name, "<missing>"))
        for name in sorted(set(expected_by_name) | set(actual_by_name))
        if expected_by_name.get(name) != actual_by_name.get(name)
    ]
    assert not changed, "{0} calls changed outcome:\n{1}".format(len(changed), "\n".join(changed[:50]))
