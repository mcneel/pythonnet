#define METHODBINDER_SOLVER_NEW_CACHE_DIST
using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Diagnostics;

namespace Python.Runtime
{
    /*
    ┌── INPUT ARGS/KWARGS
    │
    ▼

    HORIZONTAL FILTER:
    Filter functions based on number of arguments provided and parameter count.

        ──────────────────────────►
        FOO<T,T,T,T,...>(X,Y,Z,...)

    │
    ▼

    VERTICAL FILTER:
    Filter functions based on kind (static vs instance), argument types,
    function parameter type, and type of parameter match. Compute a distance
    value for each potential function based on aforementioned properties.
    
            │ │ │ │      │ │ │
            ▼ ▼ ▼ ▼      ▼ ▼ ▼
        FOO<T,T,T,T,...>(X,Y,Z,...)

    Distance Range:
    This is the overall range of computed distance for each function.
    It is split into blocks to ensure various function kinds do not
    end up with overlapping distances e.g. a static generic method with
    fewer parameters, matching distance with an instance method with a
    larger number of parameters.

    The groups are:

    FUNCTION GROUP: Kind of function e.g. static vs instance
    ARGS     GROUP: Dedicated slot for each parameter. See BindSpec.MAX_ARGS
    TYPE     GROUP: Kind of Type e.g. ByRef
    MATCH    GROUP: Kind of type match e.g. an exact match has a lower distance

                  MIN
    ┌───────────────┐       ┌───┐       ┌────────┐       ┌──────────────┐
    │               │ ───── │   │ ───── │        │ ───── │              │
  D │  INSTANCE     │       │   │       │        │       │ EXACT        │
  I │               │ ──┐   │   │ ──┐   │        │ ──┐   │              │
  S ├───────────────┤   │   ├───┤   │   │        │   │   ├──────────────┤
  T │               │   │   │┼┼┼│   │   │        │   │   │              │
  A │  INSTANCE<T>  │   │   │┼┼┼│   │   │        │   │   │ DERIVED      │
  N │               │   │   │┼┼┼│   │   │        │   │   │              │
  C ├───────────────┤   │   │┼┼┼│   │   ├────────┤   │   ├──────────────┤
  E │               │   │   │┼┼┼│   │   │        │   │   │              │
    │  STATIC       │   │   │┼┼┼│   │   │        │   │   │ GENERIC      │
  R │               │   │   │┼┼┼│   │   │        │   │   │              │
  A ├───────────────┤   │   │┼┼┼│   │   │ BY REF │   │   ├──────────────┤
  N │               │   │   │┼┼┼│   │   │        │   │   │              │
  G │  STATIC<T>    │   │   │┼┼┼│   │   │        │   │   │ CAST/CONVERT │
  E │               │   └── │┼┼┼│   └── │        │   └── │              │
    └───────────────┘       └───┘       └────────┘       └──────────────┘
    FUNCTION      MAX       ARGS        TYPE             MATCH
    GROUP                   GROUP       GROUP            GROUP

    */
    partial class MethodBinder
    {
        static readonly Type PAAT = typeof(ParamArrayAttribute);
        static readonly Type UNINT = typeof(nuint);
        static readonly Type NINT = typeof(nint);

#if METHODBINDER_SOLVER_NEW_CACHE_DIST
        static readonly Dictionary<int, uint> s_distMap = new();
#endif

        private enum BindParamKind
        {
            Argument,
            Option,
            Params,
            Return,
            Self,
        }

        private sealed class BindParam
        {
            readonly ParameterInfo _param;

            public readonly BindParamKind Kind = BindParamKind.Argument;
            public readonly string Key;
            public readonly Type Type;

            public object? Value { get; set; } = default;

            public BindParam(ParameterInfo paramInfo, BindParamKind kind)
            {
                _param = paramInfo;

                Kind = kind;
                Key = paramInfo.Name;

                // this type is used to compute distance between
                // provided argument type and parameter type.
                // for capturing params[] lets use the element type.
                if (Kind == BindParamKind.Params)
                {
                    Type = paramInfo.ParameterType.GetElementType();
                }
                else
                {
                    Type = paramInfo.ParameterType;
                }
            }

