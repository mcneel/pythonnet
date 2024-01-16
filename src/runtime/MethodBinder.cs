#define METHODBINDER_SOLVER_NEW
using System;
using System.Text;
using System.Linq;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;

namespace Python.Runtime
{
    using MaybeMethodBase = MaybeMethodBase<MethodBase>;

    /// <summary>
    /// A MethodBinder encapsulates information about a (possibly overloaded)
    /// managed method, and is responsible for selecting the right method given
    /// a set of Python arguments. This is also used as a base class for the
    /// ConstructorBinder, a minor variation used to invoke constructors.
    /// </summary>
    [Serializable]
    internal partial class MethodBinder
    {
        /// <summary>
        /// Context handler to provide invoke options to MethodBinder
        /// </summary>
        public sealed class InvokeContext : IDisposable
        {
            readonly MethodBinder _binder;
            readonly bool _original_allow_redirected;

            public InvokeContext(MethodBinder binder)
            {
                _binder = binder;
                _original_allow_redirected = _binder.allow_redirected;
            }

            public bool AllowRedirected
            {
                get => _binder.allow_redirected;
                set => _binder.allow_redirected = value;
            }

            public void Dispose()
            {
                _binder.allow_redirected = _original_allow_redirected;
            }
        }

        /// <summary>
        /// Utility class to sort method info by parameter type precedence.
        /// </summary>
        readonly struct Sorter : IComparer<MaybeMethodBase>
        {
            int IComparer<MaybeMethodBase>.Compare(MaybeMethodBase m1, MaybeMethodBase m2)
            {
                MethodBase me1 = m1.UnsafeValue;
                MethodBase me2 = m2.UnsafeValue;
                if (me1 == null && me2 == null)
                {
                    return 0;
                }
                else if (me1 == null)
                {
                    return -1;
                }
                else if (me2 == null)
                {
                    return 1;
                }

                if (me1.DeclaringType != me2.DeclaringType)
                {
                    // m2's type derives from m1's type, favor m2
                    if (me1.DeclaringType.IsAssignableFrom(me2.DeclaringType))
                        return 1;

                    // m1's type derives from m2's type, favor m1
                    if (me2.DeclaringType.IsAssignableFrom(me1.DeclaringType))
                        return -1;
                }

                int p1 = GetPrecedence(me1);
                int p2 = GetPrecedence(me2);
                if (p1 < p2)
                {
                    return -1;
                }
                if (p1 > p2)
                {
                    return 1;
                }
                return 0;
            }

            /// <summary>
            /// Precedence algorithm largely lifted from Jython - the concerns are
            /// generally the same so we'll start with this and tweak as necessary.
            /// </summary>
            /// <remarks>
            /// Based from Jython `org.python.core.ReflectedArgs.precedence`
            /// See: https://github.com/jythontools/jython/blob/master/src/org/python/core/ReflectedArgs.java#L192
            /// </remarks>
            static int GetPrecedence(MethodBase mi)
            {
                if (mi == null)
                {
                    return int.MaxValue;
                }

                ParameterInfo[] pi = mi.GetParameters();
                int val = mi.IsStatic ? 3000 : 0;
                int num = pi.Length;

                val += mi.IsGenericMethod ? 1 : 0;
                for (var i = 0; i < num; i++)
                {
                    val += ArgumentPrecedence(pi[i].ParameterType);
                }

                // NOTE:
                // Ensure Original methods (e.g. _BASEVIRTUAL_get_Item()) are
                // sorted after their Redirected counterpart. This makes sure when
                // allowRedirected is false and the Redirected method is skipped
                // in the Bind() loop, the Original method is right after and can
                // match the method specs and create a bind
                if (ClassDerivedObject.IsMethod<OriginalMethod>(mi))
                    val += 1;

                return val;
            }

            /// <summary>
            /// Return a precedence value for a particular Type object.
            /// </summary>
            static int ArgumentPrecedence(Type t)
            {
                Type objectType = typeof(object);
                if (t == objectType)
                {
                    return 3000;
                }

                if (t.IsArray)
                {
                    Type e = t.GetElementType();
                    if (e == objectType)
                    {
                        return 2500;
                    }
                    return 100 + ArgumentPrecedence(e);
                }

                TypeCode tc = Type.GetTypeCode(t);

                // TODO: Clean up
                return tc switch
                {
                    TypeCode.Object => 1,
                    TypeCode.UInt64 => 10,
                    TypeCode.UInt32 => 11,
                    TypeCode.UInt16 => 12,
                    TypeCode.Int64 => 13,
                    TypeCode.Int32 => 14,
                    TypeCode.Int16 => 15,
                    TypeCode.Char => 16,
                    TypeCode.SByte => 17,
                    TypeCode.Byte => 18,
                    TypeCode.Single => 20,
                    TypeCode.Double => 21,
                    TypeCode.String => 30,
                    TypeCode.Boolean => 40,
                    _ => 2000,
                };
            }
        }

