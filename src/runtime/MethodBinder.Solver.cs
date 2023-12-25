using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Threading;

namespace Python.Runtime
{
    using TypeDistanceMap = Dictionary<int, uint>;

    partial class MethodBinder
    {
        static readonly Type PAAT = typeof(ParamArrayAttribute);
        static readonly Type UNINT = typeof(nuint);
        static readonly Type NINT = typeof(nint);

        static readonly TypeDistanceMap s_distMap = new();

        private sealed class BindParam
        {
            readonly ParameterInfo _param;

            public readonly uint Index;
            public readonly string Key;
            public readonly Type Type;
            public readonly bool IsOptional;
            public readonly bool IsOut;

            public PyObject? Value { get; set; } = default;

            public BindParam(uint index, ParameterInfo paramInfo,
                                bool isOptional = false, bool isOut = false)
            {
                _param = paramInfo;

                Index = index;
                Key = paramInfo.Name;
                Type = paramInfo.ParameterType;
                IsOptional = isOptional;
                IsOut = isOut;
            }

            public object? Convert()
            {
                if (Value is null)
                {
                    if (IsOptional)
                    {
                        return GetDefaultValue(_param);
                    }
                }

                else if (TryGetManagedValue(Value, Type, out object? value))
                {
                    return value;
                }

                return null;
            }

            static object? GetDefaultValue(ParameterInfo paramInfo)
            {
                if (paramInfo.HasDefaultValue)
                {
                    return paramInfo.DefaultValue;
                }

                // [OptionalAttribute] was specified for the parameter.
                // See https://stackoverflow.com/questions/3416216/optionalattribute-parameters-default-value
                // for rules on determining the value to pass to the parameter
                var type = paramInfo.ParameterType;

                if (type == typeof(object))
                {
                    return Type.Missing;
                }
                else if (type.IsValueType)
                {
                    return Activator.CreateInstance(type);
                }
                else
                {
                    return null;
                }
            }

        }

        private sealed class BindSpec
        {
            const uint SECTOR_SIZE = uint.MaxValue / 4;
            const uint STATIC_DISTANCE = SECTOR_SIZE;
            const uint PARAMS_DISTANCE = SECTOR_SIZE;
            const uint ARG_DISTANCE = 32; // reserved for TypeCode range

            public readonly MethodBase Method;
            public readonly uint Required;
            public readonly uint Optional;
            public readonly bool Expands;
            public readonly BindParam[] Parameters;

            public BindSpec(MethodBase method,
                             uint required,
                             uint optional,
                             bool expands,
                             BindParam[] argSpecs)
            {
                Method = method;
                Required = required;
                Optional = optional;
                Expands = expands;
                Parameters = argSpecs;
            }

            public object?[] GetArguments()
                => Parameters.Select(s => s.Convert()).ToArray();

            public uint GetDistance(BorrowedReference args,
                                    BorrowedReference kwargs)
            {
                uint score = 0;

                if (Method.IsStatic)
                {
                    score += STATIC_DISTANCE;
                }

                if (Expands)
                {
                    score += PARAMS_DISTANCE;
                }

                if (Method.IsGenericMethod)
                {
                    score += (ARG_DISTANCE *
                                (uint)Method.GetGenericArguments().Length);
                }

                uint argidx = 0;
                uint argsCount = (uint)Runtime.PyTuple_Size(args);
                bool checkArgs = args != null;
                bool checkKwargs = kwargs != null;

                for (uint i = 0; i < Parameters.Length; i++)
                {
                    if (Parameters[i] is BindParam slot)
                    {
                        BorrowedReference item;

                        if (checkKwargs)
                        {
                            item = Runtime.PyDict_GetItemString(kwargs, slot.Key);
                            if (item != null)
                            {
                                slot.Value =
                                    PyObject.FromNullableReference(item);

                                score += GetDistance(item, slot.Type);
                                continue;
                            }
                        }

                        if (checkArgs)
                        {
                            if (argsCount > 0
                                && argidx < argsCount)
                            {
                                item = Runtime.PyTuple_GetItem(args, (nint)argidx);
                                if (item != null)
                                {
                                    slot.Value =
                                        PyObject.FromNullableReference(item);

                                    score += GetDistance(item, slot.Type);
                                    argidx++;
                                    continue;
                                }
                            }
                        }

                        score += ARG_DISTANCE;
                    }
                }

                return score;
            }