            public bool TryConvert(out object? value)
            {
                // NOTE:
                // value is either a PyObject for standard parameters,
                // or a PyObject[] for a capturing params[]
                object? v = Value;

                if (v is null)
                {
                    if (Kind == BindParamKind.Option)
                    {
                        value = GetDefaultValue(_param);
                        return true;
                    }

                    if (Kind == BindParamKind.Params)
                    {
                        value = Array.CreateInstance(Type, 0);
                        return true;
                    }

                    value = null;
                    return true;
                }

                if (Kind == BindParamKind.Params)
                {
                    PyObject[] values = (PyObject[])v;
                    var parray = Array.CreateInstance(Type, values.Length);

                    for (int i = 0; i < values.Length; i++)
                    {
                        if (TryGetManagedValue(values[i], Type, out object? p))
                        {
                            parray.SetValue(p, i);
                        }
                        else
                        {
                            parray.SetValue(GetDefaultValue(_param), i);
                        }
                    }

                    value = parray;
                    return true;
                }

                return TryGetManagedValue((PyObject)v, Type, out value);
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
            // https://stackoverflow.com/a/17268854/2350244
            // assumes two groups of parameters, hence *2
            // Foo<generic-params>(params)
            static readonly uint MAX_ARGS =
                Convert.ToUInt32(Math.Pow(2, 16)) * 2;

            static readonly uint TOTAL_MAX_DIST = uint.MaxValue;
            static readonly uint FUNC_MAX_DIST = TOTAL_MAX_DIST / 4;
            static readonly uint ARG_MAX_DIST = FUNC_MAX_DIST / MAX_ARGS;
            static readonly uint TYPE_MAX_DIST = ARG_MAX_DIST / 4;
            static readonly uint MATCH_MAX_DIST = TYPE_MAX_DIST / 4;

            public static bool IsRedirected(MethodBase method) =>
                method.GetCustomAttributes<RedirectedMethod>().Any();

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

            public bool TryGetArguments(object? instance,
                                        out object?[] arguments)
            {
                arguments = new object?[Parameters.Length];

                for (int i = 0; i < Parameters.Length; i++)
                {
                    BindParam param = Parameters[i];
                    if (param.Kind == BindParamKind.Self)
                    {
                        arguments[i] = instance;
                    }
                    else if (param.TryConvert(out object? value))
                    {
                        arguments[i] = value;
                    }
                    else
                    {
                        return false;
                    }
                }

                return true;
            }

            public void SetArgs(BorrowedReference args,
                                     BorrowedReference kwargs)
            {
                ExtractParameters(args, kwargs, computeDist: false);
            }

            public uint SetArgsAndGetDistance(BorrowedReference args,
                                                   BorrowedReference kwargs)
            {
                uint distance = 0;

                if (Method.IsStatic)
                {
                    distance += FUNC_MAX_DIST;
                }

                if (Method.IsGenericMethod)
                {
                    distance += FUNC_MAX_DIST;
                    distance += (ARG_MAX_DIST *
                                (uint)Method.GetGenericArguments().Length);
                }

                distance += ExtractParameters(args, kwargs, computeDist: true);

                Debug.WriteLine($"{Method} -> {distance}");

                return distance;
            }

            uint ExtractParameters(BorrowedReference args,
                                   BorrowedReference kwargs,
                                   bool computeDist)
            {
                uint distance = 0;

                uint argidx = 0;
                uint argsCount = args != null ? (uint)Runtime.PyTuple_Size(args) : 0;
                bool checkArgs = argsCount > 0;

                uint kwargsCount = kwargs != null ? (uint)Runtime.PyDict_Size(kwargs) : 0;
                bool checkKwargs = kwargsCount > 0;

                for (uint i = 0; i < Parameters.Length; i++)
                {
                    BindParam slot = Parameters[i];
                    if (slot is null)
                    {
                        break;
                    }

                    BorrowedReference item;

                    // NOTE:
                    // value of self if provided on invoke
                    // since this is the instance the bound
                    // method is being invoked on. lets skip.
                    if (slot.Kind == BindParamKind.Self)
                    {
                        continue;
                    }

                    // NOTE:
                    // if kwargs contains a value for this parameter,
                    // lets capture that and compute distance.
                    if (checkKwargs)
                    {
                        item = Runtime.PyDict_GetItemString(kwargs, slot.Key);
                        if (item != null)
                        {
                            slot.Value = new PyObject(item);

                            if (computeDist)
                            {
                                distance +=
                                   GetTypeDistance(item, slot.Type);
                            }

                            continue;
                        }
                    }

                    // NOTE:
                    // now that we have checked kwargs for potential matches
                    // and found none, lets look in the args to find one
                    // for this param and compute distance.
                    if (checkArgs)
                    {
                        // NOTE:
                        // if this param is a capturing params[], walk
                        // through all remaining args and add them to the
                        // list of args for this capturing array.
                        // compute distance based on type of the first arg.
                        // this is always the last param in this loop.
                        if (slot.Kind == BindParamKind.Params)
                        {
                            // if there are remaining args, capture them
                            if (argidx < argsCount)
                            {
                                uint count = argsCount - argidx;
                                var values = new PyObject[count];
                                for (uint ai = argidx,
                                          vi = 0; ai < argsCount; ai++, vi++)
                                {
                                    item = Runtime.PyTuple_GetItem(args, (nint)ai);
                                    if (item != null)
                                    {
                                        values[vi] = new PyObject(item);

                                        // compute distance on first arg
                                        // that is being captured by params []
                                        if (computeDist && ai == argidx)
                                        {
                                            distance +=
                                               GetTypeDistance(item, slot.Type);
                                        }

                                        argidx++;
                                        continue;
                                    }
                                }

                                slot.Value = values;
                            }

                            // if args are already processsed, compute
                            // a default distance for this param slot
                            else if (argidx > argsCount)
                            {
                                distance += computeDist ? ARG_MAX_DIST : 0;
                            }

                            continue;
                        }

                        // NOTE:
                        // otherwise look into the args and grab one
                        // for this param if available. compute distance
                        // based on type of the arg.
                        else if (argidx < argsCount)
                        {
                            item = Runtime.PyTuple_GetItem(args, (nint)argidx);
                            if (item != null)
                            {
                                slot.Value = new PyObject(item);

                                if (computeDist)
                                {
                                    distance +=
                                       GetTypeDistance(item, slot.Type);
                                }

                                argidx++;
                                continue;
                            }
                        }
                    }
                }

                return distance;
            }

            static uint GetTypeDistance(BorrowedReference from, Type to)
            {
                if (GetCLRType(from, to) is Type argType)
                {
                    return GetTypeDistance(argType, to);
                }

                return ARG_MAX_DIST;
            }

            static uint GetTypeDistance(Type from, Type to)
            {
#if METHODBINDER_SOLVER_NEW_CACHE_DIST
                int key = from.GetHashCode() + to.GetHashCode();
                if (s_distMap.TryGetValue(key, out uint dist))
                {
                    return dist;
                }
#endif

                uint distance = 0;

                // NOTE:
                // shift distance based on type kind
                if (to.IsByRef)
                {
                    distance += TYPE_MAX_DIST;
                }

                // NOTE:
                // shift distance based on match kind
                // exact match
                if (from == to)
                {
                    goto computed;
                }

                if (from.IsArray != to.IsArray)
                {
                    distance = ARG_MAX_DIST;
                    goto computed;
                }

                // derived match
                distance += MATCH_MAX_DIST;
                if (to.IsAssignableFrom(from))
                {
                    distance += GetDerivedTypeDistance(from, to);
                    goto computed;
                }

                // generic match
                distance += MATCH_MAX_DIST;
                if (to.IsGenericType)
                {
                    goto computed;
                }

                // cast/convert match
                distance += MATCH_MAX_DIST;
                if (TryGetTypePrecedence(from, out uint fromPrec)
                        && TryGetTypePrecedence(to, out uint toPrec))
                {
                    distance += GetConvertTypeDistance(fromPrec, toPrec);
                    goto computed;
                }

                distance = ARG_MAX_DIST;

            computed:
#if METHODBINDER_SOLVER_NEW_CACHE_DIST
                s_distMap[key] = distance;
#endif
                return distance;
            }

            // zero when types are equal.
            // assumes derived is assignable to @base
            // 0 <= x < MATCH_MAX_DIST
            static uint GetDerivedTypeDistance(Type derived, Type @base)
            {
                uint depth = 0;

                Type t = derived;
                while (t != null
                            && t != @base
                            && depth < MATCH_MAX_DIST)
                {
                    depth++;
                    t = t.BaseType;
                }

                return depth;
            }

            // zero when types are equal.
            // 0 <= x < MATCH_MAX_DIST
            static uint GetConvertTypeDistance(uint from, uint to)
            {
                return (uint)Math.Abs((int)to - (int)from);
            }

            static bool TryGetTypePrecedence(Type of, out uint predecence)
            {
                predecence = 0;

                if (UNINT == of)
                {
                    predecence = 30;
                    return true;
                }

                if (NINT == of)
                {
                    predecence = 31;
                    return true;
                }

                switch (Type.GetTypeCode(of))
                {
                    // 0-9
                    case TypeCode.Object:
                        predecence = 1;
                        return true;

                    // 10-19
                    case TypeCode.UInt64:
                        predecence = 10;
                        return true;

                    case TypeCode.UInt32:
                        predecence = 11;
                        return true;

                    case TypeCode.UInt16:
                        predecence = 12;
                        return true;

                    case TypeCode.Int64:
                        predecence = 13;
                        return true;

                    case TypeCode.Int32:
                        predecence = 14;
                        return true;

                    case TypeCode.Int16:
                        predecence = 15;
                        return true;

                    case TypeCode.Char:
                        predecence = 16;
                        return true;

                    case TypeCode.SByte:
                        predecence = 17;
                        return true;

                    case TypeCode.Byte:
                        predecence = 18;
                        return true;

                    // 20-29

                    // 30-39
                    // UIntPtr  |   nuint
                    // IntPtr   |   nint

                    // 40-49
                    case TypeCode.Single:
                        predecence = 40;
                        return true;

                    case TypeCode.Double:
                        predecence = 41;
                        return true;

                    // 50-59
                    case TypeCode.String:
                        predecence = 50;
                        return true;

                    // 60-69
                    case TypeCode.Boolean:
                        predecence = 60;
                        return true;
                }

                return false;
            }
        }

