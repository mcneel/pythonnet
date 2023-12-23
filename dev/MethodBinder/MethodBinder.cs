using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace MethodBinder
{
    using KeywordArgs = Dictionary<string, object?>;

    public static class MethodInvoke
    {
        static readonly Type PAAT = typeof(ParamArrayAttribute);
        static readonly Type OBJT = typeof(object);

        private sealed class MatchArgSlot
        {
            public readonly uint Index;
            public readonly string Key;
            public readonly Type Type;
            public readonly bool IsOptional;

            public object? Value;

            public MatchArgSlot(uint index, ParameterInfo paramInfo,
                                bool isOptional = false)
            {
                Index = index;
                Key = paramInfo.Name;
                Type = paramInfo.ParameterType;
                IsOptional = isOptional;
                Value = default;
            }
        }

        private sealed class MatchSpec
        {
            const int STATIC_DISTANCE = 3000;
            const int GENERIC_DISTANCE = 1;
            const int ARRAY_DISTANCE = 100;
            const int MISSING_ARG_DISTANCE = 0;
            const int NULL_ARG_DISTANCE = 0;

            public readonly MethodBase Method;
            public readonly uint Required;
            public readonly uint Optional;
            public readonly bool Expands;
            public readonly MatchArgSlot?[] ArgumentSlots;

            public MatchSpec(MethodBase method,
                             uint required,
                             uint optional,
                             bool expands,
                             MatchArgSlot?[] argSpecs)
            {
                Method = method;
                Required = required;
                Optional = optional;
                Expands = expands;
                ArgumentSlots = argSpecs;
            }

            public object?[] GetArguments()
                => ArgumentSlots.Select(s => s.Value).ToArray();

            // (0, 0, 0) -> (a, b=0, c=0)
            // (0, a=0, c=0) -> (a, b, c)
            public int GetPrecedence(object?[] args, KeywordArgs kwargs)
            {
                int precedence = 0;

                if (Method.IsStatic)
                {
                    precedence += STATIC_DISTANCE;
                }

                if (Method.IsGenericMethod)
                {
                    precedence += GENERIC_DISTANCE;
                }

                uint argidx = 0;
                for (uint i = 0; i < ArgumentSlots.Length; i++)
                {
                    if (ArgumentSlots[i] is MatchArgSlot slot)
                    {
                        if (kwargs.TryGetValue(slot.Key, out object? kwargv))
                        {
                            slot.Value = kwargv;
                            precedence += GetDistance(kwargv, slot.Type);
                        }

                        else if (args.Length > 0
                                    && argidx < args.Length
                                    && args[argidx] is object argv)
                        {
                            slot.Value = argv;
                            precedence += GetDistance(argv, slot.Type);
                            argidx++;
                        }
                        else
                        {
                            precedence += MISSING_ARG_DISTANCE;
                        }
                    }
                }

                return precedence;
            }

            static int GetDistance(object? arg, Type paramType)
            {
                if (arg is object argument)
                {
                    return (int)GetPrecedence(paramType)
                         - (int)GetPrecedence(GetArgumentType(argument));
                }

                return NULL_ARG_DISTANCE;
            }

            static uint GetPrecedence(Type type)
            {
                if (type.IsArray)
                {
                    Type e = type.GetElementType();
                    return ARRAY_DISTANCE + GetPrecedence(e);
                }

                return (uint)Type.GetTypeCode(type);
            }

            static Type GetArgumentType(object arg) => arg.GetType();
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

            if (TryBind(methods, args, kwargs, out MatchSpec spec))
            {
                object? result =
                    spec.Method.Invoke(instance,
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
                            out MatchSpec spec)
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
                if (TryMatch(mb, totalArgs, kwargKeys, out MatchSpec matched))
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
                                    out MatchSpec spec)
        {
            spec = null;

            if (specs.Length > 0)
            {
                int highest = 0;
                foreach (MatchSpec mspec in specs.OfType<MatchSpec>())
                {
                    int precedence = mspec.GetPrecedence(args, kwargs);
                    if (precedence > highest)
                    {
                        highest = precedence;
                        spec = mspec;
                    }
                }

                return highest > 0;
            }

            return false;
        }

        static bool TryMatch(MethodBase m,
                             uint givenArgs,
                             HashSet<string> kwargs,
                             out MatchSpec spec)
        {
            spec = default;

            uint required = 0;
            uint optional = 0;
            bool expands = false;

            MatchArgSlot?[] argSpecs;
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

                argSpecs = new MatchArgSlot?[length];

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
                        argSpecs[i] = default;
                        continue;
                    }
                    // `.IsOptional` will be true if the parameter has
                    // a default value, or if the parameter has the `[Optional]`
                    // attribute specified.
                    else if (param.IsOptional)
                    {
                        argSpecs[i] = new MatchArgSlot(i, param, true);
                        optional++;
                    }
                    // otherwise this is a required parameter
                    else
                    {
                        argSpecs[i] = new MatchArgSlot(i, param);
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
                argSpecs = Array.Empty<MatchArgSlot?>();
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
    }
}
