using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;

namespace Python.Runtime
{
    using KeywordArgs = Dictionary<string, object?>;
    using TypeDistMap = Dictionary<int, uint>;

#if UNIT_TEST
    public static class MethodInvoke
#else
    internal partial class MethodBinder
#endif
    {
        const string OP_IMPLICIT = "op_Implicit";
        const string OP_EXPLICIT = "op_Explicit";
        static readonly BindingFlags FLAGS = BindingFlags.Static
                                           | BindingFlags.Public
                                           | BindingFlags.NonPublic;

        static readonly Type PAAT = typeof(ParamArrayAttribute);
        static readonly Type UNINT = typeof(nuint);
        static readonly Type NINT = typeof(nint);

        static readonly TypeDistMap s_distMap = new();

        private sealed class MatchArgSlot
        {
            readonly ParameterInfo _paramInfo;

            bool _hasValue = false;
            object? _value = default;

            public readonly uint Index;
            public readonly string Key;
            public readonly Type Type;
            public readonly bool IsOptional;
            public readonly bool IsOut;

            public object? Value
            {
                get => _hasValue ? _value : GetDefaultValue(_paramInfo);
                set
                {
                    _hasValue = true;
                    _value = value;
                }
            }

            public MatchArgSlot(uint index, ParameterInfo paramInfo,
                                bool isOptional = false, bool isOut = false)
            {
                _paramInfo = paramInfo;

                Index = index;
                Key = paramInfo.Name;
                Type = paramInfo.ParameterType;
                IsOptional = isOptional;
                IsOut = isOut;
            }


            public object? Convert()
            {
                if (TryCast(Value, Type, out object? cast))
                {
                    return cast;
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
                    return Type.Missing;
                else if (type.IsValueType)
                    return Activator.CreateInstance(type);
                else
                    return null;
            }

            static bool TryCast(object? source, Type to, out object? cast)
            {
                cast = default;

                if (source is null)
                {
                    return false;
                }

                Type from = source.GetType();

                MethodInfo castMethod =
                    // search for a cast operator in the source type
                    GetCast(from, from, to)
                    // search in the target type if not found in source type
                 ?? GetCast(to, from, to);

                if (castMethod != null)
                {
                    cast = castMethod.Invoke(null, new[] { source });
                    return true;
                }

                return false;
            }
        }

        private sealed class MatchSpec
        {
            const uint SECTOR_SIZE = uint.MaxValue / 4;
            const uint STATIC_DISTANCE = SECTOR_SIZE;
            const uint PARAMS_DISTANCE = SECTOR_SIZE;
            const uint ARG_DISTANCE = 32; // reserved for TypeCode range

            public readonly MethodBase Method;
            public readonly uint Required;
            public readonly uint Optional;
            public readonly bool Expands;
            public readonly MatchArgSlot[] ArgumentSlots;

            public MatchSpec(MethodBase method,
                             uint required,
                             uint optional,
                             bool expands,
                             MatchArgSlot[] argSpecs)
            {
                Method = method;
                Required = required;
                Optional = optional;
                Expands = expands;
                ArgumentSlots = argSpecs;
            }

            public object?[] GetArguments()
                => ArgumentSlots.Select(s => s.Convert()).ToArray();

            // no two specs should have the exact same distance as that means
            // the signatures are exactly the same and that is not possible
            public uint GetDistance(object?[] args, KeywordArgs kwargs)
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
                for (uint i = 0; i < ArgumentSlots.Length; i++)
                {
                    if (ArgumentSlots[i] is MatchArgSlot slot)
                    {
                        if (kwargs.TryGetValue(slot.Key, out object? kwargv))
                        {
                            slot.Value = kwargv;
                            score += GetDistance(kwargv, slot.Type);
                        }

                        else if (args.Length > 0
                                    && argidx < args.Length
                                    && args[argidx] is object argv)
                        {
                            slot.Value = argv;
                            score += GetDistance(argv, slot.Type);
                            argidx++;
                        }
                        else
                        {
                            score += ARG_DISTANCE;
                        }
                    }
                }

                return score;
            }

            static uint GetDistance(object? arg, Type to)
            {
                uint distance = ARG_DISTANCE;

                if (to.IsGenericType)
                {
                    distance += 2;
                }

                if (arg is object argument)
                {
                    Type argType = GetType(argument);

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
                            (int)GetDistance(to)
                           -(int)GetDistance(from)
                        );
                }

                s_distMap[key] = distance;

                return distance;
            }

            static uint GetDistance(Type of)
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

            static Type GetType(object arg) => arg.GetType();
        }

