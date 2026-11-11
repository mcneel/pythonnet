using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
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
    // Caching plan (not implemented; keep each step a pure function of its key):
    //   - SHAPE depends only on the MethodBase: cache the parameter kinds per method.
    //   - MATCH depends only on (argument CLR type, parameter type) when the
    //     argument has a CLR type: cache Match per type pair.
    //   - SELECT depends on (method group, argument kinds and types): cache the
    //     winner per method group, keyed by that tuple.
    partial class MethodBinder
    {
        #region Entry

        static bool TryBind(MethodBase[] methods,
                            BorrowedReference args,
                            BorrowedReference kwargs,
                            bool allowRedirected,
                            out BindSpec? spec,
                            out BindError? error)
        {
            spec = null;
            error = null;

            var call = new CallArgs(args, kwargs);

            var shaped = new List<BindSpec>(methods.Length);
            foreach (MethodBase method in methods)
            {
                if (!allowRedirected && BindSpec.IsRedirected(method))
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
            bool grade = shaped.Count > 1;

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

        #region Call arguments

        // Python args and kwargs of one call. Lives for that call only.
        // Arguments are numbered positional first, then keywords in dict order.
        private ref struct CallArgs
        {
            readonly BorrowedReference _args;
            readonly BorrowedReference _kwargs;
            readonly ArgInfo?[] _infos;

            public readonly int Count;
            public readonly string[] KwNames;

            public CallArgs(BorrowedReference args, BorrowedReference kwargs)
            {
                _args = args;
                _kwargs = kwargs;
                Count = args == null ? 0 : Math.Max(0, (int)Runtime.PyTuple_Size(args));
                KwNames = kwargs == null ? Array.Empty<string>() : GetKeywordNames(kwargs);
                _infos = new ArgInfo?[Count + KwNames.Length];
            }

            public readonly int Total => Count + KwNames.Length;

            public readonly BorrowedReference this[int index]
                => index < Count
                    ? Runtime.PyTuple_GetItem(_args, index)
                    : Runtime.PyDict_GetItemString(_kwargs, KwNames[index - Count]);

            // what kind of value an argument is; worked out once per call
            public readonly ArgInfo Info(int index)
                => _infos[index] ??= ArgInfo.Of(this[index]);

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
        private sealed class ArgInfo
        {
            public ArgKind Kind;
            public Type? ClrType;       // for Clr
            public int Length;          // for Str and Bytes
            public long Signed;         // for Int, when it fits a long
            public ulong Unsigned;      // for Int, when it only fits a ulong
            public IntRange Range;      // for Int

            public static ArgInfo Of(BorrowedReference arg)
            {
                var info = new ArgInfo { Kind = ArgKind.Other };

                if (arg == null || arg == Runtime.PyNone)
                {
                    info.Kind = ArgKind.None;
                    return info;
                }

                switch (ManagedType.GetManagedObject(arg))
                {
                    case CLRObject co:
                        info.Kind = ArgKind.Clr;
                        info.ClrType = co.inst?.GetType() ?? typeof(object);
                        return info;

                    case ClassBase cb when cb.type.Valid:
                        info.Kind = ArgKind.Clr;
                        info.ClrType = cb.type.Value.GetType();
                        return info;
                }

                // bool before int: a Python bool is also an int
                if (Runtime.PyBool_Check(arg))
                {
                    info.Kind = ArgKind.Bool;
                }
                else if (Runtime.PyInt_Check(arg))
                {
                    info.Kind = ArgKind.Int;
                    ReadInt(arg, info);
                }
                else if (Runtime.PyFloat_Check(arg))
                {
                    info.Kind = ArgKind.Float;
                }
                else if (Runtime.PyString_Check(arg))
                {
                    info.Kind = ArgKind.Str;
                    info.Length = (int)Runtime.PyUnicode_GetLength(arg);
                }
                else if (Runtime.PyObject_TypeCheck(arg, Runtime.PyBytesType))
                {
                    info.Kind = ArgKind.Bytes;
                    info.Length = (int)Runtime.PyBytes_Size(arg);
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
                if (Runtime.PyLong_AsLongLong(arg) is long signed)
                {
                    info.Signed = signed;
                    info.Range = IntRange.Signed;
                    return;
                }
                Exceptions.Clear();

                if (Runtime.PyLong_AsUnsignedLongLong(arg) is ulong unsigned)
                {
                    info.Unsigned = unsigned;
                    info.Range = IntRange.Unsigned;
                    return;
                }
                Exceptions.Clear();

                info.Range = IntRange.Big;
            }

            // whether a Python int fits an integer type
            public bool IntFits(TypeCode code)
            {
                if (Range == IntRange.Big)
                {
                    return false;
                }

                if (Range == IntRange.Unsigned)
                {
                    return code == TypeCode.UInt64;
                }

                long v = Signed;
                return code switch
                {
                    TypeCode.SByte => v >= sbyte.MinValue && v <= sbyte.MaxValue,
                    TypeCode.Byte => v >= byte.MinValue && v <= byte.MaxValue,
                    TypeCode.Int16 => v >= short.MinValue && v <= short.MaxValue,
                    TypeCode.UInt16 => v >= ushort.MinValue && v <= ushort.MaxValue,
                    TypeCode.Char => v >= char.MinValue && v <= char.MaxValue,
                    TypeCode.Int32 => v >= int.MinValue && v <= int.MaxValue,
                    TypeCode.UInt32 => v >= uint.MinValue && v <= uint.MaxValue,
                    TypeCode.Int64 => true,
                    TypeCode.UInt64 => v >= 0,
                    _ => false,
                };
            }
        }

        private enum IntRange : byte
        {
            Signed,     // fits a long
            Unsigned,   // fits only a ulong
            Big,        // fits neither
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

            ParameterInfo[] mparams = method.GetParameters();

            BindParam[] argSpecs;
            uint required = 0;
            uint optional = 0;
            uint returns = 0;
            bool expands = false;

            if (OperatorMethod.IsOperatorMethod(method))
            {
                bool isReverse = OperatorMethod.IsReverse(method);
                if (isReverse && OperatorMethod.IsComparisonOp(method))
                {
                    return false;
                }

                if (mparams.Length == 2)
                {
                    required = 1;
                    argSpecs = isReverse
                        ? new[] { new BindParam(mparams[0], BindParamKind.Default), new BindParam(mparams[1], BindParamKind.Self) }
                        : new[] { new BindParam(mparams[0], BindParamKind.Self), new BindParam(mparams[1], BindParamKind.Default) };
                }
                else
                {
                    // operators always have at least one operand
                    argSpecs = new[] { new BindParam(mparams[0], BindParamKind.Self) };
                }

                if (call.Total != required)
                {
                    return false;
                }

                spec = new BindSpec(method, argSpecs, required, optional, returns, expands);
                return true;
            }

            int kwLeft = call.KwNames.Length;
            argSpecs = new BindParam[mparams.Length];

            if (mparams.Length > 0)
            {
                int last = mparams.Length - 1;
                expands = Attribute.IsDefined(mparams[last], typeof(ParamArrayAttribute));

                for (int i = 0; i < mparams.Length; i++)
                {
                    ParameterInfo param = mparams[i];

                    if (kwLeft > 0 && call.KwIndex(param.Name) >= 0)
                    {
                        kwLeft--;
                    }

                    // `.IsOut` is false for `ref`. [In][Out] arrays (e.g. Stream.Read) are inputs.
                    if (param.IsOut && param.GetCustomAttribute<InAttribute>() is null)
                    {
                        argSpecs[i] = new BindParam(param, BindParamKind.BoxedReturn);
                        returns++;
                    }
                    else if (param.ParameterType.IsByRef)
                    {
                        argSpecs[i] = new BindParam(param, param.IsOut ? BindParamKind.BoxedReturn : BindParamKind.Return);
                        returns++;
                        required++;
                    }
                    else if (param.IsOptional)
                    {
                        argSpecs[i] = new BindParam(param, BindParamKind.Option);
                        optional++;
                    }
                    else if (expands && i == last)
                    {
                        argSpecs[i] = new BindParam(param, BindParamKind.Params);
                    }
                    else
                    {
                        argSpecs[i] = new BindParam(param, BindParamKind.Default);
                        required++;
                    }
                }

                // a keyword that names no parameter of this method
                if (kwLeft > 0)
                {
                    return false;
                }
            }
            else if (kwLeft > 0)
            {
                return false;
            }

            uint given = (uint)call.Total;
            bool fits = given == required
                     || (required < given && (given <= required + optional + returns || expands));
            if (!fits)
            {
                return false;
            }

            spec = new BindSpec(method, argSpecs, required, optional, returns, expands);
            return true;
        }

        #endregion

        #region Match (step 2)

        // How well one argument fits one parameter. Lower is stricter.
        private enum MatchLevel : byte
        {
            Exact,      // same .NET type
            Widening,   // no data loss: subclass to base, Int16 to int, T to T?, None to a class
            Python,     // Python value to a .NET type that holds it: int to Int32, str to String
            Converted,  // needs a converter: float to int, callable to delegate, list to array
            None,       // cannot fit
        }

        // Ranks order matches within one level. Lower is closer.
        const ushort RankNatural = 0;       // a Python value's own .NET type
        const ushort RankNumeric = 1;       // another number type that holds the value
        const ushort RankBoolNumber = 2;    // a Python bool used as a number
        const ushort RankBoxed = 50;        // a Python value boxed as `object`
        const ushort RankIterator = 1500;   // a one-shot iterator; its items are not checked
        const ushort RankConverter = 2000;  // only the converter (or a codec) knows how
        const ushort RankPyObject = 2500;   // passed through as a PyObject
        const ushort RankObject = 3000;     // a Python object wrapped as `object`

        // Items of a Python sequence checked when grading it against a collection.
        // A later item that does not fit is reported when the argument is converted.
        const int SequenceSample = 16;

        // Grade of one argument, plus a rank within that grade.
        private readonly struct Match
        {
            public readonly MatchLevel Level;
            public readonly ushort Rank;

            public Match(MatchLevel level, ushort rank = 0)
            {
                Level = level;
                Rank = rank;
            }

            public static readonly Match None = new(MatchLevel.None);
            public static readonly Match Exact = new(MatchLevel.Exact);

            public bool IsWorseThan(Match other)
                => Level != other.Level ? Level > other.Level : Rank > other.Rank;
        }

        // A method that passed SHAPE, with its arguments assigned and graded.
        private sealed class Candidate
        {
            public readonly BindSpec Spec;
            public readonly Match[] Matches;    // per argument number
            public readonly Type[] Targets;     // parameter type each argument went to
            public MatchLevel Level;            // worst of Matches
            public bool Expanded;               // params[] taken as separate arguments
            public int Unfilled;                // optional and out slots left to defaults

            public Candidate(BindSpec spec, int argCount)
            {
                Spec = spec;
                Matches = new Match[argCount];
                Targets = new Type[argCount];
            }

            public bool IsGenericDefinition
                => Spec.Method.IsGenericMethod && Spec.Method.ContainsGenericParameters;

            public void Grade(int argIndex, Match match, Type target)
            {
                Matches[argIndex] = match;
                Targets[argIndex] = target;
                if (match.Level > Level)
                {
                    Level = match.Level;
                }
            }
        }

        // Assigns args and kwargs to the method's slots, then grades them when
        // `grade` is set. Returns false when the arguments cannot be assigned
        // or one of them cannot fit.
        static bool TryMatch(BindSpec spec, ref CallArgs call, bool grade, out Candidate? candidate)
        {
            candidate = null;
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

            var result = new Candidate(spec, call.Total);
            foreach (BindParam slot in slots)
            {
                switch (slot.Kind)
                {
                    case BindParamKind.Default:
                    case BindParamKind.Return:
                        if (!slot.IsAssigned)
                        {
                            return false;
                        }
                        break;

                    case BindParamKind.Option:
                    case BindParamKind.BoxedReturn:
                        if (!slot.IsAssigned)
                        {
                            result.Unfilled++;
                        }
                        break;

                    case BindParamKind.Params:
                        if (!slot.AsArray)
                        {
                            result.Expanded = true;
                        }
                        break;
                }

                if (grade && !TryGrade(slot, ref call, result))
                {
                    return false;
                }
            }

            candidate = result;
            return true;
        }

        // Grades the arguments in one slot. False when one of them cannot fit.
        static bool TryGrade(BindParam slot, ref CallArgs call, Candidate candidate)
        {
            if (!slot.IsAssigned || slot.Kind == BindParamKind.Self)
            {
                return true;
            }

            int index = slot.ArgIndex;

            if (slot.Kind == BindParamKind.BoxedReturn)
            {
                // assignment already checked that it is a fitting StrongBox
                candidate.Grade(index, Match.Exact, slot.ParameterType);
                return true;
            }

            if (slot.Kind != BindParamKind.Params)
            {
                Match match = GetMatch(call.Info(index), call[index], slot.ParameterType);
                candidate.Grade(index, match, slot.ParameterType);
                return match.Level != MatchLevel.None;
            }

            // params[] given one sequence: prefer reading it as the whole array
            if (slot.ItemCount == 1)
            {
                ArgInfo info = call.Info(index);
                if (slot.ByKeyword || info.Kind is ArgKind.Sequence or ArgKind.Clr)
                {
                    Match asArray = GetMatch(info, call[index], slot.ParameterType);
                    if (asArray.Level != MatchLevel.None || slot.ByKeyword)
                    {
                        slot.AsArray = true;
                        candidate.Expanded = false;
                        candidate.Grade(index, asArray, slot.ParameterType);
                        return asArray.Level != MatchLevel.None;
                    }
                }
            }

            slot.AsArray = false;
            candidate.Expanded = true;
            for (int i = 0; i < slot.ItemCount; i++)
            {
                Match match = GetMatch(call.Info(index + i), call[index + i], slot.Type);
                candidate.Grade(index + i, match, slot.Type);
                if (match.Level == MatchLevel.None)
                {
                    return false;
                }
            }

            return true;
        }

        // Grades one argument against one parameter type.
        static Match GetMatch(ArgInfo arg, BorrowedReference value, Type to)
        {
            if (to.IsByRef)
            {
                to = to.GetElementType();
            }

            return arg.Kind == ArgKind.Clr
                ? MatchClr(arg.ClrType!, to)
                : MatchPython(arg, value, to);
        }

        static Match MatchClr(Type from, Type to)
        {
            if (from == to)
            {
                return Match.Exact;
            }

            if (to.ContainsGenericParameters)
            {
                if (to.IsGenericParameter)
                {
                    return Match.Exact;
                }
                return Unify(to, from, new Type?[GenericArity(to)]) ? new Match(MatchLevel.Widening) : Match.None;
            }

            if (to.IsAssignableFrom(from))
            {
                return new Match(MatchLevel.Widening);
            }

            Type target = Nullable.GetUnderlyingType(to) ?? to;
            if (target == from)
            {
                return new Match(MatchLevel.Widening);
            }

            if (IsImplicitNumeric(from, target))
            {
                return new Match(MatchLevel.Widening, RankNumeric);
            }

            if (typeof(PyObject).IsAssignableFrom(to))
            {
                return new Match(MatchLevel.Converted, RankPyObject);
            }

            if (HasCastOperator(from, to))
            {
                return new Match(MatchLevel.Converted);
            }

            return Match.None;
        }

        static Match MatchPython(ArgInfo arg, BorrowedReference value, Type to)
        {
            if (arg.Kind == ArgKind.None)
            {
                bool takesNull = !to.IsValueType || Nullable.GetUnderlyingType(to) is not null || to.IsGenericParameter;
                return takesNull ? new Match(MatchLevel.Widening) : Match.None;
            }

            // T is inferred from the value's own .NET type
            if (to.IsGenericParameter)
            {
                return new Match(MatchLevel.Python);
            }

            if (to.ContainsGenericParameters)
            {
                return Match.None;
            }

            if (typeof(PyObject).IsAssignableFrom(to))
            {
                return new Match(MatchLevel.Converted, RankPyObject);
            }

            if (typeof(Delegate).IsAssignableFrom(to))
            {
                return Runtime.PyCallable_Check(value) == 1 ? new Match(MatchLevel.Converted) : Match.None;
            }

            Type target = Nullable.GetUnderlyingType(to) ?? to;

            switch (arg.Kind)
            {
                case ArgKind.Bool:
                    return MatchBool(target);

                case ArgKind.Int:
                    return MatchInt(arg, target);

                case ArgKind.Float:
                    return MatchFloat(target);

                case ArgKind.Str:
                    if (target == typeof(string))
                    {
                        return new Match(MatchLevel.Python);
                    }
                    if (target == typeof(char))
                    {
                        return arg.Length == 1 ? new Match(MatchLevel.Converted) : Match.None;
                    }
                    if (target == typeof(object))
                    {
                        return new Match(MatchLevel.Python, RankBoxed);
                    }
                    return MatchSequence(value, to);

                case ArgKind.Bytes:
                    if (arg.Length == 1 && (target == typeof(byte) || target == typeof(sbyte) || target == typeof(char)))
                    {
                        return new Match(MatchLevel.Converted);
                    }
                    break;

                case ArgKind.Iterator:
                    if (TryGetElementType(to, out _))
                    {
                        return new Match(MatchLevel.Converted, RankIterator);
                    }
                    return to == typeof(object) ? new Match(MatchLevel.Converted, RankObject) : Match.None;
            }

            if (arg.Kind is ArgKind.Bool or ArgKind.Int or ArgKind.Float)
            {
                return Match.None;
            }

            if (arg.Kind is ArgKind.Sequence or ArgKind.Bytes)
            {
                Match asCollection = MatchSequence(value, to);
                if (asCollection.Level != MatchLevel.None)
                {
                    return asCollection;
                }
            }

            if (to == typeof(object))
            {
                return new Match(MatchLevel.Converted, RankObject);
            }

            return MatchByConverter(value, to);
        }

        static Match MatchBool(Type target)
        {
            if (target == typeof(bool))
            {
                return new Match(MatchLevel.Python);
            }

            if (Converter.IsInteger(target) || Converter.IsFloatingNumber(target))
            {
                return new Match(MatchLevel.Converted, RankBoolNumber);
            }

            return target == typeof(object) ? new Match(MatchLevel.Python, RankBoxed) : Match.None;
        }

        static Match MatchInt(ArgInfo arg, Type target)
        {
            TypeCode code = Type.GetTypeCode(target);
            if (target.IsEnum)
            {
                return Match.None;
            }

            switch (code)
            {
                case TypeCode.Int32:
                    return arg.IntFits(code) ? new Match(MatchLevel.Python, RankNatural) : Match.None;

                case TypeCode.SByte:
                case TypeCode.Byte:
                case TypeCode.Int16:
                case TypeCode.UInt16:
                case TypeCode.UInt32:
                case TypeCode.Int64:
                case TypeCode.UInt64:
                    return arg.IntFits(code) ? new Match(MatchLevel.Python, RankNumeric) : Match.None;

                case TypeCode.Single:
                case TypeCode.Double:
                    return new Match(MatchLevel.Python, RankNumeric);

                case TypeCode.Char:
                    return arg.IntFits(code) ? new Match(MatchLevel.Converted) : Match.None;
            }

            if (target == typeof(System.Numerics.BigInteger))
            {
                return new Match(MatchLevel.Converted);
            }

            // boxed as Int32, so only when it fits one
            if (target == typeof(object))
            {
                return arg.IntFits(TypeCode.Int32) ? new Match(MatchLevel.Python, RankBoxed) : Match.None;
            }

            return Match.None;
        }

        static Match MatchFloat(Type target)
        {
            if (target == typeof(double))
            {
                return new Match(MatchLevel.Python, RankNatural);
            }

            if (target == typeof(float))
            {
                return new Match(MatchLevel.Python, RankNumeric);
            }

            // truncated; kept so older scripts work, but never preferred
            if (target == typeof(int))
            {
                return new Match(MatchLevel.Converted);
            }

            return target == typeof(object) ? new Match(MatchLevel.Python, RankBoxed) : Match.None;
        }

        // Grades a Python sequence against an array or collection type, by its items.
        static Match MatchSequence(BorrowedReference value, Type to)
        {
            if (!TryGetElementType(to, out Type? element))
            {
                return Match.None;
            }

            nint size = Runtime.PySequence_Size(value);
            if (size < 0)
            {
                Exceptions.Clear();
                return new Match(MatchLevel.Converted, RankIterator);
            }

            Match worst = Match.Exact;
            int sample = (int)Math.Min(size, SequenceSample);
            for (int i = 0; i < sample; i++)
            {
                using NewReference item = Runtime.PySequence_GetItem(value, i);
                if (item.IsNull())
                {
                    Exceptions.Clear();
                    return Match.None;
                }

                Match match = GetMatch(ArgInfo.Of(item.Borrow()), item.Borrow(), element!);
                if (match.Level == MatchLevel.None)
                {
                    return Match.None;
                }

                if (match.IsWorseThan(worst))
                {
                    worst = match;
                }
            }

            // closer items make a closer collection: [1] picks int[] over double[]
            int rank = 1 + (int)worst.Level * 256 + Math.Min((int)worst.Rank, 255);
            return new Match(MatchLevel.Converted, (ushort)rank);
        }

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

        // Last resort for Python objects: ask the converter, which also runs codecs.
        static Match MatchByConverter(BorrowedReference value, Type to)
        {
            bool converts = Converter.ToManaged(value, to, out _, setError: false);
            if (Exceptions.ErrorOccurred())
            {
                Exceptions.Clear();
            }
            return converts ? new Match(MatchLevel.Converted, RankConverter) : Match.None;
        }

        static bool HasCastOperator(Type from, Type to)
            => FindCastOperator(from, from, to) || FindCastOperator(to, from, to);

        static bool FindCastOperator(Type owner, Type from, Type to)
        {
            foreach (MethodInfo m in owner.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if ((m.Name == "op_Implicit" || m.Name == "op_Explicit")
                        && m.ReturnType == to
                        && m.GetParameters() is { Length: 1 } ps
                        && ps[0].ParameterType == from)
                {
                    return true;
                }
            }
            return false;
        }

        // C# implicit numeric conversions.
        static bool IsImplicitNumeric(Type from, Type to)
        {
            if (!from.IsPrimitive || !(to.IsPrimitive || to == typeof(decimal)) || from == typeof(bool) || to == typeof(bool))
            {
                return false;
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

        // Whether a value of `from` can go where `to` is expected without a cast, as in C#.
        static bool IsImplicit(Type from, Type to)
        {
            if (from == to || to.IsAssignableFrom(from))
            {
                return true;
            }

            if (Nullable.GetUnderlyingType(to) is Type underlying)
            {
                return IsImplicit(from, underlying);
            }

            return IsImplicitNumeric(from, to);
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

        // Keeps the strictest level, then picks the one candidate that beats
        // all others; otherwise reports the tied ones as ambiguous.
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

            MatchLevel strictest = MatchLevel.None;
            foreach (Candidate c in candidates)
            {
                if (c.Level < strictest)
                {
                    strictest = c.Level;
                }
            }

            Candidate? best = null;
            foreach (Candidate c in candidates)
            {
                if (c.Level == strictest && (best is null || Compare(c, best) > 0))
                {
                    best = c;
                }
            }

            List<MethodBase>? tied = null;
            foreach (Candidate c in candidates)
            {
                if (c != best && c.Level == strictest && Compare(best!, c) <= 0)
                {
                    tied ??= new List<MethodBase> { best!.Spec.Method };
                    tied.Add(c.Spec.Method);
                }
            }

            if (tied is not null)
            {
                error = new AmbiguousBindError(tied.ToArray());
                return false;
            }

            spec = best!.Spec;
            return true;
        }

        // >0 when `a` is the better call, <0 when `b` is, 0 when neither.
        static int Compare(Candidate a, Candidate b)
        {
            bool aBetter = false;
            bool bBetter = false;
            int firstDifference = 0;

            for (int i = 0; i < a.Matches.Length; i++)
            {
                int r = CompareArgument(a.Matches[i], a.Targets[i], b.Matches[i], b.Targets[i]);
                if (r == 0)
                {
                    continue;
                }

                if (firstDifference == 0)
                {
                    firstDifference = r;
                }

                if (r > 0)
                {
                    aBetter = true;
                }
                else
                {
                    bBetter = true;
                }
            }

            if (aBetter != bBetter)
            {
                return aBetter ? 1 : -1;
            }

            // each is better somewhere; C# calls this ambiguous,
            // here the first argument that differs decides
            if (aBetter)
            {
                return firstDifference;
            }

            return TieBreak(a, b);
        }

        // Compares where one argument went in two candidates.
        static int CompareArgument(Match ma, Type? ta, Match mb, Type? tb)
        {
            if (ma.Level != mb.Level)
            {
                return ma.Level < mb.Level ? 1 : -1;
            }

            if (ma.Rank != mb.Rank)
            {
                return ma.Rank < mb.Rank ? 1 : -1;
            }

            if (ta is null || tb is null)
            {
                return 0;
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
                return 0;
            }

            // the more specific type wins: one that converts to the other, not back
            bool aToB = IsImplicit(ta, tb);
            bool bToA = IsImplicit(tb, ta);
            if (aToB != bToA)
            {
                return aToB ? 1 : -1;
            }

            // signed beats unsigned, as in C#
            bool aSigned = IsSignedInteger(ta), bSigned = IsSignedInteger(tb);
            bool aUnsigned = IsUnsignedInteger(ta), bUnsigned = IsUnsignedInteger(tb);
            if (aSigned && bUnsigned)
            {
                return 1;
            }
            if (bSigned && aUnsigned)
            {
                return -1;
            }

            return 0;
        }

        static bool IsSignedInteger(Type t)
            => t == typeof(sbyte) || t == typeof(short) || t == typeof(int) || t == typeof(long);

        static bool IsUnsignedInteger(Type t)
            => t == typeof(byte) || t == typeof(ushort) || t == typeof(uint) || t == typeof(ulong);

        // Whole-method preferences, used when every argument fits equally well.
        static int TieBreak(Candidate a, Candidate b)
        {
            // params[] taken as written beats params[] expanded
            if (a.Expanded != b.Expanded)
            {
                return a.Expanded ? -1 : 1;
            }

            // a plain method beats a generic one
            bool aGeneric = a.IsGenericDefinition;
            bool bGeneric = b.IsGenericDefinition;
            if (aGeneric != bGeneric)
            {
                return aGeneric ? -1 : 1;
            }

            // every argument given explicitly beats leaning on defaults
            if (a.Unfilled != b.Unfilled)
            {
                return a.Unfilled < b.Unfilled ? 1 : -1;
            }

            // a Python override beats the .NET method it redirects
            bool aRedirected = BindSpec.IsRedirected(a.Spec.Method);
            bool bRedirected = BindSpec.IsRedirected(b.Spec.Method);
            if (aRedirected != bRedirected)
            {
                return aRedirected ? 1 : -1;
            }

            // a method hidden with `new` loses to the one hiding it
            Type? da = a.Spec.Method.DeclaringType;
            Type? db = b.Spec.Method.DeclaringType;
            if (da is not null && db is not null && da != db)
            {
                if (da.IsSubclassOf(db))
                {
                    return 1;
                }
                if (db.IsSubclassOf(da))
                {
                    return -1;
                }
            }

            return 0;
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
                           || (info.Kind == ArgKind.Clr && ParameterType.IsAssignableFrom(info.ClrType));
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
                // .NET numbers widen as in C#: an Int16 goes where an int is expected
                if (ManagedType.GetManagedObject(arg) is CLRObject { inst: IConvertible number }
                        && !type.IsInstanceOfType(number)
                        && IsImplicitNumeric(number.GetType(), Nullable.GetUnderlyingType(type) ?? type))
                {
                    value = Convert.ChangeType(number, Nullable.GetUnderlyingType(type) ?? type, CultureInfo.InvariantCulture);
                    return true;
                }

                return Converter.ToManaged(arg, type, out value, setError: true);
            }

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
            public static bool IsRedirected(MethodBase method) =>
                method.GetCustomAttribute<RedirectedMethod>() != null;

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
