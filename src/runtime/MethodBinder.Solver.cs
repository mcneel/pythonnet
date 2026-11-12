using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Python.Runtime
{
    // Picks the .NET overload for a call from Python, in four steps:
    //
    //   1. SHAPE   drop methods that cannot take this many args or these keywords.
    //   2. MATCH   assign args to parameters and grade each one (see MatchLevel).
    //   3. SELECT  keep candidates at the strictest grade any of them reached,
    //              then compare those head to head, argument by argument.
    //              A winner must be at least as good on every argument and
    //              better on one. When each is better somewhere, the first
    //              argument that differs decides. Otherwise tie-breaks apply.
    //   4. BIND    convert the winner's arguments and call it.
    //
    // Caching:
    //   - SHAPE: each method's parameters are read once and kept (MethodShape).
    //   - MATCH + SELECT: each method list remembers which overload it picked for
    //     the argument types of a call (CallKey). When picking had to look at an
    //     argument's value, not just its type, the pick is not remembered.
    //     ArgInfo enforces this: a rule can only reach a value through its Read
    //     methods, and everything else it offers is part of CallKey.
    partial class MethodBinder
    {
        #region Entry

        // `choices` holds earlier picks for this `methods` list; null means don't remember.
        // The first `skip` items of `args` are not arguments.
        static bool TryBind(MethodBase[] methods,
                            BorrowedReference args,
                            BorrowedReference kwargs,
                            int skip,
                            bool allowRedirected,
                            ChoiceCache? choices,
                            out BindSpec? spec,
                            out BindError? error)
        {
            var call = new CallArgs(args, kwargs, skip);

            // with a single method there is nothing to choose, so nothing to remember
            if (choices is null || methods.Length < 2)
            {
                return TrySolve(methods, ref call, allowRedirected, out spec, out error);
            }

            var key = new CallKey(ref call, allowRedirected);
            if (choices.TryGetValue(key, out Choice choice) && TryReuse(choice, ref call, out spec))
            {
                error = null;
                return true;
            }

            if (!TrySolve(methods, ref call, allowRedirected, out spec, out error))
            {
                return false;
            }

            if (!call.AnyValueRead)
            {
                choices.Add(key.Detached(), new Choice(spec!));
            }
            return true;
        }

        // Fits this call's arguments to an overload picked earlier, skipping the comparison.
        static bool TryReuse(Choice choice, ref CallArgs call, out BindSpec? spec)
        {
            if (!TryShape(choice.Method, ref call, out spec) || !TryAssign(spec!, ref call))
            {
                spec = null;
                return false;
            }

            foreach (BindParam slot in spec!.Parameters)
            {
                if (slot.Kind == BindParamKind.Params)
                {
                    slot.AsArray = choice.AsArray;
                }
            }
            return true;
        }

        static bool TrySolve(MethodBase[] methods,
                             ref CallArgs call,
                             bool allowRedirected,
                             out BindSpec? spec,
                             out BindError? error)
        {
            spec = null;
            error = null;

            var shaped = new List<BindSpec>(methods.Length);
            foreach (MethodBase method in methods)
            {
                if (!allowRedirected && MethodShape.Of(method).IsRedirected)
                {
                    continue;
                }

                if (!TryShape(method, ref call, out BindSpec? shape))
                {
                    continue;
                }

                // a parameterless method is the exact fit for a call without arguments
                if (call.Total == 0 && shape!.Parameters.Length == 0)
                {
                    spec = shape;
                    return true;
                }

                shaped.Add(shape!);
            }

            // with one method there is nothing to compare; conversion reports
            // what is wrong, which reads better than "no overload"
            bool grade = shaped.Count > 1 || (shaped.Count == 1 && shaped[0].IsOperator);

            var candidates = new List<Candidate>(shaped.Count);
            foreach (BindSpec shape in shaped)
            {
                if (TryMatch(shape, ref call, grade, out Candidate? candidate))
                {
                    candidates.Add(candidate!);
                }
            }

            return TrySelect(candidates, out spec, out error);
        }

        #endregion

        #region Cache

        // What the pick depends on when no argument's value was read: whether redirected
        // methods may compete, the keyword names and, for each argument, everything
        // ArgInfo tells without reading the value.
        // Made per call around that call's own arguments; `Detached` gives one that can be kept.
        private readonly struct CallKey : IEquatable<CallKey>
        {
            readonly bool _allowRedirected;
            readonly string[] _kwNames;
            readonly ArgInfo[] _args;
            readonly int _hash;

            public CallKey(ref CallArgs call, bool allowRedirected)
                : this(allowRedirected, call.KwNames, call.Infos())
            {
            }

            CallKey(bool allowRedirected, string[] kwNames, ArgInfo[] args)
            {
                _allowRedirected = allowRedirected;
                _kwNames = kwNames;
                _args = args;

                int hash = allowRedirected ? 1 : 0;
                foreach (ArgInfo arg in args)
                {
                    hash = (hash * 31) ^ arg.FactsHash;
                }
                foreach (string name in kwNames)
                {
                    hash = (hash * 31) ^ name.GetHashCode();
                }
                _hash = hash;
            }

            // a copy that does not hold on to the call's argument values
            public CallKey Detached() => new(_allowRedirected, _kwNames, Array.ConvertAll(_args, a => a.FactsOnly()));

            public override int GetHashCode() => _hash;

            public override bool Equals(object obj) => obj is CallKey other && Equals(other);

            public bool Equals(CallKey other)
            {
                if (_hash != other._hash || _allowRedirected != other._allowRedirected
                        || _args.Length != other._args.Length || _kwNames.Length != other._kwNames.Length)
                {
                    return false;
                }

                for (int i = 0; i < _args.Length; i++)
                {
                    if (!_args[i].SameFacts(other._args[i]))
                    {
                        return false;
                    }
                }

                for (int i = 0; i < _kwNames.Length; i++)
                {
                    if (_kwNames[i] != other._kwNames[i])
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        // An earlier pick, and whether its params[] parameter took a passed array as-is.
        private readonly struct Choice
        {
            public readonly MethodBase Method;
            public readonly bool AsArray;

            public Choice(BindSpec spec)
            {
                Method = spec.Method;
                AsArray = Array.Exists(spec.Parameters, p => p.Kind == BindParamKind.Params && p.AsArray);
            }
        }

        // Earlier picks for one method list. Safe across threads; emptied once it holds 64.
        private sealed class ChoiceCache
        {
            const int Limit = 64;

            readonly ConcurrentDictionary<CallKey, Choice> _choices = new();

            public bool TryGetValue(CallKey key, out Choice choice) => _choices.TryGetValue(key, out choice);

            public void Add(CallKey key, Choice choice)
            {
                // counting is slow, but this only runs after a full pick
                if (_choices.Count >= Limit)
                {
                    _choices.Clear();
                }
                _choices[key] = choice;
            }
        }

        #endregion

        #region Call arguments

        // Python args and kwargs of one call. Lives for that call only.
        // Arguments are numbered positional first, then keywords in dict order.
        private ref struct CallArgs
        {
            readonly BorrowedReference _args;
            readonly BorrowedReference _kwargs;
            readonly ArgInfo?[] _infos;
            readonly int _skip;   // leading items of args that are not arguments

            public readonly int Count;
            public readonly string[] KwNames;

            public CallArgs(BorrowedReference args, BorrowedReference kwargs, int skip)
            {
                _args = args;
                _kwargs = kwargs;
                _skip = skip;
                Count = args == null ? 0 : Math.Max(0, (int)Runtime.PyTuple_Size(args) - skip);
                KwNames = kwargs == null ? Array.Empty<string>() : GetKeywordNames(kwargs);
                _infos = new ArgInfo?[Count + KwNames.Length];
            }

            public readonly int Total => Count + KwNames.Length;

            public readonly BorrowedReference this[int index]
                => index < Count
                    ? Runtime.PyTuple_GetItem(_args, index + _skip)
                    : Runtime.PyDict_GetItemString(_kwargs, KwNames[index - Count]);

            // what kind of value an argument is; worked out once per call
            public readonly ArgInfo Info(int index)
                => _infos[index] ??= ArgInfo.Of(this[index]);

            // every argument's Info; the array belongs to this call
            public readonly ArgInfo[] Infos()
            {
                for (int i = 0; i < _infos.Length; i++)
                {
                    _infos[i] ??= ArgInfo.Of(this[i]);
                }
                return _infos!;
            }

            // true when picking looked at an argument's value, not just its type
            public readonly bool AnyValueRead
            {
                get
                {
                    foreach (ArgInfo? info in _infos)
                    {
                        if (info is { ValueRead: true })
                        {
                            return true;
                        }
                    }
                    return false;
                }
            }

            public readonly int KwIndex(string name)
            {
                for (int i = 0; i < KwNames.Length; i++)
                {
                    if (KwNames[i] == name)
                    {
                        return Count + i;
                    }
                }
                return -1;
            }

            static string[] GetKeywordNames(BorrowedReference kwargs)
            {
                int count = (int)Runtime.PyDict_Size(kwargs);
                if (count <= 0)
                {
                    return Array.Empty<string>();
                }

                var names = new string[count];
                using NewReference keys = Runtime.PyDict_Keys(kwargs);
                for (int i = 0; i < count; i++)
                {
                    names[i] = Runtime.GetManagedString(Runtime.PyList_GetItem(keys.Borrow(), i))!;
                }
                return names;
            }
        }

        private enum ArgKind : byte
        {
            None,       // Python None
            Clr,        // wrapped .NET object, or a .NET type
            Bool,
            Int,
            Float,
            Str,
            Bytes,
            Sequence,   // list, tuple, and other sequences
            Iterator,   // generators and other one-shot iterators; never read ahead
            Other,
        }

        // A Python argument, classified once so each overload can grade it cheaply.
        // NOTE: whatever is public here, other than the Read methods, must be part of
        // SameFacts and FactsHash; otherwise a remembered pick is reused for calls it does not fit.
        private sealed class ArgInfo
        {
            public ArgKind Kind { get; private set; }
            public Type From { get; private set; } = typeof(object); // .NET type it counts as; for Python values, its stand-in
            public IntPtr PyType { get; private set; }               // its Python type: codecs go by it
            public bool IsSingle { get; private set; }               // Str, Bytes and .NET strings: exactly one long

            IntPtr _python;
            object? _clr;      // for Clr: the .NET object
            int _intFits;      // for Int: one bit per integer TypeCode it fits

            // a rule read this argument's value, so the pick can't be reused for other values
            public bool ValueRead { get; private set; }

            public BorrowedReference ReadPython()
            {
                ValueRead = true;
                return new BorrowedReference(_python);
            }

            public object? ReadClr()
            {
                ValueRead = true;
                return _clr;
            }

            // Equality over what rules use without reading the value.
            public bool SameFacts(ArgInfo other)
                => Kind == other.Kind && From == other.From && PyType == other.PyType
                    && IsSingle == other.IsSingle && _intFits == other._intFits;

            public int FactsHash
                => From.GetHashCode() ^ PyType.GetHashCode() ^ ((int)Kind << 24) ^ _intFits ^ (IsSingle ? 1 : 0);

            // a copy without the value, safe to keep after the call
            public ArgInfo FactsOnly()
            {
                var copy = (ArgInfo)MemberwiseClone();
                copy._python = IntPtr.Zero;
                copy._clr = null;
                return copy;
            }

            public static ArgInfo Of(BorrowedReference arg)
            {
                var info = new ArgInfo { Kind = ArgKind.Other };

                if (arg == null || arg == Runtime.PyNone)
                {
                    info.Kind = ArgKind.None;
                    return info;
                }

                info._python = arg.DangerousGetAddress();
                info.PyType = Runtime.PyObject_TYPE(arg).DangerousGetAddress();

                switch (ManagedType.GetManagedObject(arg))
                {
                    case CLRObject co:
                        info.Kind = ArgKind.Clr;
                        info._clr = co.inst;
                        info.From = co.inst?.GetType() ?? typeof(object);
                        info.IsSingle = co.inst is string { Length: 1 };
                        return info;

                    case ClassBase cb when cb.type.Valid:
                        info.Kind = ArgKind.Clr;
                        info._clr = cb.type.Value;
                        info.From = cb.type.Value.GetType();
                        return info;
                }

                // bool before int: a Python bool is also an int
                if (Runtime.PyBool_Check(arg))
                {
                    info.Kind = ArgKind.Bool;
                    info.From = typeof(bool);
                }
                else if (Runtime.PyInt_Check(arg))
                {
                    info.Kind = ArgKind.Int;
                    ReadInt(arg, info);
                }
                else if (Runtime.PyFloat_Check(arg))
                {
                    info.Kind = ArgKind.Float;
                    info.From = typeof(double);
                }
                else if (Runtime.PyString_Check(arg))
                {
                    info.Kind = ArgKind.Str;
                    info.From = typeof(string);
                    info.IsSingle = Runtime.PyUnicode_GetLength(arg) == 1;
                }
                else if (Runtime.PyObject_TypeCheck(arg, Runtime.PyBytesType))
                {
                    info.Kind = ArgKind.Bytes;
                    info.IsSingle = Runtime.PyBytes_Size(arg) == 1;
                }
                else if (Runtime.PySequence_Check(arg))
                {
                    info.Kind = ArgKind.Sequence;
                }
                else if (Runtime.PyIter_Check(arg))
                {
                    info.Kind = ArgKind.Iterator;
                }

                return info;
            }

            static void ReadInt(BorrowedReference arg, ArgInfo info)
            {
                // stands in as the smallest of Int32, Int64, UInt64 or BigInteger that holds it
                if (Runtime.PyLong_AsLongLong(arg) is long signed)
                {
                    info._intFits = FitsOf(signed);
                    info.From = signed >= int.MinValue && signed <= int.MaxValue ? typeof(int) : typeof(long);
                    return;
                }
                Exceptions.Clear();

                if (Runtime.PyLong_AsUnsignedLongLong(arg) is ulong unsigned)
                {
                    info._intFits = 1 << (int)TypeCode.UInt64;
                    info.From = typeof(ulong);
                    return;
                }
                Exceptions.Clear();

                info.From = typeof(System.Numerics.BigInteger);
            }

            // whether a Python int fits an integer type
            public bool IntFits(TypeCode code) => (_intFits & (1 << (int)code)) != 0;

            static int FitsOf(long v)
            {
                int fits = 1 << (int)TypeCode.Int64;
                if (v >= 0) fits |= 1 << (int)TypeCode.UInt64;
                if (v >= int.MinValue && v <= int.MaxValue) fits |= 1 << (int)TypeCode.Int32;
                if (v >= uint.MinValue && v <= uint.MaxValue) fits |= 1 << (int)TypeCode.UInt32;
                if (v >= short.MinValue && v <= short.MaxValue) fits |= 1 << (int)TypeCode.Int16;
                if (v >= ushort.MinValue && v <= ushort.MaxValue) fits |= (1 << (int)TypeCode.UInt16) | (1 << (int)TypeCode.Char);
                if (v >= sbyte.MinValue && v <= sbyte.MaxValue) fits |= 1 << (int)TypeCode.SByte;
                if (v >= byte.MinValue && v <= byte.MaxValue) fits |= 1 << (int)TypeCode.Byte;
                return fits;
            }
        }

        #endregion

        #region Shape (step 1)

        // How a parameter takes its argument.
        private enum BindParamKind
        {
            Default,        // plain required parameter
            Option,         // has a default value, or [Optional]
            Params,         // trailing params T[]; collects extra positional args
            BoxedReturn,    // `out`; may also take a StrongBox<T> to write into
            Return,         // `ref`; required, and its new value is returned
            Self,           // operator operand filled with the instance
        }

        // Returns false when the method cannot take this many args or these keywords.
        static bool TryShape(MethodBase method, ref CallArgs call, out BindSpec? spec)
        {
            spec = null;

            MethodShape shape = MethodShape.Of(method);
            ParameterInfo[] mparams = shape.Parameters;

            if (shape.IsOperator)
            {
                if (shape.NeverMatches || call.Total != shape.Required)
                {
                    return false;
                }
            }
            else
            {
                // a keyword that names no parameter of this method
                int kwLeft = call.KwNames.Length;
                for (int i = 0; i < mparams.Length && kwLeft > 0; i++)
                {
                    if (call.KwIndex(mparams[i].Name) >= 0)
                    {
                        kwLeft--;
                    }
                }
                if (kwLeft > 0)
                {
                    return false;
                }

                uint given = (uint)call.Total;
                uint required = shape.Required;
                bool fits = given == required
                         || (required < given && (given <= required + shape.Optional + shape.Returns || shape.Expands));
                if (!fits)
                {
                    return false;
                }
            }

            var argSpecs = new BindParam[shape.Kinds.Length];
            for (int i = 0; i < argSpecs.Length; i++)
            {
                argSpecs[i] = new BindParam(mparams[i], shape.Kinds[i]);
            }

            spec = new BindSpec(method, argSpecs, shape.Required, shape.Optional, shape.Returns, shape.Expands);
            return true;
        }

        // A method's parameters, read once per method because reflection is slow.
        private sealed class MethodShape
        {
            static readonly ConditionalWeakTable<MethodBase, MethodShape> s_cache = new();
            static readonly ConditionalWeakTable<MethodBase, MethodShape>.CreateValueCallback s_create = m => new MethodShape(m);

            public static MethodShape Of(MethodBase method) => s_cache.GetValue(method, s_create);

            public readonly ParameterInfo[] Parameters;
            public readonly BindParamKind[] Kinds;
            public readonly uint Required;
            public readonly uint Optional;
            public readonly uint Returns;
            public readonly bool Expands;
            public readonly bool IsOperator;
            public readonly bool NeverMatches;  // reversed comparison operators are never picked
            public readonly bool IsRedirected;
            public readonly bool IsExtension;

            MethodShape(MethodBase method)
            {
                Parameters = method.GetParameters();
                Kinds = new BindParamKind[Parameters.Length];
                IsRedirected = method.GetCustomAttribute<RedirectedMethod>() != null;
                IsExtension = method.IsDefined(typeof(ExtensionAttribute), false);

                ParameterInfo[] mparams = Parameters;

                if (OperatorMethod.IsOperatorMethod(method))
                {
                    IsOperator = true;
                    bool isReverse = OperatorMethod.IsReverse(method);
                    NeverMatches = isReverse && OperatorMethod.IsComparisonOp(method);

                    if (mparams.Length == 2)
                    {
                        Required = 1;
                        Kinds[0] = isReverse ? BindParamKind.Default : BindParamKind.Self;
                        Kinds[1] = isReverse ? BindParamKind.Self : BindParamKind.Default;
                    }
                    else
                    {
                        // operators always have at least one operand
                        Kinds = new[] { BindParamKind.Self };
                    }
                    return;
                }

                if (mparams.Length == 0)
                {
                    return;
                }

                int last = mparams.Length - 1;
                Expands = Attribute.IsDefined(mparams[last], typeof(ParamArrayAttribute));

                for (int i = 0; i < mparams.Length; i++)
                {
                    ParameterInfo param = mparams[i];

                    // `.IsOut` is false for `ref`. [In][Out] arrays (e.g. Stream.Read) are inputs.
                    if (param.IsOut && param.GetCustomAttribute<InAttribute>() is null)
                    {
                        Kinds[i] = BindParamKind.BoxedReturn;
                        Returns++;
                    }
                    else if (param.ParameterType.IsByRef)
                    {
                        Kinds[i] = param.IsOut ? BindParamKind.BoxedReturn : BindParamKind.Return;
                        Returns++;
                        Required++;
                    }
                    else if (param.IsOptional)
                    {
                        Kinds[i] = BindParamKind.Option;
                        Optional++;
                    }
                    else if (Expands && i == last)
                    {
                        Kinds[i] = BindParamKind.Params;
                    }
                    else
                    {
                        Kinds[i] = BindParamKind.Default;
                        Required++;
                    }
                }
            }
        }

        #endregion

        #region Match (step 2)

        // How much an argument must be narrowed to fit a parameter. Lower is stricter.
        // Same levels as IronPython's PythonNarrowing, and the same conversions on each.
        private enum Narrowing : byte
        {
            None,           // same type, subclass to base, implicit numeric (int to long), None to a class
            Minimal,        // number to double or decimal
            BinaryOperator, // number to float, T to T?, sequence to array or IList<T>
            IndexOperator,  // narrowing numbers, str to char, callable to delegate,
                            // iterable to IEnumerable, user implicit operators
            All,            // any number to any number, except floating point to integer;
                            // also pythonnet's own: float to int, PyObject, codecs
            Never,          // cannot fit
        }

        // Items of a Python sequence checked when grading it against a collection.
        // A later item that does not fit is reported when the argument is converted.
        const int SequenceSample = 16;

        // A method that passed SHAPE, with its arguments assigned and graded.
        private sealed class Candidate
        {
            public readonly BindSpec Spec;
            public readonly Narrowing[] Levels; // per argument number
            public readonly Type[] Targets;     // parameter type each argument went to
            public Narrowing Level;             // worst of Levels

            public Candidate(BindSpec spec, int argCount)
            {
                Spec = spec;
                Levels = new Narrowing[argCount];
                Targets = new Type[argCount];
            }

            public void Grade(int argIndex, Narrowing level, Type target)
            {
                Levels[argIndex] = level;
                Targets[argIndex] = target;
                if (level > Level)
                {
                    Level = level;
                }
            }
        }

        // Assigns the arguments (TryAssign), then grades them when `grade` is set.
        // Returns false when they cannot be assigned or one of them cannot fit.
        static bool TryMatch(BindSpec spec, ref CallArgs call, bool grade, out Candidate? candidate)
        {
            candidate = null;
            if (!TryAssign(spec, ref call))
            {
                return false;
            }

            var result = new Candidate(spec, call.Total);
            if (grade)
            {
                foreach (BindParam slot in spec.Parameters)
                {
                    if (!TryGrade(slot, ref call, result))
                    {
                        return false;
                    }
                }

                if (spec.IsOperator && !IsSafeOperand(result, ref call))
                {
                    return false;
                }

                if (!SatisfiesGenericRules(spec, ref call))
                {
                    return false;
                }
            }

            candidate = result;
            return true;
        }

        // Assigns args and kwargs to the method's slots. False when they don't fit the slots.
        static bool TryAssign(BindSpec spec, ref CallArgs call)
        {
            BindParam[] slots = spec.Parameters;

            // keywords take their slots first; positional arguments fill the rest
            // in order, so `F(1, i=1)` on F(i, params int[] rest) puts 1 in rest
            int next = 0;
            foreach (BindParam slot in slots)
            {
                if (slot.Kind == BindParamKind.Self)
                {
                    continue;
                }

                int kw = call.KwIndex(slot.Key);
                if (kw >= 0)
                {
                    if (slot.Kind == BindParamKind.Params)
                    {
                        slot.AssignArray(call[kw], kw);
                    }
                    else if (!slot.TryAssign(call[kw], kw))
                    {
                        return false;
                    }
                    continue;
                }

                if (next == call.Count)
                {
                    continue;
                }

                if (slot.Kind == BindParamKind.Params)
                {
                    slot.AssignItems(ref call, next, call.Count - next);
                    next = call.Count;
                }
                // an `out` slot given a plain value is left out; the value goes to the next slot
                else if (slot.TryAssign(call[next], next))
                {
                    next++;
                }
                else if (slot.Kind != BindParamKind.BoxedReturn)
                {
                    return false;
                }
            }

            if (next < call.Count)
            {
                return false;
            }

            foreach (BindParam slot in slots)
            {
                if (!slot.IsAssigned && (slot.Kind == BindParamKind.Default || slot.Kind == BindParamKind.Return))
                {
                    return false;
                }
            }
            return true;
        }

        // As IronPython, an operator's operand is converted only in small, safe ways.
        // Codecs and PyObject parameters are pythonnet's own and still allowed.
        static bool IsSafeOperand(Candidate candidate, ref CallArgs call)
        {
            for (int i = 0; i < candidate.Levels.Length; i++)
            {
                if (candidate.Levels[i] <= Narrowing.BinaryOperator)
                {
                    continue;
                }

                ArgInfo info = call.Info(i);
                Type target = candidate.Targets[i];
                bool lenientFloat = info.Kind == ArgKind.Float && IsInteger(Nullable.GetUnderlyingType(target) ?? target);
                if (lenientFloat || GetTypeNarrowing(info, target) != Narrowing.Never)
                {
                    return false;
                }
            }
            return true;
        }

        // As IronPython, a generic method whose T would break its rules
        // (e.g. `where T : class`) loses, so another overload can win.
        static bool SatisfiesGenericRules(BindSpec spec, ref CallArgs call)
        {
            if (spec.Method is not MethodInfo { IsGenericMethodDefinition: true } method)
            {
                return true;
            }

            var inferred = new Type?[method.GetGenericArguments().Length];
            foreach (BindParam slot in spec.Parameters)
            {
                if (!slot.IsAssigned || slot.Kind is BindParamKind.Self or BindParamKind.BoxedReturn)
                {
                    continue;
                }

                Type type = slot.Kind == BindParamKind.Params && !slot.AsArray ? slot.Type : slot.ParameterType;
                if (type.IsByRef)
                {
                    type = type.GetElementType();
                }

                // only arguments whose own .NET type is known for sure
                ArgInfo info = call.Info(slot.ArgIndex);
                if (type.ContainsGenericParameters
                        && info.Kind is ArgKind.Clr or ArgKind.Bool or ArgKind.Int or ArgKind.Float or ArgKind.Str)
                {
                    Unify(type, info.From, inferred);
                }
            }

            if (Array.IndexOf(inferred, null) >= 0)
            {
                // not every T is known yet; checked when the call is made
                return true;
            }

            try
            {
                method.MakeGenericMethod(inferred!);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        // Grades the arguments in one slot. False when one of them cannot fit.
        static bool TryGrade(BindParam slot, ref CallArgs call, Candidate candidate)
        {
            if (!slot.IsAssigned || slot.Kind == BindParamKind.Self)
            {
                return true;
            }

            int index = slot.ArgIndex;

            if (slot.Kind == BindParamKind.BoxedReturn || slot.HasBox)
            {
                // assignment already checked that it is a fitting StrongBox
                candidate.Grade(index, Narrowing.None, slot.ParameterType);
                return true;
            }

            if (slot.Kind != BindParamKind.Params)
            {
                ArgInfo info = call.Info(index);
                Narrowing level = GetNarrowing(info, slot.ParameterType);

                // a bare T becomes the argument's own type, so Echo<T>(Int16) beats Echo(int)
                Type target = slot.ParameterType.IsGenericParameter && info.Kind != ArgKind.None
                    ? info.From
                    : slot.ParameterType;
                candidate.Grade(index, level, target);
                return level != Narrowing.Never;
            }

            // params[] given one sequence or array: prefer reading it as the whole array
            if (slot.ItemCount == 1)
            {
                ArgInfo info = call.Info(index);
                if (slot.ByKeyword || info.Kind is ArgKind.Sequence or ArgKind.Clr)
                {
                    Narrowing asArray = GetNarrowing(info, slot.ParameterType);
                    if (asArray != Narrowing.Never || slot.ByKeyword)
                    {
                        slot.AsArray = true;
                        candidate.Grade(index, asArray, slot.ParameterType);
                        return asArray != Narrowing.Never;
                    }
                }
            }

            slot.AsArray = false;
            for (int i = 0; i < slot.ItemCount; i++)
            {
                Narrowing level = GetNarrowing(call.Info(index + i), slot.Type);
                candidate.Grade(index + i, level, slot.Type);
                if (level == Narrowing.Never)
                {
                    return false;
                }
            }

            return true;
        }

        // Least narrowing at which one argument fits one parameter type.
        static Narrowing GetNarrowing(ArgInfo arg, Type to)
        {
            if (to.IsByRef)
            {
                to = to.GetElementType();
            }

            if (arg.Kind == ArgKind.None)
            {
                bool takesNull = !to.IsValueType || Nullable.GetUnderlyingType(to) is not null || to.IsGenericParameter;
                return takesNull ? Narrowing.None : Narrowing.Never;
            }

            // T is inferred from the argument's own type
            if (to.IsGenericParameter)
            {
                return Narrowing.None;
            }

            if (to.ContainsGenericParameters)
            {
                return arg.Kind == ArgKind.Clr && Unify(to, arg.From, new Type?[GenericArity(to)])
                    ? Narrowing.None
                    : Narrowing.Never;
            }

            Narrowing level = GetTypeNarrowing(arg, to);
            if (level == Narrowing.Never && arg.Kind != ArgKind.Clr)
            {
                level = GetPythonNarrowing(arg, to);
            }

            // pythonnet passes anything as a PyObject
            if (level == Narrowing.Never && typeof(PyObject).IsAssignableFrom(to))
            {
                level = Narrowing.All;
            }

            return level;
        }

        // Narrowing between .NET types, as IronPython's Converter.CanConvertFrom.
        // A Python value takes part as its own .NET type (int as Int32, float as Double),
        // limited to what pythonnet's converter can do with it.
        static Narrowing GetTypeNarrowing(ArgInfo arg, Type to)
        {
            Type from = arg.From;
            bool python = arg.Kind != ArgKind.Clr;

            if (from == to)
            {
                return Narrowing.None;
            }

            if (to.IsAssignableFrom(from))
            {
                // the converter boxes Python values only as `object`
                if (!python || to == typeof(object))
                {
                    return Narrowing.None;
                }
            }

            // the converter turns Python numbers into decimal never
            if (python && to == typeof(decimal))
            {
                return Narrowing.Never;
            }

            if (Nullable.GetUnderlyingType(to) is Type underlying)
            {
                Narrowing inner = GetTypeNarrowing(arg, underlying);
                return inner == Narrowing.Never ? Narrowing.Never : Max(inner, Narrowing.BinaryOperator);
            }

            if (IsImplicitNumeric(from, to))
            {
                return Narrowing.None;
            }

            bool fromNumeric = IsNumeric(from) || from == typeof(bool);

            if (IsNumeric(from) && IsFloatingPoint(to) && to != typeof(float))
            {
                return Narrowing.Minimal;
            }

            if (IsNumeric(from) && to == typeof(float))
            {
                return Narrowing.BinaryOperator;
            }

            if (fromNumeric && IsInteger(from) || from == typeof(bool))
            {
                if (IsNumeric(to))
                {
                    return ValueFits(arg, to) ? Narrowing.IndexOperator : Narrowing.Never;
                }
            }

            if (to == typeof(char) && from == typeof(string))
            {
                return arg.IsSingle ? Narrowing.IndexOperator : Narrowing.Never;
            }

            if (to == typeof(string) && from == typeof(char))
            {
                return Narrowing.IndexOperator;
            }

            if (!python && HasImplicitOperator(from, to))
            {
                return Narrowing.IndexOperator;
            }

            if (IsNumeric(from) && IsNumeric(to) && !(IsFloatingPoint(from) && !IsFloatingPoint(to)))
            {
                return ValueFits(arg, to) ? Narrowing.All : Narrowing.Never;
            }

            return Narrowing.Never;
        }

        // Conversions only Python values have: collections, delegates, and pythonnet's own.
        static Narrowing GetPythonNarrowing(ArgInfo arg, Type to)
        {
            switch (arg.Kind)
            {
                case ArgKind.Float:
                    // truncated; kept so older scripts work, but never preferred
                    return to == typeof(int) || to == typeof(int?) ? Narrowing.All : Narrowing.Never;

                case ArgKind.Str:
                    // a str is a sequence of chars: IEnumerable<char> and the like
                    return to.IsAssignableFrom(typeof(string)) && TryGetElementType(to, out _)
                        ? Narrowing.None
                        : Narrowing.Never;

                case ArgKind.Bool:
                case ArgKind.Int:
                    // only a codec can take an int the type rules refused; whether one
                    // exists goes by type, so the value is read only when it does
                    var pyType = new BorrowedReference(arg.PyType);
                    return PyObjectConversions.HasDecoder(pyType, to)
                            && PyObjectConversions.TryDecode(arg.ReadPython(), pyType, to, out _)
                        ? Narrowing.All
                        : Narrowing.Never;

                case ArgKind.Bytes:
                    if (arg.IsSingle && (to == typeof(byte) || to == typeof(sbyte) || to == typeof(char)))
                    {
                        return Narrowing.IndexOperator;
                    }
                    goto case ArgKind.Sequence;

                case ArgKind.Sequence:
                    if (TryGetElementType(to, out _))
                    {
                        return GetCollectionNarrowing(arg, to, readItems: true);
                    }
                    // not a collection type: a codec may still take it, e.g. tuple to ValueTuple
                    break;

                case ArgKind.Iterator:
                    // never handed to the converter here: that could read it
                    return GetCollectionNarrowing(arg, to, readItems: false);
            }

            // whether it can be called, and what the converter or a codec makes of it, depend on the value
            BorrowedReference value = arg.ReadPython();

            if (typeof(Delegate).IsAssignableFrom(to))
            {
                return Runtime.PyCallable_Check(value) == 1 ? Narrowing.IndexOperator : Narrowing.Never;
            }

            // only the converter (or a codec) knows how
            bool converts = Converter.ToManaged(value, to, out _, setError: false);
            if (Exceptions.ErrorOccurred())
            {
                Exceptions.Clear();
            }
            return converts ? Narrowing.All : Narrowing.Never;
        }

        // A Python iterable to an array (pythonnet's own; IronPython takes only tuples),
        // IList<T>, or IEnumerable. Items are graded too, so [1.0] fits double[] but not int[].
        static Narrowing GetCollectionNarrowing(ArgInfo arg, Type to, bool readItems)
        {
            if (!TryGetElementType(to, out Type? element))
            {
                return Narrowing.Never;
            }

            bool listLike = to.IsArray || IsGenericList(to);

            // a one-shot iterator is never read ahead, and fits a list only after being read in full
            if (!readItems)
            {
                return listLike ? Narrowing.All : Narrowing.IndexOperator;
            }

            Narrowing level = listLike ? Narrowing.BinaryOperator : Narrowing.IndexOperator;

            BorrowedReference value = arg.ReadPython();
            nint size = Runtime.PySequence_Size(value);
            if (size < 0)
            {
                Exceptions.Clear();
                return level;
            }

            int sample = (int)Math.Min(size, SequenceSample);
            for (int i = 0; i < sample; i++)
            {
                using NewReference item = Runtime.PySequence_GetItem(value, i);
                if (item.IsNull())
                {
                    Exceptions.Clear();
                    return Narrowing.Never;
                }

                Narrowing itemLevel = GetNarrowing(ArgInfo.Of(item.Borrow()), element!);
                if (itemLevel == Narrowing.Never)
                {
                    return Narrowing.Never;
                }
                level = Max(level, itemLevel);
            }

            return level;
        }

        static bool IsGenericList(Type to)
            => to.IsGenericType && to.GetGenericTypeDefinition() == typeof(IList<>);

        static Narrowing Max(Narrowing a, Narrowing b) => a > b ? a : b;

        // Element type of a collection the converter can build from a Python iterable.
        static bool TryGetElementType(Type to, out Type? element)
        {
            element = null;

            if (to.IsArray)
            {
                element = to.GetArrayRank() == 1 ? to.GetElementType() : null;
            }
            else if (to == typeof(IEnumerable))
            {
                element = typeof(object);
            }
            else if (to.IsGenericType
                        && to.GenericTypeArguments.Length == 1
                        && typeof(IEnumerable).IsAssignableFrom(to))
            {
                // the converter builds an array, so the type must take one
                Type item = to.GenericTypeArguments[0];
                if (to.IsAssignableFrom(item.MakeArrayType()))
                {
                    element = item;
                }
            }

            return element is not null;
        }

        // Whether a value can be narrowed to a number type without overflow.
        static bool ValueFits(ArgInfo arg, Type to)
        {
            TypeCode code = Type.GetTypeCode(to);
            if (code is TypeCode.Single or TypeCode.Double or TypeCode.Decimal)
            {
                return true;
            }

            if (arg.Kind == ArgKind.Int)
            {
                return arg.IntFits(code);
            }

            if (arg.From == typeof(bool))
            {
                return true;
            }

            // a .NET number: check its value
            try
            {
                Convert.ChangeType(arg.ReadClr(), to, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception ex) when (ex is OverflowException or InvalidCastException or FormatException)
            {
                return false;
            }
        }

        // User-defined implicit operator from `from` to `to`, on either type or their bases.
        static bool HasImplicitOperator(Type from, Type to)
            => HasImplicitOperatorOn(from, from, to) || HasImplicitOperatorOn(to, from, to);

        static bool HasImplicitOperatorOn(Type? owner, Type from, Type to)
        {
            // primitives, strings and enums declare no operators
            for (; owner is not null && !owner.IsPrimitive && owner != typeof(string) && owner != typeof(object) && !owner.IsEnum; owner = owner.BaseType)
            {
                foreach (MethodInfo m in owner.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (m.Name == "op_Implicit"
                            && to.IsAssignableFrom(m.ReturnType)
                            && m.GetParameters() is { Length: 1 } ps
                            && ps[0].ParameterType.IsAssignableFrom(from))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        static bool IsNumeric(Type t)
        {
            if (t.IsEnum)
            {
                return false;
            }

            return Type.GetTypeCode(t) switch
            {
                TypeCode.SByte or TypeCode.Byte or TypeCode.Int16 or TypeCode.UInt16
                    or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64
                    or TypeCode.Single or TypeCode.Double or TypeCode.Decimal => true,
                TypeCode.Object => t == typeof(System.Numerics.BigInteger),
                _ => false,
            };
        }

        static bool IsInteger(Type t)
            => !t.IsEnum && (Type.GetTypeCode(t) is TypeCode.SByte or TypeCode.Byte or TypeCode.Int16 or TypeCode.UInt16
                    or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64
               || t == typeof(System.Numerics.BigInteger));

        static bool IsFloatingPoint(Type t)
            => Type.GetTypeCode(t) is TypeCode.Single or TypeCode.Double or TypeCode.Decimal;

        // Implicit numeric conversions, as IronPython: C#'s, plus bool as Int32,
        // integers to BigInteger, and BigInteger to double.
        static bool IsImplicitNumeric(Type from, Type to)
        {
            if (from.IsEnum || to.IsEnum)
            {
                return false;
            }

            if (from == typeof(System.Numerics.BigInteger))
            {
                return to == typeof(double);
            }

            if (from == typeof(bool))
            {
                return to == typeof(int) || IsImplicitNumeric(typeof(int), to);
            }

            if (to == typeof(System.Numerics.BigInteger))
            {
                return IsInteger(from) || from == typeof(char);
            }

            TypeCode f = Type.GetTypeCode(from);
            TypeCode t = Type.GetTypeCode(to);
            return f switch
            {
                TypeCode.SByte => t is TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal,
                TypeCode.Byte => t is TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal,
                TypeCode.Int16 => t is TypeCode.Int32 or TypeCode.Int64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal,
                TypeCode.UInt16 => t is TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal,
                TypeCode.Char => t is TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal,
                TypeCode.Int32 => t is TypeCode.Int64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal,
                TypeCode.UInt32 => t is TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal,
                TypeCode.Int64 => t is TypeCode.Single or TypeCode.Double or TypeCode.Decimal,
                TypeCode.UInt64 => t is TypeCode.Single or TypeCode.Double or TypeCode.Decimal,
                TypeCode.Single => t is TypeCode.Double,
                _ => false,
            };
        }

        // Fills `inferred` with the generic arguments that make `parameter` take `argument`.
        static bool Unify(Type parameter, Type argument, Type?[] inferred)
        {
            if (parameter.IsGenericParameter)
            {
                if (parameter.DeclaringMethod is null)
                {
                    return false;
                }

                int position = parameter.GenericParameterPosition;
                if (position < inferred.Length)
                {
                    inferred[position] ??= argument;
                }
                return true;
            }

            if (!parameter.ContainsGenericParameters)
            {
                return parameter.IsAssignableFrom(argument);
            }

            if (parameter.IsArray)
            {
                return argument.IsArray
                    && argument.GetArrayRank() == parameter.GetArrayRank()
                    && Unify(parameter.GetElementType(), argument.GetElementType(), inferred);
            }

            if (parameter.IsGenericType)
            {
                Type definition = parameter.GetGenericTypeDefinition();
                foreach (Type candidate in SelfBasesAndInterfaces(argument))
                {
                    if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == definition)
                    {
                        Type[] want = parameter.GetGenericArguments();
                        Type[] have = candidate.GetGenericArguments();
                        for (int i = 0; i < want.Length; i++)
                        {
                            if (!Unify(want[i], have[i], inferred))
                            {
                                return false;
                            }
                        }
                        return true;
                    }
                }
            }

            return false;
        }

        static IEnumerable<Type> SelfBasesAndInterfaces(Type type)
        {
            for (Type? t = type; t is not null; t = t.BaseType)
            {
                yield return t;
            }

            foreach (Type i in type.GetInterfaces())
            {
                yield return i;
            }
        }

        // Number of type parameters on the generic method that declares `type`'s parameters.
        static int GenericArity(Type type)
        {
            if (type.IsGenericParameter)
            {
                return type.DeclaringMethod?.GetGenericArguments().Length ?? 0;
            }

            if (type.HasElementType)
            {
                return GenericArity(type.GetElementType());
            }

            int arity = 0;
            if (type.IsGenericType)
            {
                foreach (Type arg in type.GetGenericArguments())
                {
                    arity = Math.Max(arity, GenericArity(arg));
                }
            }
            return arity;
        }

        #endregion

        #region Select (step 3)

        // Outcome of comparing two candidates, as IronPython's Candidate enum.
        private enum Preference
        {
            Equivalent,
            First,
            Second,
            Ambiguous,
        }

        // Keeps the candidates at the strictest level, then picks the one that
        // beats every other; otherwise reports them as ambiguous.
        static bool TrySelect(List<Candidate> candidates, out BindSpec? spec, out BindError? error)
        {
            spec = null;
            error = null;

            if (candidates.Count == 0)
            {
                error = BindError.NoMatchBindError;
                return false;
            }

            if (candidates.Count == 1)
            {
                spec = candidates[0].Spec;
                return true;
            }

            Narrowing strictest = Narrowing.Never;
            foreach (Candidate c in candidates)
            {
                if (c.Level < strictest)
                {
                    strictest = c.Level;
                }
            }

            var applicable = new List<Candidate>(candidates.Count);
            foreach (Candidate c in candidates)
            {
                if (c.Level == strictest)
                {
                    applicable.Add(c);
                }
            }

            foreach (Candidate c in applicable)
            {
                if (IsBest(c, applicable))
                {
                    spec = c.Spec;
                    return true;
                }
            }

            var tied = new MethodBase[applicable.Count];
            for (int i = 0; i < tied.Length; i++)
            {
                tied[i] = applicable[i].Spec.Method;
            }
            error = new AmbiguousBindError(tied);
            return false;
        }

        static bool IsBest(Candidate candidate, List<Candidate> others)
        {
            foreach (Candidate other in others)
            {
                if (other != candidate && Compare(candidate, other) != Preference.First)
                {
                    return false;
                }
            }
            return true;
        }

        // As IronPython's GetPreferredCandidate: argument by argument first,
        // then whole-method tie-breaks when that settles nothing.
        static Preference Compare(Candidate a, Candidate b)
        {
            Preference result = Preference.Equivalent;
            for (int i = 0; i < a.Levels.Length; i++)
            {
                Preference p = CompareArgument(a.Levels[i], a.Targets[i], b.Levels[i], b.Targets[i]);
                switch (result)
                {
                    case Preference.Equivalent:
                        result = p;
                        break;

                    case Preference.First when p == Preference.Second:
                    case Preference.Second when p == Preference.First:
                        result = Preference.Ambiguous;
                        i = a.Levels.Length;
                        break;

                    case Preference.Ambiguous when p != Preference.Equivalent:
                        result = p;
                        break;
                }
            }

            return result is Preference.First or Preference.Second ? result : TieBreak(a, b);
        }

        // Where one argument went in two candidates, as IronPython's GetPreferredParameter.
        static Preference CompareArgument(Narrowing la, Type? ta, Narrowing lb, Type? tb)
        {
            if (ta is null || tb is null)
            {
                return Preference.Equivalent;
            }

            if (ta.IsByRef)
            {
                ta = ta.GetElementType();
            }
            if (tb.IsByRef)
            {
                tb = tb.GetElementType();
            }

            if (ta == tb)
            {
                return Preference.Equivalent;
            }

            Preference byType = CompareTypes(ta, tb);
            if (byType is Preference.First or Preference.Second)
            {
                return byType;
            }

            // the argument itself needs less narrowing for one of them
            if (la != lb)
            {
                return la < lb ? Preference.First : Preference.Second;
            }

            return Preference.Ambiguous;
        }

        // The more specific of two parameter types: the one that converts to the
        // other but not back; else IronPython's PreferConvert order.
        static Preference CompareTypes(Type ta, Type tb)
        {
            bool bToA = IsImplicitType(tb, ta);
            bool aToB = IsImplicitType(ta, tb);
            if (aToB && bToA)
            {
                return Preference.Ambiguous;
            }
            if (bToA)
            {
                return Preference.Second;
            }
            if (aToB)
            {
                return Preference.First;
            }

            // pythonnet's own: collections compare by their items, so [1] picks int[] over double[]
            if (TryGetElementType(ta, out Type? ea) && TryGetElementType(tb, out Type? eb) && ea != eb)
            {
                Preference byItems = CompareTypes(ea!, eb!);
                if (byItems is Preference.First or Preference.Second)
                {
                    return byItems;
                }
            }

            Preference preferred = PreferConvert(ta, tb);
            if (preferred != Preference.Equivalent)
            {
                return preferred;
            }

            return PreferConvert(tb, ta) switch
            {
                Preference.First => Preference.Second,
                Preference.Second => Preference.First,
                _ => Preference.Equivalent,
            };
        }

        // Whether `from` goes where `to` is expected with no narrowing.
        static bool IsImplicitType(Type from, Type to)
            => from == to || to.IsAssignableFrom(from) || IsImplicitNumeric(from, to);

        // IronPython's Converter.PreferConvert: int over bool, BigInteger over
        // decimal, signed over unsigned.
        static Preference PreferConvert(Type t1, Type t2)
        {
            if (t1 == typeof(bool) && t2 == typeof(int))
            {
                return Preference.Second;
            }
            if (t1 == typeof(decimal) && t2 == typeof(System.Numerics.BigInteger))
            {
                return Preference.Second;
            }

            TypeCode c2 = Type.GetTypeCode(t2);
            bool unsignedSecond = Type.GetTypeCode(t1) switch
            {
                TypeCode.SByte => c2 is TypeCode.Byte or TypeCode.UInt16 or TypeCode.UInt32 or TypeCode.UInt64,
                TypeCode.Int16 => c2 is TypeCode.UInt16 or TypeCode.UInt32 or TypeCode.UInt64,
                TypeCode.Int32 => c2 is TypeCode.UInt32 or TypeCode.UInt64,
                TypeCode.Int64 => c2 is TypeCode.UInt64,
                _ => false,
            };
            return unsignedSecond && !t1.IsEnum && !t2.IsEnum ? Preference.First : Preference.Equivalent;
        }

        // Whole-method preferences, in IronPython's order (CompareEquivalentParameters),
        // then pythonnet's own.
        static Preference TieBreak(Candidate a, Candidate b)
        {
            MethodBase ma = a.Spec.Method;
            MethodBase mb = b.Spec.Method;

            // a non-generic method beats a generic one
            if (ma.IsGenericMethod != mb.IsGenericMethod)
            {
                return ma.IsGenericMethod ? Preference.Second : Preference.First;
            }
            if (ma.IsGenericMethod)
            {
                return Preference.Equivalent;
            }

            // fewer out values returned
            int outA = a.Spec.CountReturnedOuts();
            int outB = b.Spec.CountReturnedOuts();
            if (outA != outB)
            {
                return outA < outB ? Preference.First : Preference.Second;
            }

            // arguments taken plainly beat defaults, which beat params[] expansion
            Preference byPriority = ComparePriorities(a.Spec, b.Spec);
            if (byPriority != Preference.Equivalent)
            {
                return byPriority;
            }

            // pythonnet's own: fewer parameters left to their defaults
            int defaultsA = a.Spec.CountDefaultsUsed();
            int defaultsB = b.Spec.CountDefaultsUsed();
            if (defaultsA != defaultsB)
            {
                return defaultsA < defaultsB ? Preference.First : Preference.Second;
            }

            // a regular method beats an extension method
            bool extA = MethodShape.Of(ma).IsExtension;
            bool extB = MethodShape.Of(mb).IsExtension;
            if (extA != extB)
            {
                return extA ? Preference.Second : Preference.First;
            }

            // pythonnet's own: a Python override beats the .NET method it redirects
            bool redirA = MethodShape.Of(ma).IsRedirected;
            bool redirB = MethodShape.Of(mb).IsRedirected;
            if (redirA != redirB)
            {
                return redirA ? Preference.First : Preference.Second;
            }

            // pythonnet's own: a method hidden with `new` loses to the one hiding it
            Type? da = ma.DeclaringType;
            Type? db = mb.DeclaringType;
            if (da is not null && db is not null && da != db)
            {
                if (da.IsSubclassOf(db))
                {
                    return Preference.First;
                }
                if (db.IsSubclassOf(da))
                {
                    return Preference.Second;
                }
            }

            return Preference.Equivalent;
        }

        // IronPython's "earlier conversion rules" check: compare the highest
        // slot priority each method needs, then the next highest, and so on.
        static Preference ComparePriorities(BindSpec a, BindSpec b)
        {
            for (int ceiling = int.MaxValue; ceiling >= 0;)
            {
                int pa = a.MaxPriority(ceiling);
                int pb = b.MaxPriority(ceiling);
                if (pa != pb)
                {
                    return pa < pb ? Preference.First : Preference.Second;
                }
                ceiling = pa - 1;
            }
            return Preference.Equivalent;
        }

        #endregion

        #region Bind (step 4)

        // One parameter slot of a method, and the Python argument assigned to it.
        // Holds borrowed pointers into the call's args and kwargs, which outlive the call.
        private sealed class BindParam
        {
            static readonly Type s_boxType = typeof(System.Runtime.CompilerServices.StrongBox<>);

            readonly ParameterInfo _param;

            public readonly BindParamKind Kind;
            public readonly string Key;
            public readonly Type ParameterType;

            // element type for params[], otherwise the parameter type
            public readonly Type Type;

            IntPtr _arg;
            IntPtr[]? _items;

            // number of the argument in this slot; the first one for params[]
            public int ArgIndex { get; private set; } = -1;

            // params[]: the single argument is the whole array, not one item
            public bool AsArray { get; set; }

            public bool HasBox { get; private set; }

            // params[] given by keyword
            public bool ByKeyword { get; private set; }

            public BindParam(ParameterInfo paramInfo, BindParamKind kind)
            {
                _param = paramInfo;
                Kind = kind;
                Key = paramInfo.Name;
                ParameterType = paramInfo.ParameterType;
                Type = kind == BindParamKind.Params ? ParameterType.GetElementType() : ParameterType;
            }

            public bool IsAssigned => ArgIndex >= 0;

            public int Priority => Kind switch
            {
                BindParamKind.Return or BindParamKind.BoxedReturn => 5,
                BindParamKind.Params => AsArray ? 0 : 4,
                BindParamKind.Option => IsAssigned ? 0 : 2,
                _ => 0,
            };

            public int ItemCount => _items?.Length ?? 0;

            // the StrongBox given for an `out` slot
            public PyObject? Value => HasBox ? new PyObject(new BorrowedReference(_arg)) : null;

            // false when an `out` slot is given something other than a fitting StrongBox
            public bool TryAssign(BorrowedReference arg, int index)
            {
                if (Kind == BindParamKind.BoxedReturn)
                {
                    if (!IsFittingBox(arg))
                    {
                        return false;
                    }
                    HasBox = true;
                }
                else if (Kind == BindParamKind.Return && IsFittingBox(arg))
                {
                    HasBox = true;
                }

                _arg = arg.DangerousGetAddress();
                ArgIndex = index;
                return true;
            }

            public void AssignItems(ref CallArgs call, int first, int count)
            {
                _items = new IntPtr[count];
                for (int i = 0; i < count; i++)
                {
                    _items[i] = call[first + i].DangerousGetAddress();
                }
                ArgIndex = first;

                // without grading, one sequence or fitting .NET array is read as the whole array
                if (count == 1)
                {
                    ArgInfo info = call.Info(first);
                    AsArray = info.Kind == ArgKind.Sequence
                           || (info.Kind == ArgKind.Clr && ParameterType.IsAssignableFrom(info.From));
                }
            }

            // params[] given by keyword: always the whole array
            public void AssignArray(BorrowedReference arg, int index)
            {
                _items = new[] { arg.DangerousGetAddress() };
                ArgIndex = index;
                AsArray = true;
                ByKeyword = true;
            }

            // Python argument from which a generic type argument can be read
            public bool TryGetHint(out BorrowedReference arg, out Type type)
            {
                type = AsArray || Kind != BindParamKind.Params ? ParameterType : Type;
                if (type.IsByRef)
                {
                    type = type.GetElementType();
                }

                IntPtr ptr = _items is { Length: > 0 } ? _items[0] : _arg;
                arg = new BorrowedReference(ptr);
                return ptr != IntPtr.Zero && Kind != BindParamKind.BoxedReturn;
            }

            // Converts the assigned Python value to `parameterType`, which differs
            // from ParameterType only once a generic method has been closed.
            public bool TryConvert(Type parameterType, out object? value)
            {
                Type type = parameterType.IsByRef ? parameterType.GetElementType() : parameterType;

                if (Kind == BindParamKind.Params)
                {
                    Type element = type.GetElementType();
                    if (_items is null)
                    {
                        value = Array.CreateInstance(element, 0);
                        return true;
                    }

                    if (AsArray)
                    {
                        return TryConvertArg(new BorrowedReference(_items[0]), type, out value);
                    }

                    var array = Array.CreateInstance(element, _items.Length);
                    for (int i = 0; i < _items.Length; i++)
                    {
                        if (!TryConvertArg(new BorrowedReference(_items[i]), element, out object? item))
                        {
                            value = null;
                            return false;
                        }
                        array.SetValue(item, i);
                    }

                    value = array;
                    return true;
                }

                if (_arg == IntPtr.Zero)
                {
                    value = Kind == BindParamKind.Option ? GetDefaultValue(_param) : null;
                    return true;
                }

                if (HasBox)
                {
                    using var box = new PyObject(new BorrowedReference(_arg));
                    using PyObject boxed = box.GetAttr("Value");
                    return TryConvertArg(boxed.Reference, type, out value);
                }

                return TryConvertArg(new BorrowedReference(_arg), type, out value);
            }

            static bool TryConvertArg(BorrowedReference arg, Type type, out object? value)
            {
                // None for a delegate is no delegate, not a callable to wrap
                if (arg == Runtime.PyNone && typeof(Delegate).IsAssignableFrom(type))
                {
                    value = null;
                    return true;
                }

                // the converter cannot change one .NET number or char into another
                if (ManagedType.GetManagedObject(arg) is CLRObject { inst: IConvertible clr }
                        && !type.IsInstanceOfType(clr))
                {
                    Type target = Nullable.GetUnderlyingType(type) ?? type;
                    Type source = clr.GetType();
                    bool numbers = (IsNumeric(source) || source == typeof(bool)) && IsNumeric(target) && !target.IsEnum && !source.IsEnum;
                    bool chars = (source == typeof(char) && target == typeof(string))
                              || (source == typeof(string) && target == typeof(char) && ((string)clr).Length == 1);
                    if (numbers || chars)
                    {
                        try
                        {
                            // whole numbers drop the fraction, as Python's int() does
                            object number = IsFloatingPoint(source) && !IsFloatingPoint(target)
                                ? Math.Truncate(Convert.ToDecimal(clr, CultureInfo.InvariantCulture))
                                : clr;

                            value = target == typeof(System.Numerics.BigInteger)
                                ? ToBigInteger(number)
                                : Convert.ChangeType(number, target, CultureInfo.InvariantCulture);
                            return true;
                        }
                        catch (OverflowException ex)
                        {
                            Exceptions.SetError(Exceptions.OverflowError, ex.Message);
                            value = null;
                            return false;
                        }
                    }
                }

                return Converter.ToManaged(arg, type, out value, setError: true);
            }

            static System.Numerics.BigInteger ToBigInteger(object number) => number switch
            {
                decimal d => new System.Numerics.BigInteger(d),
                bool b => b ? System.Numerics.BigInteger.One : System.Numerics.BigInteger.Zero,
                ulong u => new System.Numerics.BigInteger(u),
                _ => new System.Numerics.BigInteger(Convert.ToInt64(number, CultureInfo.InvariantCulture)),
            };

            bool IsFittingBox(BorrowedReference arg)
            {
                if (ManagedType.GetManagedObject(arg) is CLRObject { inst: object box })
                {
                    Type boxType = box.GetType();
                    return boxType.IsGenericType
                        && boxType.GetGenericTypeDefinition() == s_boxType
                        && boxType.GetGenericArguments()[0].MakeByRefType().IsAssignableFrom(ParameterType);
                }
                return false;
            }

            // [Optional] without a default: https://stackoverflow.com/questions/3416216
            static object? GetDefaultValue(ParameterInfo paramInfo)
            {
                if (paramInfo.HasDefaultValue)
                {
                    return paramInfo.DefaultValue;
                }

                Type type = paramInfo.ParameterType;
                if (type == typeof(object))
                {
                    return Type.Missing;
                }

                return type.IsValueType ? Activator.CreateInstance(type) : null;
            }
        }

        // The chosen method, with its parameter slots filled.
        private sealed class BindSpec
        {
            public readonly MethodBase Method;
            public readonly BindParam[] Parameters;
            public readonly uint Required;
            public readonly uint Optional;
            public readonly uint Returns;
            public readonly bool Expands;

            public BindSpec(MethodBase method, BindParam[] parameters,
                            uint required, uint optional, uint returns, bool expands)
            {
                Method = method;
                Parameters = parameters;
                Required = required;
                Optional = optional;
                Returns = returns;
                Expands = expands;
            }

            // an operator method, with the instance as one operand
            public bool IsOperator => Array.Exists(Parameters, p => p.Kind == BindParamKind.Self);

            // `out` and `ref` values handed back to Python
            public int CountReturnedOuts()
            {
                int count = 0;
                foreach (BindParam p in Parameters)
                {
                    if (p.Kind == BindParamKind.Return || (p.Kind == BindParamKind.BoxedReturn && !p.HasBox))
                    {
                        count++;
                    }
                }
                return count;
            }

            // optional parameters left to their default value
            public int CountDefaultsUsed()
            {
                int count = 0;
                foreach (BindParam p in Parameters)
                {
                    if (p.Kind == BindParamKind.Option && !p.IsAssigned)
                    {
                        count++;
                    }
                }
                return count;
            }

            // Highest slot priority at or below `ceiling`, as IronPython's ArgBuilder
            // priorities: plain argument 0, default value 2, params[] items 4, out/ref 5.
            public int MaxPriority(int ceiling)
            {
                int max = 0;
                foreach (BindParam p in Parameters)
                {
                    int priority = p.Priority;
                    if (priority <= ceiling && priority > max)
                    {
                        max = priority;
                    }
                }
                return max;
            }

            // Converts every slot to its .NET value. Closes a generic method over
            // the types of the arguments given for its type parameters.
            public bool TryGetArguments(object? instance, out MethodBase method, out object?[] arguments)
            {
                method = Method;
                arguments = new object?[Parameters.Length];
                bool[]? converted = null;

                if (Method.IsGenericMethod && Method.ContainsGenericParameters)
                {
                    converted = new bool[Parameters.Length];
                    if (!TryCloseGeneric(arguments, converted, out method))
                    {
                        return false;
                    }
                }

                ParameterInfo[]? closed = method == Method ? null : method.GetParameters();

                for (int i = 0; i < Parameters.Length; i++)
                {
                    BindParam param = Parameters[i];

                    if (param.Kind == BindParamKind.Self)
                    {
                        arguments[i] = instance;
                    }
                    else if (converted is null || !converted[i])
                    {
                        Type type = closed?[i].ParameterType ?? param.ParameterType;
                        if (!param.TryConvert(type, out arguments[i]))
                        {
                            return false;
                        }
                    }
                }

                return true;
            }

            // Infers each type parameter from the argument given for it.
            // A Python value given for a bare T is converted to its own .NET type,
            // which then is T; `converted` marks those so they are not converted twice.
            bool TryCloseGeneric(object?[] arguments, bool[] converted, out MethodBase method)
            {
                method = Method;
                Type[] typeParams = Method.GetGenericArguments();
                var inferred = new Type?[typeParams.Length];
                var nullOnly = new bool[typeParams.Length];

                for (int i = 0; i < Parameters.Length; i++)
                {
                    BindParam param = Parameters[i];
                    if (param.Kind == BindParamKind.Self
                            || !param.TryGetHint(out BorrowedReference arg, out Type type)
                            || !type.ContainsGenericParameters)
                    {
                        continue;
                    }

                    switch (ManagedType.GetManagedObject(arg))
                    {
                        case CLRObject co:
                            Unify(type, co.inst?.GetType() ?? typeof(object), inferred);
                            continue;

                        case ClassBase cb when cb.type.Valid:
                            Unify(type, cb.type.Value.GetType(), inferred);
                            continue;
                    }

                    if (!type.IsGenericParameter || type.DeclaringMethod is null)
                    {
                        continue;
                    }

                    if (arg == Runtime.PyNone)
                    {
                        nullOnly[type.GenericParameterPosition] = true;
                        continue;
                    }

                    if (param.Kind == BindParamKind.Params)
                    {
                        if (!TryGetManagedValue(arg, out object? item))
                        {
                            return false;
                        }
                        Unify(type, item?.GetType() ?? typeof(object), inferred);
                        continue;
                    }

                    if (!TryGetNaturalValue(arg, out object? value))
                    {
                        return false;
                    }
                    arguments[i] = value;
                    converted[i] = true;
                    Unify(type, value?.GetType() ?? typeof(object), inferred);
                }

                for (int t = 0; t < inferred.Length; t++)
                {
                    // None was the only hint: T is object
                    if (inferred[t] is null && nullOnly[t])
                    {
                        inferred[t] = typeof(object);
                    }

                    if (inferred[t] is null)
                    {
                        Exceptions.SetError(Exceptions.TypeError,
                            "can not infer generic type arguments; provide them explicitly");
                        return false;
                    }
                }

                try
                {
                    method = ((MethodInfo)Method).MakeGenericMethod(inferred!);
                    return true;
                }
                // T does not satisfy the method's constraints
                catch (ArgumentException ex)
                {
                    Exceptions.SetError(Exceptions.TypeError, ex.Message);
                    return false;
                }
            }

            // A Python value as its own .NET type; anything without one stays a PyObject.
            static bool TryGetNaturalValue(BorrowedReference arg, out object? value)
            {
                // an int's own type depends on its size
                if (Runtime.PyInt_Check(arg) && !Runtime.PyBool_Check(arg))
                {
                    return Converter.ToManaged(arg, typeof(object), out value, setError: true);
                }

                if (GetCLRType(arg) is null)
                {
                    Exceptions.Clear();
                    value = new PyObject(arg);
                    return true;
                }
                return TryGetManagedValue(arg, out value);
            }
        }

        private abstract class BindError : Exception
        {
            public static NoMatchBindError NoMatchBindError { get; } = new();
        }

        private sealed class NoMatchBindError : BindError { }

        private sealed class AmbiguousBindError : BindError
        {
            public MethodBase[] Methods { get; }

            public AmbiguousBindError(MethodBase[] methods)
            {
                Methods = methods;
            }
        }

        #endregion
    }
}