        public static T? Invoke<T>(object instance,
                                   string name,
                                   object?[] args,
                                   KeywordArgs kwargs)
        {
            if (instance is null)
            {
                return default;
            }

            MethodInfo[] methods =
                instance.GetType()
                        .GetMethods()
                        .Where(m => m.Name == name)
                        .ToArray();

            if (TryBind(methods, args, kwargs, out MatchSpec? spec))
            {
                object? result =
                    spec!.Method.Invoke(instance,
                                       spec.GetArguments());

                if (result != null
                    && typeof(T).IsAssignableFrom(result.GetType()))
                {
                    return (T)result;
                }
            }

            throw new MethodAccessException(name);
        }

        static bool TryBind(MethodBase[] methods,
                            object?[] args,
                            KeywordArgs kwargs,
                            out MatchSpec? spec)
        {
            spec = default;

            uint argsCount = (uint)args.Count();
            uint kwargsCount = (uint)kwargs.Count();
            HashSet<string> kwargKeys = new(kwargs.Keys);

            // Find any method that could accept this many args and kwargs
            int index = 0;
            MatchSpec?[] matches = new MatchSpec?[methods.Count()];
            uint totalArgs = argsCount + kwargsCount;
            foreach (MethodBase mb in methods)
            {
                if (TryMatch(mb, totalArgs, kwargKeys, out MatchSpec? matched))
                {
                    matches[index] = matched;
                    index++;
                }
            }

            return TryMatchClosest(matches, args, kwargs, out spec);
        }

        static bool TryMatchClosest(MatchSpec?[] specs,
                                    object?[] args,
                                    KeywordArgs kwargs,
                                    out MatchSpec? spec)
        {
            spec = null;

            if (specs.Length == 0)
            {
                return false;
            }
            else if (specs.Length == 1)
            {
                spec = specs[0];
                return true;
            }
            else if (specs.Length > 1)
            {
                uint closest = uint.MaxValue;
                foreach (MatchSpec? mspec in specs)
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

                return closest < uint.MaxValue;
            }

            return false;
        }

        static bool TryMatch(MethodBase m,
                             uint givenArgs,
                             HashSet<string> kwargs,
                             out MatchSpec? spec)
        {
            spec = default;

            uint required = 0;
            uint optional = 0;
            bool expands = false;

            MatchArgSlot[] argSpecs;
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

                argSpecs = new MatchArgSlot[length];

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
                            new MatchArgSlot(i, param, isOut: true);

                        continue;
                    }
                    // `.IsOptional` will be true if the parameter has
                    // a default value, or if the parameter has the `[Optional]`
                    // attribute specified.
                    else if (param.IsOptional)
                    {
                        argSpecs[i] =
                            new MatchArgSlot(i, param, isOptional: true);

                        optional++;
                    }
                    // otherwise this is a required parameter
                    else
                    {
                        argSpecs[i] =
                            new MatchArgSlot(i, param);

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
            }
            else
            {
                argSpecs = Array.Empty<MatchArgSlot>();
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
                        && (expands
                                || givenArgs <= (required + optional)))
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
            spec = new MatchSpec(m, required, optional, expands, argSpecs);
            return true;
        }

        static MethodInfo GetCast(Type @in, Type from, Type to)
        {
            return @in.GetMethods(FLAGS)
                       .FirstOrDefault(m =>
                       {
                           return (m.Name == OP_IMPLICIT
                                        || m.Name == OP_EXPLICIT)
                                && m.ReturnType == to
                                && m.GetParameters() is ParameterInfo[] pi
                                && pi.Length == 1
                                && pi[0].ParameterType == from;
                       });
        }
    }
}