            static uint GetDistance(BorrowedReference from, Type to)
            {
                uint distance = ARG_DISTANCE;

                if (to.IsGenericType)
                {
                    distance += 2;
                }

                if (GetCLRType(from, to) is Type argType)
                {
                    if (to == argType)
                    {
                        distance += 1;
                    }
                    else
                    {
                        distance += 3 + GetDistance(argType, to);
                    }
                }

                return distance;
            }

            static uint GetDistance(Type from, Type to)
            {
                uint distance;

                int key = from.GetHashCode() + to.GetHashCode();
                if (s_distMap.TryGetValue(key, out uint dist))
                {
                    return dist;
                }

                if (from == to)
                {
                    distance = 0;
                }
                else if (from.IsAssignableFrom(to))
                {
                    distance = 1;
                }
                else
                {
                    distance = 1 +
                        (uint)Math.Abs(
                            (int)GetPrecedence(to)
                           - (int)GetPrecedence(from)
                        );
                }

                s_distMap[key] = distance;

                return distance;
            }

            static uint GetPrecedence(Type of)
            {
                if (UNINT == of)
                {
                    return 30;
                }

                if (NINT == of)
                {
                    return 31;
                }

                return Type.GetTypeCode(of) switch
                {
                    TypeCode.Object => 1,

                    TypeCode.UInt64 => 12,
                    TypeCode.UInt32 => 13,
                    TypeCode.UInt16 => 14,
                    TypeCode.Int64 => 15,
                    TypeCode.Int32 => 16,
                    TypeCode.Int16 => 17,
                    TypeCode.Char => 18,
                    TypeCode.SByte => 19,
                    TypeCode.Byte => 20,

                    //UIntPtr|nuint => 30,
                    //IntPtr|nint => 31,

                    TypeCode.Single => 40,

                    TypeCode.Double => 42,

                    TypeCode.String => 50,

                    TypeCode.Boolean => 60,
                    _ => 2000,
                };
            }
        }

        static bool TryBind(MethodBase[] methods,
                            BorrowedReference args,
                            BorrowedReference kwargs,
                            out BindSpec? spec)
        {
            spec = default;

            int argSize = args == null ? 0 : (int)Runtime.PyTuple_Size(args);
            int kwargSize = kwargs == null ? 0 : (int)Runtime.PyDict_Size(kwargs);
            uint argsCount = (uint)(argSize == -1 ? 0 : argSize);
            uint kwargsCount = (uint)(kwargSize == -1 ? 0 : kwargSize);
            HashSet<string> kwargKeys = kwargSize > 0 ? GetKeys(kwargs) : new();

            // Find any method that could accept this many args and kwargs
            int index = 0;
            BindSpec?[] bindSpecs = new BindSpec?[methods.Count()];
            uint totalArgs = argsCount + kwargsCount;
            foreach (MethodBase mb in methods)
            {
                if (TryBindByParams(mb, totalArgs, kwargKeys, out BindSpec? bindSpec))
                {
                    bindSpecs[index] = bindSpec;
                    index++;
                }
            }

            return TryBindByDistance(bindSpecs, args, kwargs, out spec);
        }

