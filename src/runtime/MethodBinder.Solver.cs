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
                    (*) <T> methods here are method with no generic parameters.
    ARGS     GROUP: Dedicated slot for each parameter. See MethodBinder.MAX_ARGS
    TYPE     GROUP: Kind of Type e.g. ByRef
    MATCH    GROUP: Kind of type match e.g. an exact match has a lower distance

                  MIN
    ┌───────────────┐       ┌───┐       ┌────────┐       ┌──────────────┐
    │               │ ────► │   │ ────► │        │ ───── │              │
  D │  INSTANCE     │       │   │       │        │       │ EXACT        │
  I │               │ ──┐   │   │ ──┐   │        │ ──┐   │              │
  S ├───────────────┤   │   ├───┤   │   │        │   │   ├──────────────┤
  T │               │   │   │┼┼┼│   │   │        │   │   │              │
  A │  INSTANCE<T>* │   │   │┼┼┼│   │   │        │   │   │ DERIVED      │
  N │               │   │   │┼┼┼│   │   │        │   │   │              │
  C ├───────────────┤   │   │┼┼┼│   │   ├────────┤   │   ├──────────────┤
  E │               │   │   │┼┼┼│   │   │        │   │   │              │
    │  STATIC       │   │   │┼┼┼│   │   │        │   │   │ GENERIC      │
  R │               │   │   │┼┼┼│   │   │        │   │   │              │
  A ├───────────────┤   │   │┼┼┼│   │   │ BY REF │   │   ├──────────────┤
  N │               │   │   │┼┼┼│   │   │        │   │   │              │
  G │  STATIC<T>*   │   │   │┼┼┼│   │   │        │   │   │ CAST/CONVERT │
  E │               │   └─► │┼┼┼│   └── │        │   └── │              │
    └───────────────┘       └───┘       └────────┘       └──────────────┘
    FUNCTION      MAX       ARGS        TYPE             MATCH
    GROUP                   GROUP       GROUP            GROUP

    */
    partial class MethodBinder
    {
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

            public object? Value { get; private set; } = default;

            public uint Distance { get; private set; } = uint.MaxValue;

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

            public void AssignValue(BorrowedReference item, bool computeDist)
            {
                Value = new PyObject(item);

                if (computeDist)
                {
                    Distance = GetTypeDistance(item, Type);
                }
            }

            public void AssignParamsValue(PyObject[] items)
            {
                Value = items;
            }

            public bool TryConvert(out object? value)
            {
                // NOTE:
                // value is either a PyObject for standard parameters,
                // or a PyObject[] for a capturing params[]
                object? v = Value;

                if (Type.IsGenericParameter)
                {
                    if (v is null)
                    {
                        value = null;
                        return true;
                    }

                    return TryGetManagedValue((PyObject)v, out value);
                }

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

                    // NOTE:
                    // if param is a capturing params[] and there
                    // is only one argument of sequence type provided,
                    // then lets convert that sequence to params[] type
                    // and pass that as the array of arguments.
                    // e.g. Calling .Foo(params int[]) as .Foo([1,2,3])
                    if (values.Length == 1
                            && values[0] is PyObject arg
                            && Runtime.PySequence_Check(arg))
                    {
                        Type ptype = _param.ParameterType;
                        return TryGetManagedValue(arg, ptype, out value);
                    }

                    // otherwise, lets build an array and add each arg
                    // e.g. Calling .Foo(params int[]) as .Foo(1,2,3)
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
            public static bool IsRedirected(MethodBase method) =>
                method.GetCustomAttributes<RedirectedMethod>().Any();

            public readonly MethodBase Method;
            public readonly uint Required;
            public readonly uint Optional;
            public readonly BindParam[] Parameters;

            public BindSpec(MethodBase method,
                            uint required,
                            uint optional,
                            BindParam[] argSpecs)
            {
                Method = method;
                Required = required;
                Optional = optional;
                Parameters = argSpecs;
            }

            public bool TryGetArguments(object? instance,
                                        out MethodBase method,
                                        out object?[] arguments)
            {
                method = Method;
                arguments = new object?[Parameters.Length];

                Type[] genericTypes =
                    new Type[
                        Method.IsGenericMethod ?
                            Method.GetGenericArguments().Length
                          : 0
                    ];

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

                        if (param.Type.IsGenericParameter)
                        {
                            genericTypes[i] = value?.GetType() ?? typeof(object);
                        }
                    }
                    else
                    {
                        return false;
                    }
                }

                if (Method.IsGenericMethod
                        && method.ContainsGenericParameters)
                {
                    try
                    {
                        // .MakeGenericMethod can throw ArgumentException if
                        // the type parameters do not obey the constraints.
                        if (Method is MethodInfo minfo)
                        {
                            method = minfo.MakeGenericMethod(genericTypes);
                        }
                    }
                    catch (ArgumentException)
                    {
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
                    distance += FUNC_GROUP_SIZE;
                }

                // NOTE:
                // if method contains generic parameters, the distance
                // compute logic will take that into consideration.
                // but methods can be generic with no generic paramerers,
                // e.g. Foo<T>(int, float).
                // this ensures these methods are furthur away from
                // non-generic instance methods with matching parameters
                if (Method.IsGenericMethod
                        && !Method.ContainsGenericParameters)
                {
                    distance += FUNC_GROUP_SIZE;
                    distance += (ARG_GROUP_SIZE *
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
                uint argsCount =
                    args != null ? (uint)Runtime.PyTuple_Size(args) : 0;
                bool checkArgs = argsCount > 0;

                uint kwargsCount =
                    kwargs != null ? (uint)Runtime.PyDict_Size(kwargs) : 0;
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
                            slot.AssignValue(item, computeDist);

                            if (computeDist)
                            {
                                distance += slot.Distance;
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

                                slot.AssignParamsValue(values);
                            }

                            // if args are already processsed, compute
                            // a default distance for this param slot
                            else if (argidx > argsCount)
                            {
                                distance += computeDist ? ARG_GROUP_SIZE : 0;
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
                                slot.AssignValue(item, computeDist);

                                if (computeDist)
                                {
                                    distance += slot.Distance;
                                }

                                argidx++;
                                continue;
                            }
                        }
                    }
                }

                return distance;
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

            return TryBindByValue(index, bindSpecs, args, kwargs, out spec);
        }

        // vertical filter
        static bool TryBindByValue(int count,
                                   BindSpec?[] specs,
                                   BorrowedReference args,
                                   BorrowedReference kwargs,
                                   out BindSpec? spec)
        {
            spec = null;

            if (count == 0)
            {
                return false;
            }

            if (count == 1)
            {
                spec = specs[0];
                spec!.SetArgs(args, kwargs);
                return true;
            }

            uint closest = uint.MaxValue;
            for (int sidx = 0; sidx < count; sidx++)
            {
                BindSpec mspec = specs[sidx]!;

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

                    // NOTE:
                    // if method has the same distance, and have the least
                    // optional parameters, lets look at distance computed
                    // for each parameter and choose method which starts with
                    // parameters closer to the given args.
                    // dotnet compiler usually does not allow compiling such
                    // method calls and calls it ambiguous but we need to make
                    // a decision eitherway in python.
                    // e.g. when passing (short, int) or (int, short)
                    // compiler will complain about ambiguous call between:
                    // .Foo(short, short)
                    // .Foo(int, int)
                    if (spec.Parameters.Length == mspec!.Parameters.Length)
                    {
                        int pcount = spec.Parameters.Length;
                        for (int pidx = 0; pidx < pcount; pidx++)
                        {
                            if (pidx < spec.Parameters.Length)
                            {
                                BindParam mp = mspec!.Parameters[pidx];
                                BindParam sp = spec.Parameters[pidx];

                                if (mp.Distance >= sp.Distance)
                                {
                                    break;
                                }

                                spec = mspec;
                                break;
                            }
                        }
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
                    Attribute.IsDefined(mparams[mparams.Length - 1],
                                        typeof(ParamArrayAttribute));

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
                        BindParamKind kind = param.ParameterType.IsByRef ?
                            BindParamKind.Return
                          : BindParamKind.Argument;

                        argSpecs[i] =
                            new BindParam(param, kind);

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
            spec = new BindSpec(method, required, optional, argSpecs);
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

        #region Distance Compute Logic
        static Dictionary<(TypeCode, TypeCode), uint> s_builtinDistMap =
            new Dictionary<(TypeCode, TypeCode), uint>
            {
                [(TypeCode.UInt64, TypeCode.UInt64)] = 0,
                [(TypeCode.UInt64, TypeCode.Int64)] = 1,
                [(TypeCode.UInt64, TypeCode.UInt32)] = 2,
                [(TypeCode.UInt64, TypeCode.Int32)] = 3,
                [(TypeCode.UInt64, TypeCode.UInt16)] = 4,
                [(TypeCode.UInt64, TypeCode.Int16)] = 5,
                [(TypeCode.UInt64, TypeCode.Double)] = 6,
                [(TypeCode.UInt64, TypeCode.Single)] = 7,
                [(TypeCode.UInt64, TypeCode.Decimal)] = 7,
                [(TypeCode.UInt64, TypeCode.Object)] = uint.MaxValue - 1,
                [(TypeCode.UInt64, TypeCode.DateTime)] = uint.MaxValue,

                [(TypeCode.UInt32, TypeCode.UInt32)] = 0,
                [(TypeCode.UInt32, TypeCode.Int32)] = 1,
                [(TypeCode.UInt32, TypeCode.UInt16)] = 2,
                [(TypeCode.UInt32, TypeCode.Int16)] = 3,

                [(TypeCode.UInt16, TypeCode.UInt16)] = 0,
                [(TypeCode.UInt16, TypeCode.Int16)] = 1,

                [(TypeCode.Int64, TypeCode.Int64)] = 0,
                [(TypeCode.Int32, TypeCode.Int32)] = 0,
                [(TypeCode.Int16, TypeCode.Int16)] = 0,
            };

#if METHODBINDER_SOLVER_NEW_CACHE_DIST
        static readonly Dictionary<int, uint> s_distMap = new();
#endif

        // https://stackoverflow.com/a/17268854/2350244
        // assumes two groups of parameters, hence *2
        // Foo<generic-params>(params)
        static readonly uint MAX_ARGS =
            Convert.ToUInt32(Math.Pow(2, 16)) * 2;

        static readonly uint TOTAL_MAX_DIST = uint.MaxValue;
        static readonly uint FUNC_GROUP_SIZE = TOTAL_MAX_DIST / 4;
        static readonly uint ARG_GROUP_SIZE = FUNC_GROUP_SIZE / MAX_ARGS;
        static readonly uint TYPE_GROUP_SIZE = ARG_GROUP_SIZE / 4;
        static readonly uint MATCH_GROUP_SIZE = TYPE_GROUP_SIZE / 4;

        public static uint GetTypeDistance(BorrowedReference from, Type to)
        {
            if (GetCLRType(from) is Type argType)
            {
                return GetTypeDistance(argType, to);
            }

            return ARG_GROUP_SIZE;
        }

        static uint GetTypeDistance(Type from, Type to)
        {
#if METHODBINDER_SOLVER_NEW_CACHE_DIST
            int key = ComputeKey(from, to);
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
                distance += TYPE_GROUP_SIZE;
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
                distance = ARG_GROUP_SIZE;
                goto computed;
            }

            // derived match
            distance += MATCH_GROUP_SIZE;
            if (to.IsAssignableFrom(from))
            {
                distance += GetDerivedTypeDistance(from, to);
                goto computed;
            }

            // generic match
            distance += MATCH_GROUP_SIZE;
            if (to.IsGenericParameter)
            {
                goto computed;
            }

            // cast/convert match
            distance += MATCH_GROUP_SIZE;
            if (TryGetPrecedence(from, out uint fromPrec)
                    && TryGetPrecedence(to, out uint toPrec))
            {
                distance += GetConvertTypeDistance(fromPrec, toPrec);
                goto computed;
            }

            distance = ARG_GROUP_SIZE;

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
                        && depth < MATCH_GROUP_SIZE)
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

        static bool TryGetPrecedence(Type of, out uint predecence)
        {
            predecence = 0;

            if (of is null)
            {
                return false;
            }

            if (of.IsArray)
            {
                return TryGetPrecedence(of.GetElementType(), out predecence);
            }

            if (typeof(nuint) == of)
            {
                predecence = 30;
                return true;
            }

            if (typeof(nint) == of)
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

        static int ComputeKey(Type from, Type to)
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 23 + from.GetHashCode();
                hash = hash * 23 + to.GetHashCode();
                return hash;
            }
        }
        #endregion
    }
}