        static bool TryBind(MethodBase[] methods,
                            BorrowedReference args,
                            BorrowedReference kwargs,
                            bool allowRedirected,
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
            BindSpec?[] bindSpecs = new BindSpec?[methods.Length];
            uint totalArgs = argsCount + kwargsCount;

            foreach (MethodBase method in methods)
            {
                // NOTE:
                // methods could be marked as Original, or Redirected.
                // lets skip redirected methods if redirection is not allowed.
                // redirection is usuall allowed so we check that first.
                if (!allowRedirected && BindSpec.IsRedirected(method))
                {
                    continue;
                }

                if (TryBindByCount(method, totalArgs, kwargKeys,
                                   out BindSpec? bindSpec))
                {
                    // if givenArgs==0 find the method with zero params.
                    // that should take precedence over any other method
                    // with optional params that could be called as .Foo()
                    // without the optional params. This should make calling
                    // zero-parameter functions faster.
                    if (totalArgs == 0
                            && bindSpec!.Parameters.Length == 0)
                    {
                        spec = bindSpec;
                        return true;
                    }

                    bindSpecs[index] = bindSpec;
                    index++;
                }
            }

            return TryBindByValue(bindSpecs, args, kwargs, out spec);
        }

        // vertical filter
        static bool TryBindByValue(BindSpec?[] specs,
                                   BorrowedReference args,
                                   BorrowedReference kwargs,
                                   out BindSpec? spec)
        {
            spec = null;

            if (specs.Length == 0)
            {
                return false;
            }

            if (specs.Length > 1
                    && specs[0] is BindSpec onlySpec
                    && specs[1] is null)
            {
                spec = onlySpec;
                spec!.SetArgs(args, kwargs);
                return true;
            }

            uint closest = uint.MaxValue;
            foreach (BindSpec? mspec in specs)
            {
                if (mspec is null)
                {
                    break;
                }

                uint distance = mspec!.SetArgsAndGetDistance(args, kwargs);

                // NOTE:
                // if method has the exact same distance,
                // lets look at a few other properties to determine which
                // method should be used.
                if (distance == closest
                        && distance != uint.MaxValue)
                {
                    // NOTE:
                    // if there is a redirected method with same distance,
                    // lets use that. redirected methods should be in order
                    // or redirection e.g. origial->redirected->redirected
                    if (BindSpec.IsRedirected(mspec!.Method))
                    {
                        spec = mspec;
                    }

                    // NOTE:
                    // if method has the same distance, lets use the one
                    // with the least amount of optional parameters.
                    // e.g. between .Foo(int) and .Foo(int, float=0)
                    // we would pick the former since it is shorter.
                    // we do not compute the optional parameters that do not
                    // have a matching input arguments in the distance.
                    // otherwise .Foo(double) might end up closer than
                    // .Foo(float, float=0) when passing a float input, since
                    // including optional float mightpush distance further
                    // away from computed distance for .Foo(double), depending
                    // on the computed distance for the types.
                    if (mspec!.Optional < spec!.Optional)
                    {
                        spec = mspec;
                    }
                }
                else if (distance < closest)
                {
                    closest = distance;
                    spec = mspec;
                }
            }

            return spec is not null;
        }