        static bool TryBindByDistance(BindSpec?[] specs,
                                      BorrowedReference args,
                                      BorrowedReference kwargs,
                                      out BindSpec? spec)
        {
            spec = null;

            if (specs.Length == 0)
            {
                return false;
            }
            else if (specs.Length == 1)
            {
                if (specs[0] is BindSpec onlySpec)
                {
                    spec = onlySpec;
                    spec!.GetDistance(args, kwargs);
                    return true;
                }
            }
            else if (specs.Length > 1)
            {
                uint closest = uint.MaxValue;
                foreach (BindSpec? mspec in specs)
                {
                    if (mspec is null)
                    {
                        break;
                    }

                    uint distance = mspec!.GetDistance(args, kwargs);
                    if (distance < closest)
                    {
                        closest = distance;
                        spec = mspec;
                    }
                }

                return spec is not null;
            }

            return false;
        }

        static bool TryBindByParams(MethodBase m,
                                    uint givenArgs,
                                    HashSet<string> kwargs,
                                    out BindSpec? spec)
        {
            spec = default;

            uint required = 0;
            uint optional = 0;
            bool expands = false;

            BindParam[] argSpecs;
            ParameterInfo[] mparams = m.GetParameters();
            if (mparams.Length > 0)
            {
                // check if last parameter is a params array.
                // this means method can accept parameters
                // beyond what is required.
                expands =
                    Attribute.IsDefined(mparams[mparams.Length - 1], PAAT);

                uint kwargCount = (uint)kwargs.Count;
                uint length = expands ?
                    (uint)mparams.Length - 1 :
                    (uint)mparams.Length;

                argSpecs = new BindParam[length];

                for (uint i = 0; i < length; i++)
                {
                    ParameterInfo param = mparams[i];

                    string key = param.Name;

                    if (kwargs.Contains(key))
                    {
                        // if any of kwarg names is an `out` param,
                        // this method is not a match
                        if (param.IsOut)
                        {
                            goto not_matched;
                        }

                        kwargCount--;
                    }

                    // `.IsOut` is false for `ref` parameters
                    if (param.IsOut)
                    {
                        argSpecs[i] =
                            new BindParam(i, param, isOut: true);

                        continue;
                    }
                    // `.IsOptional` will be true if the parameter has
                    // a default value, or if the parameter has the `[Optional]`
                    // attribute specified.
                    else if (param.IsOptional)
                    {
                        argSpecs[i] =
                            new BindParam(i, param, isOptional: true);

                        optional++;
                    }
                    // otherwise this is a required parameter
                    else
                    {
                        argSpecs[i] =
                            new BindParam(i, param);

                        required++;
                    }
                }

                // we count back the kwargs as we see them during loop above.
                // by the end if `kwargCount > 0` it means one of the kwargs
                // is not in this method signature so we won't match.
                if (kwargCount > 0)
                {
                    goto not_matched;
                }

                // no point in processing the rest if we know this
                if (required > givenArgs)
                {
                    goto not_matched;
                }
            }
            else
            {
                argSpecs = Array.Empty<BindParam>();
            }

            // we compare required number of arguments for this
            // method with the number of total arguments provided
            // (0, 0) -> (a, b)
            // (0, 0) -> (a, b, c=0)
            if (givenArgs == required)
            {
                goto matched;
            }
            // (0, 0) -> (a, b=0, c=0)
            // (0, 0) -> (a=0, b=0)
            else if (required < givenArgs
                        && (givenArgs <= (required + optional) || expands))
            {
                goto matched;
            }
            else
            {
                goto not_matched;
            }

        not_matched:
            return false;

        matched:
            spec = new BindSpec(m, required, optional, expands, argSpecs);
            return true;
        }

        static HashSet<string> GetKeys(BorrowedReference kwargs)
        {
            var keys = new HashSet<string>();
            if (kwargs == null)
            {
                return keys;
            }

            using NewReference keyList = Runtime.PyDict_Keys(kwargs);
            using NewReference valueList = Runtime.PyDict_Values(kwargs);
            for (int i = 0; i < Runtime.PyDict_Size(kwargs); ++i)
            {
                string keyStr = Runtime.GetManagedString(
                        Runtime.PyList_GetItem(keyList.Borrow(), i)
                    )!;

                keys.Add(keyStr);
            }

            return keys;
        }
    }
}