        /// <summary>
        /// A Binding is a utility instance that bundles together a MethodInfo
        /// representing a method to call, a (possibly null) target instance for
        /// the call, and the arguments for the call (all as managed values).
        /// </summary>
        private sealed class Binding
        {
            public MethodBase info;
            public object?[] args;
            public object? inst;
            public int outs;

            public Binding(MethodBase info, object? inst, object?[] args, int outs)
            {
                this.info = info;
                this.inst = inst;
                this.args = args;
                this.outs = outs;
            }
        }

        /// <summary>
        /// The overloads of this method
        /// </summary>
        [NonSerialized] MethodBase[]? methods = default;
        [NonSerialized] bool methodsInit = false;
        readonly List<MaybeMethodBase> list;

        const bool DefaultAllowThreads = true;
        bool allow_redirected = true;

        public bool allow_threads = DefaultAllowThreads;

        public int Count => list.Count;

        internal MethodBinder()
        {
            list = new List<MaybeMethodBase>();
        }

        internal MethodBinder(MethodInfo mi)
        {
            list = new List<MaybeMethodBase> { new MaybeMethodBase(mi) };
        }

        internal void AddMethod(MethodBase m)
        {
            list.Add(m);
            methodsInit = false;
        }

        /// <summary>
        /// Return the array of MethodInfo for this method. The result array
        /// is arranged in order of precedence (done lazily to avoid doing it
        /// at all for methods that are never called).
        /// </summary>
        internal MethodBase[] GetMethods()
        {
            if (!methodsInit)
            {
                // I'm sure this could be made more efficient.
                list.Sort(new Sorter());
                methods = (from method in list where method.Valid select method.Value).ToArray();
                methodsInit = true;
            }

            return methods!;
        }