        // horizontal filter
        static bool TryBindByCount(MethodBase method,
                                   uint givenArgs,
                                   HashSet<string> kwargs,
                                   out BindSpec? spec)
        {
            spec = default;

            ParameterInfo[] mparams = method.GetParameters();

            uint required = 0;
            uint optional = 0;
            bool expands = false;
            bool isOperator = IsOperatorMethod(method)
                                && givenArgs == mparams.Length - 1;
            bool isReverse = isOperator && IsReverse(method);

            if (isReverse && IsComparisonOp(method))
            {
                return false;
            }

            BindParam[] argSpecs;

            if (isOperator)
            {
                // binary operators
                if (mparams.Length == 2)
                {
                    required = 1;

                    if (isReverse)
                    {
                        argSpecs = new BindParam[]
                        {
                        new BindParam(mparams[0], BindParamKind.Argument),
                        new BindParam(mparams[1], BindParamKind.Self),
                        };
                    }
                    else
                    {
                        argSpecs = new BindParam[]
                        {
                        new BindParam(mparams[0], BindParamKind.Self),
                        new BindParam(mparams[1], BindParamKind.Argument),
                        };
                    }
                }

                // unary operators
                // NOTE:
                // we are not checking for mparams.Length == 0 since that
                // should never happen. Operator methods must have at least
                // one parameter (operand) defined.
                else
                {
                    argSpecs = new BindParam[]
                    {
                        new BindParam(mparams[0], BindParamKind.Self),
                    };
                }

                goto matched;
            }

            if (mparams.Length > 0)
            {
                uint kwargCount = (uint)kwargs.Count;
                uint length = (uint)mparams.Length;
                uint last = length - 1;

                // check if last parameter is a params array.
                // this means method can accept parameters
                // beyond what is required.
                expands =
                    Attribute.IsDefined(mparams[mparams.Length - 1], PAAT);

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
                            new BindParam(param, BindParamKind.Return);

                        continue;
                    }
                    // `.IsOptional` will be true if the parameter has
                    // a default value, or if the parameter has the `[Optional]`
                    // attribute specified.
                    else if (param.IsOptional)
                    {
                        argSpecs[i] =
                            new BindParam(param, BindParamKind.Option);

                        optional++;
                    }
                    // if this is last param, and method is expanding,
                    // mark this slot as catcher for all extra args
                    else if (expands && i == last)
                    {
                        argSpecs[i] =
                            new BindParam(param, BindParamKind.Params);
                    }
                    // otherwise this is a required parameter
                    else
                    {
                        argSpecs[i] =
                            new BindParam(param, BindParamKind.Argument);

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
            spec = new BindSpec(method, required, optional, expands, argSpecs);
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
