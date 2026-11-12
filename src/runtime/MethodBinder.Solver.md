# MethodBinder.Solver: differences from IronPython

`MethodBinder.Solver.cs` picks which .NET overload a Python call runs. It follows IronPython's rules (`Converter.cs` and the DLR's `OverloadResolver.cs`): each argument is graded on IronPython's five narrowing levels, the overloads that need the least narrowing are compared argument by argument, and IronPython's tie-breaks settle the rest.

Each section below is one place where it deliberately does something else.

## Integers are checked by value

IronPython looks only at an argument's type. A Python int counts as an `Int32`, or as a `BigInteger` when it is too big. Here the actual number is checked too, so an overload that cannot hold it is skipped. A big int counts as the smallest of `Int64`, `UInt64` or `BigInteger` that holds it, so `2**40` goes to a `long` overload where IronPython would pick `double`.

Why: IronPython can pick an overload that then fails with an overflow error, even when another overload would have worked. Choosing `long` over `double` also keeps large whole numbers exact.

## Big ints keep a sized type when boxed

When a Python int too big for `Int32` goes to an `object` parameter, or sets a generic `T`, it becomes the smallest of `Int64`, `UInt64` or `BigInteger` that holds it, so `2**40` arrives as an `Int64`. IronPython always makes it a `BigInteger`.

Why: it matches how such an int counts when choosing an overload (see above), so the type used to choose is the type that arrives.

## Lists convert to arrays

IronPython only turns a tuple into a .NET array; a list must go to an `IList<T>` or `IEnumerable<T>` parameter. Here a list also converts to an array. Its first 16 items are checked, so `[1.0]` picks `double[]` over `int[]`. When several array types all fit, the one with the more specific item type wins, so `[1, 2]` picks `int[]` over `double[]`.

An iterator, such as `iter([1, 2])`, can also fill an array or `IList<T>`, but only when no overload takes an `IEnumerable<T>`.

A single list given to a `params` parameter fills the whole array, so `Lead("x", [1, 2])` on `Lead(string, params object[])` passes two items. IronPython passes the list as one item.

Why: pythonnet has always converted lists to arrays, and existing scripts and tests rely on it.

## A float can go into an int

IronPython never passes a Python float to an integer parameter. Here a float is truncated into an `int` parameter, but only when no overload takes a float.

Why: the old resolver allowed it and existing scripts depend on it. Making it the least preferred conversion means it never wins over an overload that takes the float as it is.

## Keywords fill their parameters first

In Python, `F(1, a=2)` is an error when `1` would already go to `a`. Here the keyword takes `a`, and `1` moves on to the next parameter.

Why: an upstream pythonnet test (`test_params_array_overload`) expects this, for example `F(1, i=1)` on `F(int i, params int[] rest)` puts `1` into `rest`.

## A plain value skips an `out` parameter

When a call gives a plain value where an `out` parameter sits, that parameter is left out and the value goes to the next parameter. A `StrongBox` is still accepted for the `out` parameter itself.

Why: this is how Python callers leave out `out` arguments in the middle of a call, and an upstream test (`test_method_tryout_multiple_signatures`) relies on it.

## Fewest default values wins a tie

IronPython's tie-break only asks whether an overload uses default values at all. Here, after IronPython's tie-breaks, the overload that leaves fewer parameters to their defaults wins.

Why: without it, `DefaultParamsWithOverloading(b=5)` is ambiguous between three overloads, and an upstream test (`test_default_params_overloads`) expects the one with the fewest defaults.

## Overrides and hidden methods

Two tie-breaks run after IronPython's. A method a Python subclass overrides beats the .NET method it replaces. A method hidden with `new` loses to the one hiding it.

Why: both cases exist only in pythonnet: Python subclasses of .NET classes, and both methods being listed together. Without these, either call would be ambiguous.

## Some conversions are not offered

IronPython lets Python numbers go into `decimal`, Python values into interfaces such as `IComparable`, any value into `bool`, and an int into an enum. These are never chosen here.

Why: pythonnet's converter cannot do these conversions, so choosing such an overload would only fail when the call is made.

## PyObject parameters and codecs

A parameter of type `PyObject` takes any argument as it is. A registered codec can convert a Python value that no other rule covers, such as a tuple into a `ValueTuple`. Both are tried only at the loosest level.

Why: these are pythonnet features IronPython does not have. Keeping them at the loosest level means they never beat a real conversion.

## A single overload is not graded

When only one overload has the right number of arguments, it is not graded; its arguments are simply converted.

Why: there is nothing to compare it with. If an argument does not fit, the conversion error says exactly which argument and why, which is clearer than "no overload matches".

## Some generic type rules are checked after choosing

As in IronPython, `T` counts as the argument's own type while choosing, and an overload whose `T` breaks the method's rules, such as "T must be a class", loses. When `T` comes from a list, a tuple or another Python value with no fixed .NET type, its rules are checked only after the overload is chosen.

Why: such a value's .NET type is known only once it is converted, which happens after choosing. A call like that fails with an error saying the type breaks the method's rules.

## Codecs work for operators

Operators such as `+` and `<` only convert their operands in small, safe ways, as in IronPython: `Op + 1.5` and `Vec + Convertible()` are refused. A registered codec can still convert an operand.

Why: codecs are a pythonnet feature, and upstream tests use them with operators.

## Explicit interface members

IronPython prefers an ordinary method over an explicit interface implementation. That rule is not added here.

Why: in pythonnet the two never appear in the same overload list, so the rule could never apply.