        /// <summary>
        /// Given a sequence of MethodInfo and a sequence of types, return the
        /// MethodInfo that matches the signature represented by those types.
        /// </summary>
        internal static MethodBase? MatchSignature(MethodBase[] mi, Type[] tp)
        {
            if (tp == null)
            {
                return null;
            }
            int count = tp.Length;
            foreach (MethodBase t in mi)
            {
                ParameterInfo[] pi = t.GetParameters();
                if (pi.Length != count)
                {
                    continue;
                }
                for (var n = 0; n < pi.Length; n++)
                {
                    if (tp[n] != pi[n].ParameterType)
                    {
                        break;
                    }
                    if (n == pi.Length - 1)
                    {
                        return t;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Given a sequence of MethodInfo and a sequence of type parameters,
        /// return the MethodInfo(s) that represents the matching closed generic.
        /// If unsuccessful, returns null and may set a Python error.
        /// </summary>
        internal static MethodInfo[] MatchParameters(MethodBase[] mi, Type[]? tp)
        {
            if (tp == null)
            {
                return Array.Empty<MethodInfo>();
            }
            int count = tp.Length;
            var result = new List<MethodInfo>();
            foreach (var t in mi)
            {
                if (!t.IsGenericMethodDefinition)
                {
                    continue;
                }
                Type[] args = t.GetGenericArguments();
                if (args.Length != count)
                {
                    continue;
                }

                try
                {
                    // MakeGenericMethod can throw ArgumentException if
                    // the type parameters do not obey the constraints.
                    if (t is MethodInfo minfo)
                    {
                        MethodInfo method = minfo.MakeGenericMethod(tp);
                        result.Add(method);
                    }
                }
                catch (ArgumentException)
                {
                    // The error will remain set until cleared by a successful match.
                }
            }
            return result.ToArray();
        }

        /// <summary>
        /// Given a sequence of MethodInfo and two sequences of type parameters,
        /// return the MethodInfo that matches the signature and the closed generic.
        /// </summary>
        internal static MethodInfo? MatchSignatureAndParameters(MethodBase[] mi, Type[] genericTp, Type[] sigTp)
        {
            if (genericTp == null || sigTp == null)
            {
                return null;
            }
            int genericCount = genericTp.Length;
            int signatureCount = sigTp.Length;
            foreach (var t in mi)
            {
                if (!t.IsGenericMethodDefinition)
                {
                    continue;
                }
                Type[] genericArgs = t.GetGenericArguments();
                if (genericArgs.Length != genericCount)
                {
                    continue;
                }
                ParameterInfo[] pi = t.GetParameters();
                if (pi.Length != signatureCount)
                {
                    continue;
                }
                for (var n = 0; n < pi.Length; n++)
                {
                    if (sigTp[n] != pi[n].ParameterType)
                    {
                        break;
                    }
                    if (n == pi.Length - 1 && t is MethodInfo match)
                    {
                        if (match.IsGenericMethodDefinition)
                        {
                            // FIXME: typeArgs not used
                            Type[] typeArgs = match.GetGenericArguments();
                            return match.MakeGenericMethod(genericTp);
                        }
                        return match;
                    }
                }
            }
            return null;
        }



        static readonly Type s_voidType = typeof(void);

        static bool TryGetManagedValue(BorrowedReference op, out object? value)
        {
            value = default;

            Type? clrType = GetCLRType(op);
            if (clrType == null)
            {
                return false;
            }

            return Converter.ToManaged(op, clrType, out value, true);
        }

        static bool TryGetManagedValue(BorrowedReference op, Type type, out object? value)
        {
            value = default;

            Type? clrType = GetCLRType(op, type);
            if (clrType == null)
            {
                return false;
            }

            return Converter.ToManaged(op, clrType, out value, true);
        }

        static Type? GetCLRType(BorrowedReference op)
        {
            ManagedType? mt = ManagedType.GetManagedObject(op);

            if (mt is ClassBase b)
            {
                MaybeType _type = b.type;
                return _type.Valid ? _type.Value : null;
            }
            else if (mt is CLRObject ob)
            {
                object inst = ob.inst;
                if (inst is Type ty)
                {
                    return ty;
                }
                else
                {
                    return inst?.GetType() ?? typeof(object);
                }
            }
            else
            {
                if (Runtime.PyType_Check(op))
                {
                    return Converter.GetTypeByAlias(op);
                }

                return Converter.GetTypeByAlias(Runtime.PyObject_TYPE(op), op);
            }
        }

        static Type? GetCLRType(BorrowedReference op, Type expected)
        {
            if (expected == typeof(object))
            {
                return expected;
            }

            BorrowedReference pyType = Runtime.PyObject_TYPE(op);
            BorrowedReference pyBuiltinType = Converter.GetPythonTypeByAlias(expected);

            if (pyType != null
                    && pyBuiltinType == pyType)
            {
                return expected;
            }

            TypeCode typeCode = Type.GetTypeCode(expected);
            if (TypeCode.Empty != typeCode)
            {
                return expected;
            }

            return null;
        }

        static bool IsOperatorMethod(MethodBase method) => OperatorMethod.IsOperatorMethod(method);

        static bool IsComparisonOp(MethodBase method) => OperatorMethod.IsComparisonOp(method);

        static bool IsReverse(MethodBase method) => OperatorMethod.IsReverse(method);

#if METHODBINDER_SOLVER_NEW
        internal NewReference Invoke(BorrowedReference inst,
                                     BorrowedReference args,
                                     BorrowedReference kwargs,
                                     MethodBase? targetMethod)
        {
            if (targetMethod is null)
            {
                return Invoke(inst, args, kwargs);
            }
            else
            {
                MethodBase[] methods = new MethodBase[] { targetMethod };
                return Invoke(inst, args, kwargs, methods);
            }
        }

        internal NewReference Invoke(BorrowedReference inst,
                                     BorrowedReference args,
                                     BorrowedReference kwargs)
        {
            MethodBase[] methods = (from m in list where m.Valid select m.Value).ToArray();
            return Invoke(inst, args, kwargs, methods);
        }

        NewReference Invoke(BorrowedReference inst,
                            BorrowedReference args,
                            BorrowedReference kwargs,
                            MethodBase[] methods)
        {
            if (methods.Length == 0)
            {
                var msg = new StringBuilder("The underlying C# method(s) have been deleted");
                if (list.Count > 0
                        && list[0] is MaybeMethodBase mmb
                        && mmb.Name != null)
                {
                    msg.Append($": {mmb}");
                }

                return Exceptions.RaiseTypeError(msg.ToString());
            }

            if (TryBind(methods, args, kwargs, allow_redirected, out BindSpec? spec, out BindError? error))
            {
                MethodBase match = spec!.Method;
                BindParam[] bindParams = spec.Parameters;

                // NOTE: requiring event handlers to be instances of System.Delegate
                // Currently if event handlers accept any callable object, the dynamically generated
                // Delegate for the callable is not going to have the same hash when being passed
                // to both add_ and remove_ event property methods. Therefore once event handler is
                // added, it can not be removed since the generated delegate on the remove_ method
                // is not identical to the first.
                // Here, we ensure given argument to add/remove special method is a Delegate
                if (match.IsSpecialName &&
                        (match.Name.StartsWith("add_") || match.Name.StartsWith("remove_")))
                {
                    if (Runtime.PyTuple_Size(args) != 1)
                    {
                        throw new Exception("Event handler methods only accept one Delegate argument");
                    }

                    // if argument type is not a CLR Delegate, throw an exception
                    BorrowedReference darg = Runtime.PyTuple_GetItem(args, 0);
                    if (ManagedType.GetManagedObject(darg) is not CLRObject clrObj
                            || !typeof(System.Delegate).IsAssignableFrom(clrObj.inst.GetType()))
                    {
                        return Exceptions.RaiseTypeError("event handler must be a System.Delegate");
                    }
                }

                IntPtr threadState = IntPtr.Zero;
                Type resultType = match.IsConstructor ? s_voidType : ((MethodInfo)match).ReturnType;
                bool isVoid = resultType == s_voidType;

                object? instance = (ManagedType.GetManagedObject(inst) as CLRObject)?.inst;
                object? result = default;

                if (!match.IsStatic && instance is null)
                {
                    return Exceptions.RaiseTypeError("Invoked a non-static method with an invalid instance");
                }

                // NOTE:
                // arg conversion needs to happen before GIL is possibly released
                bool converted = spec.TryGetArguments(instance, out MethodBase method, out object?[] bindArgs);
                if (!converted)
                {
                    Exception convertEx = PythonException.FetchCurrent();
                    return Exceptions.RaiseTypeError($"{convertEx.Message} in method {match}");
                }

                if (allow_threads)
                {
                    threadState = PythonEngine.BeginAllowThreads();
                }

                try
                {
                    result =
                        method.Invoke(instance,
                                      invokeAttr: BindingFlags.Default,
                                      binder: null,
                                      parameters: bindArgs,
                                      culture: null);
                }
                catch (Exception e)
                {
                    if (e.InnerException != null)
                    {
                        e = e.InnerException;
                    }

                    if (allow_threads)
                    {
                        PythonEngine.EndAllowThreads(threadState);
                    }

                    Exceptions.SetError(e);
                    return default;
                }

                if (allow_threads)
                {
                    PythonEngine.EndAllowThreads(threadState);
                }

                // If there are out parameters, we return a tuple containing
                // the result (if not void), followed by the out parameters.
                // If there is only one out parameter and the return type of
                // the method is void, we return the out parameter as the result
                // to Python (for code compatibility with ironpython).
                int returnParams = spec.Parameters.Where(p => p.Kind == BindParamKind.Return).Count();
                if (returnParams > 0)
                {
                    var tupleIndex = 0;
                    int tupleSize = returnParams + (isVoid ? 0 : 1);
                    using var tuple = Runtime.PyTuple_New(tupleSize);

                    if (!isVoid)
                    {
                        using var v = Converter.ToPython(result, resultType);
                        Runtime.PyTuple_SetItem(tuple.Borrow(), tupleIndex, v.Steal());
                        tupleIndex++;
                    }

                    for (int i = 0; i < spec.Parameters.Length; i++)
                    {
                        BindParam param = spec.Parameters[i];

                        if (param.Kind != BindParamKind.Return)
                        {
                            continue;
                        }

                        object? value = bindArgs[i];
                        using var v = Converter.ToPython(value, param.Type.GetElementType());
                        Runtime.PyTuple_SetItem(tuple.Borrow(), tupleIndex, v.Steal());
                        tupleIndex++;
                    }

                    if (tupleSize == 1)
                    {
                        BorrowedReference item = Runtime.PyTuple_GetItem(tuple.Borrow(), 0);
                        return new NewReference(item);
                    }
                    else
                    {
                        return new NewReference(tuple.Borrow());
                    }
                }

                return Converter.ToPython(result, resultType);
            }

            else if (error is AmbiguousBindError ambigErr)
            {
                // https://learn.microsoft.com/en-us/dotnet/csharp/misc/cs0121
                var msg = new StringBuilder("The call is ambiguous between the following methods or properties: \n");
                for (int i = 0; i < ambigErr.Methods.Length; i++)
                {
                    MethodBase m = ambigErr.Methods[i];

                    // build method signature
                    // e.g MethodTest.DefaultParamsWithOverloading(Int32, Int32)
                    msg.Append('\'');
                    msg.Append(m.DeclaringType.Name);
                    msg.Append('.');
                    // take method signature without return type
                    string msig = m.ToString();
                    msig = msig.Substring(msig.IndexOf(' ') + 1);
                    msg.Append(msig);
                    msg.Append('\'');
                    if (ambigErr.Methods.Length - (i + 1) > 0)
                    {
                        msg.Append(" and ");
                        msg.AppendLine();
                    }
                }

                //Runtime.PyErr_Fetch(out var errType, out var errVal, out var errTrace);
                //Runtime.PyErr_Restore(errType.StealNullable(), errVal.StealNullable(), errTrace.StealNullable());
                return Exceptions.RaiseTypeError(msg.ToString());
            }

            else
            {
                // https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/compiler-messages/cs1501
                MethodBase m = methods[0];
                var msg = new StringBuilder("No overload for method '");
                msg.Append(m.DeclaringType.Name);
                msg.Append('.');
                msg.Append(m.Name);
                msg.Append("{}' takes '");
                msg.Append(args == null ? 0 : (int)Runtime.PyTuple_Size(args));
                msg.Append("' arguments");

                Runtime.PyErr_Fetch(out var errType, out var errVal, out var errTrace);
                AppendArgumentTypes(msg: msg, args);
                Runtime.PyErr_Restore(errType.StealNullable(), errVal.StealNullable(), errTrace.StealNullable());
                return Exceptions.RaiseTypeError(msg.ToString());
            }
        }
#endif

        /// <summary>
        /// Try to convert a Python argument object to a managed CLR type.
        /// If unsuccessful, may set a Python error.
        /// </summary>
        /// <param name="op">Pointer to the Python argument object.</param>
        /// <param name="parameterType">That parameter's managed type.</param>
        /// <param name="arg">Converted argument.</param>
        /// <param name="isOut">Whether the CLR type is passed by reference.</param>
        /// <returns>true on success</returns>
        static bool TryConvertArgument(BorrowedReference op, Type parameterType,
                                       out object? arg, out bool isOut)
        {
            arg = null;
            isOut = false;
            var clrtype = TryComputeClrArgumentType(parameterType, op);
            if (clrtype == null)
            {
                return false;
            }

            if (!Converter.ToManaged(op, clrtype, out arg, true))
            {
                return false;
            }

            isOut = clrtype.IsByRef;
            return true;
        }

        /// <summary>
        /// Determine the managed type that a Python argument object needs to be converted into.
        /// </summary>
        /// <param name="parameterType">The parameter's managed type.</param>
        /// <param name="argument">Pointer to the Python argument object.</param>
        /// <returns>null if conversion is not possible</returns>
        static Type? TryComputeClrArgumentType(Type parameterType, BorrowedReference argument)
        {
            // this logic below handles cases when multiple overloading methods
            // are ambiguous, hence comparison between Python and CLR types
            // is necessary
            Type? clrtype = null;

            if (clrtype != null)
            {
                if ((parameterType != typeof(object)) && (parameterType != clrtype))
                {
                    BorrowedReference pytype = Converter.GetPythonTypeByAlias(parameterType);
                    BorrowedReference pyoptype = Runtime.PyObject_TYPE(argument);
                    var typematch = false;
                    if (pyoptype != null)
                    {
                        if (pytype != pyoptype)
                        {
                            typematch = false;
                        }
                        else
                        {
                            typematch = true;
                            clrtype = parameterType;
                        }
                    }
                    if (!typematch)
                    {
                        // this takes care of enum values
                        TypeCode parameterTypeCode = Type.GetTypeCode(parameterType);
                        TypeCode clrTypeCode = Type.GetTypeCode(clrtype);
                        if (parameterTypeCode == clrTypeCode)
                        {
                            typematch = true;
                            clrtype = parameterType;
                        }
                        else
                        {
                            Exceptions.RaiseTypeError($"Expected {parameterTypeCode}, got {clrTypeCode}");
                        }
                    }
                    if (!typematch)
                    {
                        return null;
                    }
                }
                else
                {
                    clrtype = parameterType;
                }
            }
            else
            {
                clrtype = parameterType;
            }

            return clrtype;
        }

        static void AppendArgumentTypes(StringBuilder msg, BorrowedReference args)
        {
            Runtime.AssertNoErorSet();

            nint argCount = Runtime.PyTuple_Size(args);
            msg.Append('(');

            for (nint argIndex = 0; argIndex < argCount; argIndex++)
            {
                BorrowedReference arg = Runtime.PyTuple_GetItem(args, argIndex);
                if (arg != null)
                {
                    BorrowedReference type = Runtime.PyObject_TYPE(arg);
                    if (type != null)
                    {
                        using var description = Runtime.PyObject_Str(type);
                        if (description.IsNull())
                        {
                            Exceptions.Clear();
                            msg.Append(Util.BadStr);
                        }
                        else
                        {
                            msg.Append(Runtime.GetManagedString(description.Borrow()));
                        }
                    }
                }

                if (argIndex + 1 < argCount)
                    msg.Append(", ");
            }
            msg.Append(')');
        }
    }
}
