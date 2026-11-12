# -*- coding: utf-8 -*-

"""Time common calls from Python into .NET.

    uv run --no-sync pytest --runtime coreclr --perf

Only runs with --perf, which also builds in Release. Prints nanoseconds per call
at the end; nothing fails on speed, so compare against an earlier run.
"""

import time
import timeit

import pytest
from Python.Test import (
    ResolverArity, ResolverCtor, ResolverDerived, ResolverGeneric, ResolverHierarchy,
    ResolverParams, ResolverPrimitives, ResolverSequences, ResolverStatic, ResolverVec,
)
from Python.Test.RhinoLike import Point3d, Vector3d

pytestmark = pytest.mark.perf

prim = ResolverPrimitives()
arity = ResolverArity()
hier = ResolverHierarchy()
derived = ResolverDerived()
par = ResolverParams()
gen = ResolverGeneric()
seq = ResolverSequences()
v1 = ResolverVec(1.0)
v2 = ResolverVec(2.0)
items = [1, 2, 3]
p = Point3d(1.0, 2.0, 3.0)
q = Point3d(4.0, 5.0, 6.0)
v = Vector3d(1.0, 2.0, 3.0)

SHAPES = {
    "python only: lambda": lambda: None,
    "one overload: OnlyLong(5)": lambda: prim.OnlyLong(5),
    "one overload: SingleThree(1, 2.0, 'x')": lambda: arity.SingleThree(1, 2.0, "x"),
    "11 overloads: Pick(5)": lambda: prim.Pick(5),
    "11 overloads: Pick('x')": lambda: prim.Pick("x"),
    "11 overloads: Pick(1.5)": lambda: prim.Pick(1.5),
    "3 overloads: Mixed(1, 2.0)": lambda: prim.Mixed(1, 2.0),
    "class overloads: Pick(derived)": lambda: hier.Pick(derived),
    "constructor: ResolverCtor(1, 'x')": lambda: ResolverCtor(1, "x"),
    "operator: vec + vec": lambda: v1 + v2,
    "operator: vec * 2.0": lambda: v1 * 2.0,
    "static: OnlyStatic(5)": lambda: ResolverStatic.OnlyStatic(5),
    "static among instance: Mix(1, 2)": lambda: ResolverStatic.Mix(1, 2),
    "params: Ints(1, 2, 3)": lambda: par.Ints(1, 2, 3),
    "params: Prefer(1, 2, 3)": lambda: par.Prefer(1, 2, 3),
    "generic: Echo(5.0)": lambda: gen.Echo(5.0),
    "generic: Two(1, 'x')": lambda: gen.Two(1, "x"),
    "list: Arr([1, 2, 3])": lambda: seq.Arr(items),
    "Point3d: Point3d(1.0, 2.0, 3.0)": lambda: Point3d(1.0, 2.0, 3.0),
    "Point3d: p + q": lambda: p + q,
    "Point3d: p * 2.0": lambda: p * 2.0,
    "Point3d: p.DistanceTo(q)": lambda: p.DistanceTo(q),
    "Point3d: p[0]": lambda: p[0],
    "Point3d: p.X (property)": lambda: p.X,
    "Point3d: p.IsValid (property)": lambda: p.IsValid,
    "Vector3d: v.X (field)": lambda: v.X,
}

N = 2000
REPEAT = 40
WARMUP_SECONDS = 2.0


@pytest.fixture(scope="module", autouse=True)
def warm_up():
    # .NET first runs new code unoptimized and swaps in optimized code only after
    # it has been busy for a while; without this the first tests time the slow code
    end = time.perf_counter() + WARMUP_SECONDS
    while time.perf_counter() < end:
        for call in SHAPES.values():
            call()


@pytest.mark.parametrize("name", SHAPES)
def test_perf(name, perf_results):
    call = SHAPES[name]
    # best of many short runs, so a stray pause doesn't count
    best = min(timeit.timeit(call, number=N) for _ in range(REPEAT))
    perf_results[name] = best / N * 1e9
