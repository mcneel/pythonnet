#define METHODBINDER_SOLVER_NEW_CACHE_DIST
#define METHODBINDER_SOLVER_NEW_CACHE_TYPE
using System;
using System.Reflection;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace Python.Runtime
{
    public sealed class StrongBox<T>
    {
        public T? Value { get; set; }

        public StrongBox()
        {
            Value = default;
        }
    }

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
  S ├───────────────┤   │   ├───┤   │   ├────────┤   │   ├──────────────┤
  T │               │   │   │┼┼┼│   │   │        │   │   │              │
  A │  INSTANCE<T>  │   │   │┼┼┼│   │   │ BY REF │   │   │ DERIVED      │
  N │               │   │   │┼┼┼│   │   │        │   │   │              │
  C ├───────────────┤   │   │┼┼┼│   │   ├────────┤   │   ├──────────────┤
  E │               │   │   │┼┼┼│   │   │        │   │   │              │
    │  STATIC       │   │   │┼┼┼│   │   │ resvd. │   │   │ GENERIC      │
  R │               │   │   │┼┼┼│   │   │        │   │   │              │
  A ├───────────────┤   │   │┼┼┼│   │   ├────────┤   │   ├──────────────┤
  N │               │   │   │┼┼┼│   │   │        │   │   │              │
  G │  STATIC<T>    │   │   │┼┼┼│   │   │ resvd. │   │   │ CONVERT/CAST │
  E │               │   └─► │┼┼┼│   └── │        │   └── │              │
    └───────────────┘       └───┘       └────────┘       └──────────────┘
    FUNCTION      MAX       ARGS        TYPE             MATCH
    GROUP                   GROUP       GROUP            GROUP

    */
    partial class MethodBinder
    {
        private ref struct ArgProvider
        {
            private readonly BorrowedReference _args;
            private readonly BorrowedReference _kwargs;

# if METHODBINDER_SOLVER_NEW_CACHE_TYPE
            private readonly Dictionary<IntPtr, Type?> _typeMap = new();
#endif

            public uint ArgsCount;
            public uint KWargsCount;
            public uint GivenArgs;
            public HashSet<string> Keys;

            public ArgProvider(BorrowedReference args,
                               BorrowedReference kwargs)
            {
                _args = args;
                _kwargs = kwargs;

                int argSize = _args == null ? 0 : (int)Runtime.PyTuple_Size(_args);
                ArgsCount = (uint)(argSize == -1 ? 0 : argSize);

                int kwargSize = _kwargs == null ? 0 : (int)Runtime.PyDict_Size(_kwargs);
                KWargsCount = (uint)(kwargSize == -1 ? 0 : kwargSize);
                Keys = kwargSize > 0 ? GetKeys(_kwargs) : new();

                GivenArgs = ArgsCount + KWargsCount;
            }

            public readonly BorrowedReference GetArg(uint index)
            {
                return Runtime.PyTuple_GetItem(_args, (nint)index);
            }

            public readonly BorrowedReference GetKWArg(string key)
            {
                return Runtime.PyDict_GetItemString(_kwargs, key);
            }

            public readonly Type? GetCLRType(BorrowedReference op)
            {
#if UNIT_TEST_DEBUG || !METHODBINDER_SOLVER_NEW_CACHE_TYPE
                return MethodBinder.GetCLRType(op);
#else

                IntPtr ptr = op.DangerousGetAddressOrNull();
                if (IntPtr.Zero == ptr)
                {
                    return null;
                }

                if (_typeMap.ContainsKey(ptr))
                {
                    return _typeMap[ptr];
                }

                Type? clrType = MethodBinder.GetCLRType(op);

                _typeMap[ptr] = clrType;

                return clrType;
#endif
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

        private enum BindParamKind
        {
            Default,
            Option,
            Params,
            Return,
            Self,
        }

        private sealed class BindParam
        {
            readonly ParameterInfo _param;

            public readonly BindParamKind Kind = BindParamKind.Default;
            public readonly string Key;
            public readonly Type Type;

            object? _value = default;
            public object? Value
            {
                get => _value;
                set
                {
                    _value = value;

                    if (_value is null)
                    {
                        return;
                    }

                    if (_value is PyObject pyObj)
                    {
                        IsBoxed = IsBoxedValue(pyObj);
                    }
                }
            }

            public uint Distance { get; set; } = uint.MaxValue;

            public bool IsBoxed { get; set; } = false;

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

                if (IsBoxed)
                {
                    PyObject box = (PyObject)v;
                    PyObject boxed = box!.GetAttr("Value");
                    return TryGetManagedValue(boxed, Type, out value);
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
                            continue;
                        }

                        value = default;
                        return false;
                        //else
                        //{
                        //    parray.SetValue(GetDefaultValue(_param), i);
                        //}
                    }

                    value = parray;
                    return true;
                }

                return TryGetManagedValue((PyObject)v, Type, out value);
            }

            static bool IsBoxedValue(PyObject value)
            {
                Type? opType = GetCLRType(value);
                if (opType == null)
                {
                    return false;
                }

                if (opType.IsGenericType
                        && opType.GetGenericTypeDefinition() == typeof(StrongBox<>))
                {
                    return true;
                }

                return false;
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
                method.GetCustomAttribute<RedirectedMethod>() != null;

            public readonly MethodBase Method;
            public readonly BindParam[] Parameters;
            public readonly uint Required;
            public readonly uint Optional;
            public readonly uint Returns;
            public readonly bool Expands;

            public BindSpec(MethodBase method,
                            ref BindParam[] argSpecs,
                            uint required,
                            uint optional,
                            uint returns,
                            bool expands)
            {
                Method = method;
                Parameters = argSpecs;
                Required = required;
                Optional = optional;
                Returns = returns;
                Expands = expands;
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

            public void AssignArguments(ref ArgProvider prov)
            {
                ExtractArguments(ref prov, computeDist: false);
            }

            public uint AssignArgumentsEx(ref ArgProvider prov)
            {
                uint distance = 0;

                if (Method.IsStatic)
                {
                    distance += FUNC_GROUP_SIZE;
                }

                // NOTE:
                // if method contains generic parameters, the distance
                // compute logic will take that into consideration.
                // but methods can be generic with no generic parameters,
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

#if UNIT_TEST_DEBUG
                Debug.WriteLine($"{Method} -> {distance}");
#endif

                distance += ExtractArguments(ref prov, computeDist: true);

                return distance;
            }

            uint ExtractArguments(ref ArgProvider prov, bool computeDist)
            {
                uint distance = 0;

                uint argidx = 0;
                bool checkArgs = prov.ArgsCount > 0;
                bool checkKwargs = prov.KWargsCount > 0;

                for (uint i = 0; i < Parameters.Length; i++)
                {
                    BindParam param = Parameters[i];
                    if (param is null)
                    {
                        break;
                    }

                    BorrowedReference item;

                    // NOTE:
                    // value of self if provided on invoke
                    // since this is the instance the bound
                    // method is being invoked on. lets skip.
                    if (param.Kind == BindParamKind.Self)
                    {
                        continue;
                    }

                    // NOTE:
                    // if kwargs contains a value for this parameter,
                    // lets capture that and compute distance.
                    if (checkKwargs)
                    {
                        item = prov.GetKWArg(param.Key);
                        if (item != null)
                        {
                            PyObject value = new(item);

                            // NOTE:
                            // if this param is a capturing params[], expect
                            // a tuple or a list as value for this parameter.
                            // e.g. From python calling .Foo(paramsArray=[])
                            if (param.Kind == BindParamKind.Params)
                            {
                                param.Value = new PyObject[] { value };
                            }
                            else
                            {
                                param.Value = value;
                            }

                            if (computeDist)
                            {
                                param.Distance =
                                    GetDistance(ref prov, item, param);
                                distance += param.Distance;
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
                        if (param.Kind == BindParamKind.Params)
                        {
                            // if there are remaining args, capture them
                            if (argidx < prov.ArgsCount)
                            {
                                uint count = prov.ArgsCount - argidx;
                                var values = new PyObject[count];
                                for (uint ai = argidx,
                                          vi = 0; ai < prov.ArgsCount; ai++, vi++)
                                {
                                    item = prov.GetArg(ai);
                                    if (item != null)
                                    {
                                        values[vi] = new PyObject(item);

                                        // compute distance on first arg
                                        // that is being captured by params []
                                        if (computeDist && ai == argidx)
                                        {
                                            param.Distance =
                                                GetDistance(ref prov, item, param);
                                            distance += param.Distance;
                                        }

                                        argidx++;
                                        continue;
                                    }
                                }

                                param.Value = values;
                            }

                            // if args are already processsed, compute
                            // a default distance for this param slot
                            else if (argidx > prov.ArgsCount)
                            {
                                distance += ARG_GROUP_SIZE;
                            }

                            continue;
                        }

                        // NOTE:
                        // otherwise look into the args and grab one
                        // for this param if available. compute distance
                        // based on type of the arg.
                        else if (argidx < prov.ArgsCount)
                        {
                            item = prov.GetArg(argidx);
                            if (item != null)
                            {
                                param.Value = new PyObject(item);

                                if (computeDist)
                                {
                                    param.Distance =
                                        GetDistance(ref prov, item, param);
                                    distance += param.Distance;
                                }

                                argidx++;
                                continue;
                            }
                        }
                    }

                    // NOTE:
                    // if parameter does not have a match,
                    // lets increase the distance
                    if (param.Kind == BindParamKind.Default)
                    {
                        distance += ARG_GROUP_SIZE;
                    }
                }

                return distance;
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

        static bool TryBind(MethodBase[] methods,
                            BorrowedReference args,
                            BorrowedReference kwargs,
                            bool allowRedirected,
                            out BindSpec? spec,
                            out BindError? error)
        {
            spec = default;
            error = default;

            ArgProvider provider = new(args, kwargs);

            // Find any method that could accept this many args and kwargs
            int index = 0;
            BindSpec?[] specs = new BindSpec?[methods.Length];

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

                if (TryBindByCount(method, ref provider,
                                   out BindSpec? bindSpec))
                {
                    // if givenArgs==0 find the method with zero params.
                    // that should take precedence over any other method
                    // with optional params that could be called as .Foo()
                    // without the optional params. This should make calling
                    // zero-parameter functions faster.
                    if (provider.GivenArgs == 0
                            && bindSpec!.Parameters.Length == 0)
                    {
                        spec = bindSpec;
                        return true;
                    }

                    specs[index] = bindSpec;
                    index++;
                }
            }

            return TryBindByValue(index, ref specs, ref provider,
                                  out spec, out error);
        }

        // vertical filter
        static bool TryBindByValue(int count,
                                   ref BindSpec?[] specs,
                                   ref ArgProvider prov,
                                   out BindSpec? spec,
                                   out BindError? error)
        {
            spec = null;
            error = null;

            if (count == 0)
            {
                error = BindError.NoMatchBindError;
                return false;
            }

            if (count == 1)
            {
                spec = specs[0];
                var d = spec!.AssignArgumentsEx(ref prov);
                return true;
            }

            uint ambigCount = 0;
            MethodBase?[] ambigMethods = new MethodBase?[count];
            uint closest = uint.MaxValue;
            for (int sidx = 0; sidx < count; sidx++)
            {
                BindSpec mspec = specs[sidx]!;

                uint distance = mspec!.AssignArgumentsEx(ref prov);

                // NOTE:
                // if method has the exact same distance,
                // lets look at a few other properties to determine which
                // method should be used. if can not automatically resolve
                // the ambiguity, it will return an error with a list of
                // ambiguous matches for the given args and kwargs.
                if (distance == closest
                        && distance != uint.MaxValue)
                {
                    // NOTE:
                    // if there is a redirected method with same distance,
                    // lets use that. redirected methods should be in order
                    // of redirection e.g. origial->redirected->redirected
                    if (BindSpec.IsRedirected(mspec!.Method))
                    {
                        ambigCount = 0;
                        spec = mspec;
                        continue;
                    }

                    // NOTE:
                    // if method expands and other method does not, choose
                    // the non-expanding one as it is more specific
                    if (mspec!.Expands != spec!.Expands)
                    {
                        ambigCount = 0;
                        spec = mspec!.Expands ? spec : mspec!;
                        continue;
                    }

                    if (prov.GivenArgs == 0)
                    {
                        goto ambiguousError;
                    }

                    // NOTE:
                    // if method has the same distance, lets use the one
                    // with the least amount of optional parameters.
                    // e.g. between .Foo(int) and .Foo(int, float=0)
                    // we would pick the former since it is shorter.
                    // we do not compute the optional parameters that do not
                    // have a matching input arguments in the distance.
                    // otherwise for example .Foo(double) might end up closer
                    // than .Foo(float, float=0) when passing a float input,
                    // since including optional float might push it further
                    // away from computed distance for .Foo(double), depending
                    // on the computed distance for the actual types.
                    uint mspecOpts = mspec!.Required + mspec!.Optional;
                    uint specOpts = spec!.Required + spec!.Optional;
                    if (mspecOpts < specOpts)
                    {
                        ambigCount = 0;
                        spec = mspec;
                        continue;
                    }
                    else if (mspecOpts > specOpts)
                    {
                        continue;
                    }

                    // NOTE:
                    // if method has the same distance, lets use the one
                    // with the least amount of 'out' parameters.
                    if (mspec!.Returns < spec!.Returns)
                    {
                        ambigCount = 0;
                        spec = mspec;
                        continue;
                    }
                    else if (mspec!.Returns > spec!.Returns)
                    {
                        continue;
                    }

                    // NOTE:
                    // if method has the same distance, and have the least
                    // optional parameters, and least amount of 'out' params,
                    // lets look at distance computed for each parameter and
                    // choose method which starts with closer params.
                    // dotnet compiler detects this as ambiguous but since
                    // python is dynamically typed, we can make an selection
                    // in certain conditions and avoid the error.
                    // e.g. when passing (short, int) or (int, short)
                    // compiler will complain about ambiguous call between:
                    // .Foo(short, short)
                    // .Foo(int, int)
                    // but we can choose the method with closer parameters
                    // in order and therefore call:
                    // .Foo(short, short) for (short, int) args
                    // .Foo(int, int) for (int, short) args
                    BindParam[] sparams = spec.Parameters;
                    BindParam[] msparams = mspec!.Parameters;
                    if (sparams.Length == msparams.Length)
                    {
                        for (int pidx = 0; pidx < sparams.Length; pidx++)
                        {
                            BindParam sp = sparams[pidx];
                            BindParam mp = msparams[pidx];

                            if (mp.Distance == sp.Distance)
                            {
                                continue;
                            }
                            else if (mp.Distance > sp.Distance)
                            {
                                break;
                            }
                            else
                            {
                                ambigCount = 0;
                                spec = mspec;
                                break;
                            }
                        }

                        continue;
                    }

                // NOTE:
                // if we get here, two methods have the same distance,
                // and we were not able to resolve the ambiguity and choose
                // one of the two. so lets add this to the list of
                // ambiguous methods and increate the count.
                // if a closer match is not later found, this method will
                // return an ambiguous call error with these methods.
                ambiguousError:
                    if (ambigCount == 0)
                    {
                        ambigMethods[0] = spec.Method;
                        ambigCount++;
                    }

                    ambigMethods[ambigCount] = mspec!.Method;
                    ambigCount++;
                }
                else if (distance < closest)
                {
                    ambigCount = 0;
                    closest = distance;
                    spec = mspec;
                }
            }

            if (ambigCount > 0)
            {
                MethodBase[] ambigs = new MethodBase[(int)ambigCount];
                Array.Copy(ambigMethods, ambigs, ambigCount);

                error = new AmbiguousBindError(ambigs);
                return false;
            }

            if (spec is null)
            {
                error = new NoMatchBindError();
                return false;
            }

            return true;
        }

        // horizontal filter
        static bool TryBindByCount(MethodBase method,
                                   ref ArgProvider prov,
                                   out BindSpec? spec)
        {
            spec = default;

            ParameterInfo[] mparams = method.GetParameters();

            BindParam[] argSpecs;
            uint required = 0;
            uint optional = 0;
            uint returns = 0;
            bool expands = false;

            if (OperatorMethod.IsOperatorMethod(method) is bool isOperator
                    && isOperator)
            {
                bool isReverse = isOperator
                              && OperatorMethod.IsReverse(method);

                if (isReverse && OperatorMethod.IsComparisonOp(method))
                {
                    return false;
                }

                // binary operators
                if (mparams.Length == 2)
                {
                    required = 1;

                    if (isReverse)
                    {
                        argSpecs = new BindParam[]
                        {
                            new BindParam(mparams[0], BindParamKind.Default),
                            new BindParam(mparams[1], BindParamKind.Self),
                        };
                    }
                    else
                    {
                        argSpecs = new BindParam[]
                        {
                            new BindParam(mparams[0], BindParamKind.Self),
                            new BindParam(mparams[1], BindParamKind.Default),
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
                uint kwargCount = prov.KWargsCount;
                uint length = (uint)mparams.Length;
                uint last = length - 1;

                // check if last parameter is a params array.
                // this means method can accept parameters
                // beyond what is required.
                expands =
                    Attribute.IsDefined(mparams[last],
                                        typeof(ParamArrayAttribute));

                argSpecs = new BindParam[length];

                for (uint i = 0; i < length; i++)
                {
                    ParameterInfo param = mparams[i];

                    if (prov.Keys.Contains(param.Name))
                    {
                        kwargCount--;
                    }

                    // `.IsOut` is false for `ref` parameters
                    // NOTE:
                    // some out parameters might specify `[In]` attribute.
                    // lets make sure we count them as required.
                    // e.g. Stream.Read([In][Out] char[] ...
                    if (param.IsOut
                            && param.GetCustomAttribute<InAttribute>() is null)
                    {
                        argSpecs[i] =
                            new BindParam(param, BindParamKind.Return);

                        returns++;
                    }
                    else if (param.ParameterType.IsByRef)
                    {
                        argSpecs[i] =
                            new BindParam(param, BindParamKind.Return);

                        returns++;
                        required++;
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
                            new BindParam(param, BindParamKind.Default);

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
                if (required > prov.GivenArgs)
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
            if (prov.GivenArgs == required)
            {
                goto matched;
            }

            else if (required < prov.GivenArgs
                        && (prov.GivenArgs <= (required + optional + returns)
                                || expands))
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
            spec = new BindSpec(method,
                                ref argSpecs,
                                required,
                                optional,
                                returns,
                                expands);
            return true;
        }

        #region Distance Compute Logic
        static readonly Dictionary<int, uint> s_primDistMap =
            new()
            {
                [ComputeKey(TypeCode.UInt64, TypeCode.UInt64)] = 0,
                [ComputeKey(TypeCode.UInt64, TypeCode.Int64)] = 1,
                [ComputeKey(TypeCode.UInt64, TypeCode.UInt32)] = 2,
                [ComputeKey(TypeCode.UInt64, TypeCode.Int32)] = 3,
                [ComputeKey(TypeCode.UInt64, TypeCode.UInt16)] = 4,
                [ComputeKey(TypeCode.UInt64, TypeCode.Int16)] = 5,
                [ComputeKey(TypeCode.UInt64, TypeCode.Double)] = 6,
                [ComputeKey(TypeCode.UInt64, TypeCode.Single)] = 7,
                [ComputeKey(TypeCode.UInt64, TypeCode.Decimal)] = 7,
                [ComputeKey(TypeCode.UInt64, TypeCode.Object)] = uint.MaxValue - 1,
                [ComputeKey(TypeCode.UInt64, TypeCode.DateTime)] = uint.MaxValue,

                [ComputeKey(TypeCode.UInt32, TypeCode.UInt32)] = 0,
                [ComputeKey(TypeCode.UInt32, TypeCode.Int32)] = 1,
                [ComputeKey(TypeCode.UInt32, TypeCode.UInt16)] = 2,
                [ComputeKey(TypeCode.UInt32, TypeCode.Int16)] = 3,

                [ComputeKey(TypeCode.UInt16, TypeCode.UInt16)] = 0,
                [ComputeKey(TypeCode.UInt16, TypeCode.Int16)] = 1,

                [ComputeKey(TypeCode.Int64, TypeCode.Int64)] = 0,
                [ComputeKey(TypeCode.Int32, TypeCode.Int32)] = 0,
                [ComputeKey(TypeCode.Int16, TypeCode.Int16)] = 0,
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
        static readonly uint ARG_MAX_DIST = ARG_GROUP_SIZE;
        static readonly uint TYPE_GROUP_SIZE = ARG_GROUP_SIZE / 4;
        static readonly uint TYPE_MAX_DIST = TYPE_GROUP_SIZE;
        static readonly uint MATCH_GROUP_SIZE = TYPE_GROUP_SIZE / 4;
        static readonly uint MATCH_MAX_DIST = MATCH_GROUP_SIZE;

        // NOTE:
        // this method computes a distance between the given python arg
        // and the expected type iin target parameter slot.
        // However in many cases when given arg is a python object,
        // the final clr type of arg is unknown. therefore we return the
        // max distance for these and let the arg converter attempt
        // to convert the type to the expected type later.
        static uint GetDistance(ref ArgProvider prov,
                                BorrowedReference arg, BindParam param)
        {
            Type toType = param.Type;

            if (param.Kind == BindParamKind.Params
                    && Runtime.PySequence_Check(arg))
            {
                uint argsCount = (uint)Runtime.PySequence_Size(arg);
                if (argsCount > 0)
                {
                    using var iterObj = Runtime.PyObject_GetIter(arg);
                    using var item = Runtime.PyIter_Next(iterObj.Borrow());
                    if (!item.IsNull()
                            && prov.GetCLRType(item.Borrow()) is Type argType)
                    {
                        return GetDistance(arg, argType, toType);
                    }
                }
            }

            else if (prov.GetCLRType(arg) is Type argType)
            {
                return GetDistance(arg, argType, toType);
            }

            else if (arg == null
                        || Runtime.None == arg
                        || toType == typeof(object)
                        || toType == typeof(PyObject))
            {
                return 0;
            }

            return ARG_MAX_DIST;
        }

        // 0 <= x < ARG_MAX_DIST
        static uint GetDistance(BorrowedReference arg, Type from, Type to)
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
                distance = ARG_MAX_DIST;
                goto computed;
            }

            if ((from.IsArray && to.IsArray)
                    && (from.GetElementType() != to.GetElementType()))
            {
                distance = ARG_MAX_DIST;
                goto computed;
            }

            // derived match
            distance += MATCH_GROUP_SIZE;
            if (TryGetDerivedTypeDistance(from, to, out uint derivedDistance))
            {
                distance += derivedDistance;
                goto computed;
            }

            // generic match
            distance += MATCH_GROUP_SIZE;
            if (to.IsGenericParameter)
            {
                goto computed;
            }

            // convert/cast match
            distance += MATCH_GROUP_SIZE;
            if (TryGetCastTypeDistance(from, to, out uint castDistance))
            {
                distance += castDistance;
            }
            else if (TryGetManagedValue(arg, to, out object? _, setError: false))
            {
                distance += MATCH_MAX_DIST - 1;
            }
            else
            {
                distance += MATCH_MAX_DIST;
            }

        computed:
#if METHODBINDER_SOLVER_NEW_CACHE_DIST
            s_distMap[key] = distance;
#endif
            return distance;
        }

        // zero when types are equal.
        // assumes derived is assignable to @base
        // 0 <= x < MATCH_MAX_DIST
        static bool TryGetDerivedTypeDistance(Type from, Type to, out uint distance)
        {
            distance = default;

            if (!to.IsAssignableFrom(from))
            {
                return false;
            }

            uint depth = 0;

            if (from.IsInterface)
            {
                // assumes to.IsInterface==true
                // since we have checked assignability before
                Type[] interfaces = from.GetInterfaces();
                for (int i = 0; i < interfaces.Length; i++)
                {
                    depth++;
                    if (interfaces[i] == to)
                    {
                        break;
                    }
                }

                distance = depth;
                return true;
            }

            Type t = from;
            while (t != null
                    && t != to
                    && depth < MATCH_MAX_DIST)
            {
                depth++;
                t = t.BaseType;
            }

            distance = depth;
            return true;
        }

        // zero when types are equal.
        // 0 <= x < MATCH_MAX_DIST
        static bool TryGetCastTypeDistance(Type from, Type to, out uint distance)
        {
            distance = default;

            TypeCode fromCode = Type.GetTypeCode(from);
            TypeCode toCode = Type.GetTypeCode(to);

            if (fromCode > TypeCode.DBNull
                    || toCode > TypeCode.DBNull)
            {
                int key = ComputeKey(fromCode, toCode);

                if (s_primDistMap.TryGetValue(key, out distance))
                {
                    return true;
                }

                if (TryGetPrecedence(from, out uint fromPrec)
                        && TryGetPrecedence(to, out uint toPrec))
                {
                    distance = (uint)Math.Abs((int)toPrec - (int)fromPrec);
                    return true;
                }
            }

            return false;
        }

        static bool TryGetPrecedence(Type of, out uint predecence)
        {
            predecence = 0;

            if (of is null)
            {
                return false;
            }

            if (!of.IsPrimitive)
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
                    return false;

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

        static int ComputeKey(TypeCode from, TypeCode to)
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 23 + (int)from;
                hash = hash * 23 + (int)to;
                return hash;
            }
        }
        #endregion
    }
}