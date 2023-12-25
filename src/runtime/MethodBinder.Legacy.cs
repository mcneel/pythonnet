using System;
using System.Text;
using System.Linq;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;

#if !METHODBINDER_SOLVER_NEW
namespace Python.Runtime
{
    partial class MethodBinder
    {
        internal virtual NewReference Invoke(BorrowedReference inst, BorrowedReference args, BorrowedReference kw,
                                             MethodBase? info = null, MethodBase[]? methodinfo = null)
        {
            // No valid methods, nothing to bind.
            if (GetMethods().Length == 0)
            {
                var msg = new StringBuilder("The underlying C# method(s) have been deleted");
                if (list.Count > 0 && list[0].Name != null)
                {
                    msg.Append($": {list[0]}");
                }
                return Exceptions.RaiseTypeError(msg.ToString());
            }

            Binding? binding = Bind(inst, args, kw, info);
            object result;
            IntPtr ts = IntPtr.Zero;

            if (binding == null)
            {
                var value = new StringBuilder("No method matches given arguments");
                if (methodinfo != null && methodinfo.Length > 0)
                {
                    value.Append($" for {methodinfo[0].DeclaringType?.Name}.{methodinfo[0].Name}");
                }
                else if (list.Count > 0 && list[0].Valid)
                {
                    value.Append($" for {list[0].Value.DeclaringType?.Name}.{list[0].Value.Name}");
                }

                value.Append(": ");
                Runtime.PyErr_Fetch(out var errType, out var errVal, out var errTrace);
                AppendArgumentTypes(to: value, args);
                Runtime.PyErr_Restore(errType.StealNullable(), errVal.StealNullable(), errTrace.StealNullable());
                return Exceptions.RaiseTypeError(value.ToString());
            }

            // NOTE: requiring event handlers to be instances of System.Delegate
            // Currently if event handlers accept any callable object, the dynamically generated
            // Delegate for the callable is not going to have the same hash when being passed
            // to both add_ and remove_ event property methods. Therefore once event handler is
            // added, it can not be removed since the generated delegate on the remove_ method
            // is not identical to the first.
            // Here, we ensure given argument to add/remove special method is a Delegate
            if (binding.info.IsSpecialName &&
                    (binding.info.Name.StartsWith("add_") || binding.info.Name.StartsWith("remove_")))
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

            if (allow_threads)
            {
                ts = PythonEngine.BeginAllowThreads();
            }

            try
            {
                result = binding.info.Invoke(binding.inst, BindingFlags.Default, null, binding.args, null);
            }
            catch (Exception e)
            {
                if (e.InnerException != null)
                {
                    e = e.InnerException;
                }
                if (allow_threads)
                {
                    PythonEngine.EndAllowThreads(ts);
                }
                Exceptions.SetError(e);
                return default;
            }

            if (allow_threads)
            {
                PythonEngine.EndAllowThreads(ts);
            }

            // If there are out parameters, we return a tuple containing
            // the result, if any, followed by the out parameters. If there is only
            // one out parameter and the return type of the method is void,
            // we return the out parameter as the result to Python (for
            // code compatibility with ironpython).

            var returnType = binding.info.IsConstructor ? typeof(void) : ((MethodInfo)binding.info).ReturnType;

            if (binding.outs > 0)
            {
                ParameterInfo[] pi = binding.info.GetParameters();
                int c = pi.Length;
                var n = 0;

                bool isVoid = returnType == typeof(void);
                int tupleSize = binding.outs + (isVoid ? 0 : 1);
                using var t = Runtime.PyTuple_New(tupleSize);
                if (!isVoid)
                {
                    using var v = Converter.ToPython(result, returnType);
                    Runtime.PyTuple_SetItem(t.Borrow(), n, v.Steal());
                    n++;
                }

                for (var i = 0; i < c; i++)
                {
                    Type pt = pi[i].ParameterType;
                    if (pt.IsByRef)
                    {
                        using var v = Converter.ToPython(binding.args[i], pt.GetElementType());
                        Runtime.PyTuple_SetItem(t.Borrow(), n, v.Steal());
                        n++;
                    }
                }

                if (binding.outs == 1 && returnType == typeof(void))
                {
                    BorrowedReference item = Runtime.PyTuple_GetItem(t.Borrow(), 0);
                    return new NewReference(item);
                }

                return new NewReference(t.Borrow());
            }

            return Converter.ToPython(result, returnType);
        }

        private readonly struct MatchedMethod
        {
            public MatchedMethod(int kwargsMatched, int defaultsNeeded, object?[] margs, int outs, MethodBase mb)
            {
                KwargsMatched = kwargsMatched;
                DefaultsNeeded = defaultsNeeded;
                ManagedArgs = margs;
                Outs = outs;
                Method = mb;
            }

            public int KwargsMatched { get; }
            public int DefaultsNeeded { get; }
            public object?[] ManagedArgs { get; }
            public int Outs { get; }
            public MethodBase Method { get; }
        }

        private readonly struct MismatchedMethod
        {
            public MismatchedMethod(Exception exception, MethodBase mb)
            {
                Exception = exception;
                Method = mb;
            }

            public Exception Exception { get; }
            public MethodBase Method { get; }
        }


        /// <summary>
        /// Bind the given Python instance and arguments to a particular method
        /// overload in <see cref="list"/> and return a structure that contains the converted Python
        /// instance, converted arguments and the correct method to call.
        /// If unsuccessful, may set a Python error.
        /// </summary>
        /// <param name="inst">The Python target of the method invocation.</param>
        /// <param name="args">The Python arguments.</param>
        /// <param name="kw">The Python keyword arguments.</param>
        /// <param name="info">If not null, only bind to that method.</param>
        /// <returns>A Binding if successful.  Otherwise null.</returns>
        Binding? Bind(BorrowedReference inst, BorrowedReference args, BorrowedReference kw, MethodBase? info)
        {
            // loop to find match, return invoker w/ or w/o error
            var kwargDict = new Dictionary<string, PyObject>();
            if (kw != null)
            {
                nint pynkwargs = Runtime.PyDict_Size(kw);
                using var keylist = Runtime.PyDict_Keys(kw);
                using var valueList = Runtime.PyDict_Values(kw);
                for (int i = 0; i < pynkwargs; ++i)
                {
                    var keyStr = Runtime.GetManagedString(Runtime.PyList_GetItem(keylist.Borrow(), i));
                    BorrowedReference value = Runtime.PyList_GetItem(valueList.Borrow(), i);
                    kwargDict[keyStr!] = new PyObject(value);
                }
            }

            MethodBase[] _methods;
            if (info != null)
            {
                _methods = new MethodBase[1];
                _methods.SetValue(info, 0);
            }
            else
            {
                _methods = GetMethods();
            }

            return Bind(inst, args, kwargDict, _methods, matchGenerics: true, allowRedirected: allow_redirected);
        }

        static Binding? Bind(BorrowedReference inst, BorrowedReference args,
                             Dictionary<string, PyObject> kwargDict,
                             MethodBase[] methods,
                             bool matchGenerics,
                             bool allowRedirected)
        {
            var pynargs = (int)Runtime.PyTuple_Size(args);
            var isGeneric = false;

            var argMatchedMethods = new List<MatchedMethod>(methods.Length);
            var mismatchedMethods = new List<MismatchedMethod>();

            // TODO: Clean up
            foreach (MethodBase mi in methods)
            {
                if (ClassDerivedObject.IsMethod<RedirectedMethod>(mi) && !allowRedirected)
                {
                    continue;
                }

                if (mi.IsGenericMethod)
                {
                    isGeneric = true;
                }
                ParameterInfo[] pi = mi.GetParameters();
                bool isOperator = OperatorMethod.IsOperatorMethod(mi);
                // Binary operator methods will have 2 CLR args but only one Python arg
                // (unary operators will have 1 less each), since Python operator methods are bound.
                isOperator = isOperator && pynargs == pi.Length - 1;
                bool isReverse = isOperator && OperatorMethod.IsReverse((MethodInfo)mi);  // Only cast if isOperator.
                if (isReverse && OperatorMethod.IsComparisonOp((MethodInfo)mi))
                    continue;  // Comparison operators in Python have no reverse mode.
                if (!MatchesArgumentCount(pynargs, pi, kwargDict, out bool paramsArray, out ArrayList? defaultArgList, out int kwargsMatched, out int defaultsNeeded) && !isOperator)
                {
                    continue;
                }
                // Preprocessing pi to remove either the first or second argument.
                if (isOperator && !isReverse)
                {
                    // The first Python arg is the right operand, while the bound instance is the left.
                    // We need to skip the first (left operand) CLR argument.
                    pi = pi.Skip(1).ToArray();
                }
                else if (isOperator && isReverse)
                {
                    // The first Python arg is the left operand.
                    // We need to take the first CLR argument.
                    pi = pi.Take(1).ToArray();
                }
                var margs = TryConvertArguments(pi, paramsArray, args, pynargs, kwargDict, defaultArgList, outs: out int outs);
                if (margs == null)
                {
                    var mismatchCause = PythonException.FetchCurrent();
                    mismatchedMethods.Add(new MismatchedMethod(mismatchCause, mi));
                    continue;
                }
                if (isOperator)
                {
                    if (inst != null)
                    {
                        if (ManagedType.GetManagedObject(inst) is CLRObject co)
                        {
                            bool isUnary = pynargs == 0;
                            // Postprocessing to extend margs.
                            var margsTemp = isUnary ? new object?[1] : new object?[2];
                            // If reverse, the bound instance is the right operand.
                            int boundOperandIndex = isReverse ? 1 : 0;
                            // If reverse, the passed instance is the left operand.
                            int passedOperandIndex = isReverse ? 0 : 1;
                            margsTemp[boundOperandIndex] = co.inst;
                            if (!isUnary)
                            {
                                margsTemp[passedOperandIndex] = margs[0];
                            }
                            margs = margsTemp;
                        }
                        else continue;
                    }
                }


                var matchedMethod = new MatchedMethod(kwargsMatched, defaultsNeeded, margs, outs, mi);
                argMatchedMethods.Add(matchedMethod);
            }
            if (argMatchedMethods.Count > 0)
            {
                var bestKwargMatchCount = argMatchedMethods.Max(x => x.KwargsMatched);
                var fewestDefaultsRequired = argMatchedMethods.Where(x => x.KwargsMatched == bestKwargMatchCount).Min(x => x.DefaultsNeeded);

                int bestCount = 0;
                int bestMatchIndex = -1;

                for (int index = 0; index < argMatchedMethods.Count; index++)
                {
                    var testMatch = argMatchedMethods[index];
                    if (testMatch.DefaultsNeeded == fewestDefaultsRequired && testMatch.KwargsMatched == bestKwargMatchCount)
                    {
                        bestCount++;
                        if (bestMatchIndex == -1)
                            bestMatchIndex = index;
                    }
                }

                if (bestCount > 1 && fewestDefaultsRequired > 0)
                {
                    // Best effort for determining method to match on gives multiple possible
                    // matches and we need at least one default argument - bail from this point
                    var stringBuilder = new StringBuilder("Not enough arguments provided to disambiguate the method.  Found:");
                    foreach (var matchedMethod in argMatchedMethods)
                    {
                        stringBuilder.AppendLine();
                        stringBuilder.Append(matchedMethod.Method.ToString());
                    }
                    Exceptions.SetError(Exceptions.TypeError, stringBuilder.ToString());
                    return null;
                }

                // If we're here either:
                //      (a) There is only one best match
                //      (b) There are multiple best matches but none of them require
                //          default arguments
                // in the case of (a) we're done by default. For (b) regardless of which
                // method we choose, all arguments are specified _and_ can be converted
                // from python to C# so picking any will suffice
                MatchedMethod bestMatch = argMatchedMethods[bestMatchIndex];
                var margs = bestMatch.ManagedArgs;
                var outs = bestMatch.Outs;
                var mi = bestMatch.Method;

                object? target = null;
                if (!mi.IsStatic && inst != null)
                {
                    //CLRObject co = (CLRObject)ManagedType.GetManagedObject(inst);
                    // InvalidCastException: Unable to cast object of type
                    // 'Python.Runtime.ClassObject' to type 'Python.Runtime.CLRObject'

                    // Sanity check: this ensures a graceful exit if someone does
                    // something intentionally wrong like call a non-static method
                    // on the class rather than on an instance of the class.
                    // XXX maybe better to do this before all the other rigmarole.
                    if (ManagedType.GetManagedObject(inst) is CLRObject co)
                    {
                        target = co.inst;
                    }
                    else
                    {
                        Exceptions.SetError(Exceptions.TypeError, "Invoked a non-static method with an invalid instance");
                        return null;
                    }
                }

                return new Binding(mi, target, margs, outs);
            }
            else if (matchGenerics && isGeneric)
            {
                // We weren't able to find a matching method but at least one
                // is a generic method and info is null. That happens when a generic
                // method was not called using the [] syntax. Let's introspect the
                // type of the arguments and use it to construct the correct method.
                Type[]? types = Runtime.PythonArgsToTypeArray(args, true);
                MethodInfo[] overloads = MatchParameters(methods, types);
                if (overloads.Length != 0)
                {
                    return Bind(inst, args, kwargDict, overloads, matchGenerics: false, allowRedirected: allowRedirected);
                }
            }
            if (mismatchedMethods.Count > 0)
            {
                var aggregateException = GetAggregateException(mismatchedMethods);
                Exceptions.SetError(aggregateException);
            }
            return null;
        }

        static AggregateException GetAggregateException(IEnumerable<MismatchedMethod> mismatchedMethods)
        {
            return new AggregateException(
                    mismatchedMethods.Select(m =>
                    {
                        return new ArgumentException($"{m.Exception.Message} in method {m.Method}", m.Exception);
                    })
                );
        }

        static BorrowedReference HandleParamsArray(BorrowedReference args, int arrayStart, int pyArgCount,
                                                   out NewReference tempObject)
        {
            BorrowedReference op;
            tempObject = default;
            // for a params method, we may have a sequence or single/multiple items
            // here we look to see if the item at the paramIndex is there or not
            // and then if it is a sequence itself.
            if ((pyArgCount - arrayStart) == 1)
            {
                // we only have one argument left, so we need to check it
                // to see if it is a sequence or a single item
                BorrowedReference item = Runtime.PyTuple_GetItem(args, arrayStart);
                if (!Runtime.PyString_Check(item) && Runtime.PySequence_Check(item))
                {
                    // it's a sequence (and not a string), so we use it as the op
                    op = item;
                }
                else
                {
                    tempObject = Runtime.PyTuple_GetSlice(args, arrayStart, pyArgCount);
                    op = tempObject.Borrow();
                }
            }
            else
            {
                tempObject = Runtime.PyTuple_GetSlice(args, arrayStart, pyArgCount);
                op = tempObject.Borrow();
            }
            return op;
        }


        /// <summary>
        /// Check whether the number of Python and .NET arguments match, and compute additional arg information.
        /// </summary>
        /// <param name="positionalArgumentCount">Number of positional args passed from Python.</param>
        /// <param name="parameters">Parameters of the specified .NET method.</param>
        /// <param name="kwargDict">Keyword args passed from Python.</param>
        /// <param name="paramsArray">True if the final param of the .NET method is an array (`params` keyword).</param>
        /// <param name="defaultArgList">List of default values for arguments.</param>
        /// <param name="kwargsMatched">Number of kwargs from Python that are also present in the .NET method.</param>
        /// <param name="defaultsNeeded">Number of non-null defaultsArgs.</param>
        /// <returns></returns>
        static bool MatchesArgumentCount(int positionalArgumentCount, ParameterInfo[] parameters,
                                         Dictionary<string, PyObject> kwargDict,
                                         out bool paramsArray,
                                         out ArrayList? defaultArgList,
                                         out int kwargsMatched,
                                         out int defaultsNeeded)
        {
            defaultArgList = null;
            var match = false;
            paramsArray = parameters.Length > 0
                       && Attribute.IsDefined(parameters[parameters.Length - 1], typeof(ParamArrayAttribute));
            kwargsMatched = 0;
            defaultsNeeded = 0;
            if (positionalArgumentCount == parameters.Length && kwargDict.Count == 0)
            {
                match = true;
            }
            else if (positionalArgumentCount < parameters.Length
                        && (!paramsArray || positionalArgumentCount == parameters.Length - 1))
            {
                match = true;
                // every parameter past 'positionalArgumentCount' must have either
                // a corresponding keyword arg or a default param, unless the method
                // method accepts a params array (which cannot have a default value)
                defaultArgList = new ArrayList();
                for (var v = positionalArgumentCount; v < parameters.Length; v++)
                {
                    if (parameters[v].Name is string pname && kwargDict.ContainsKey(pname))
                    {
                        // we have a keyword argument for this parameter,
                        // no need to check for a default parameter, but put a null
                        // placeholder in defaultArgList
                        defaultArgList.Add(null);
                        kwargsMatched++;
                    }
                    else if (parameters[v].IsOptional)
                    {
                        // IsOptional will be true if the parameter has a default value,
                        // or if the parameter has the [Optional] attribute specified.
                        // The GetDefaultValue() extension method will return the value
                        // to be passed in as the parameter value
                        defaultArgList.Add(GetDefaultValue(parameters[v]));
                        defaultsNeeded++;
                    }
                    else if (parameters[v].IsOut)
                    {
                        defaultArgList.Add(null);
                    }
                    else if (!paramsArray)
                    {
                        match = false;
                    }
                }
            }
            else if (positionalArgumentCount > parameters.Length && parameters.Length > 0 &&
                       Attribute.IsDefined(parameters[parameters.Length - 1], typeof(ParamArrayAttribute)))
            {
                // This is a `foo(params object[] bar)` style method
                match = true;
                paramsArray = true;
            }

            return match;
        }


        /// <summary>
        /// Attempts to convert Python positional argument tuple and keyword argument table
        /// into an array of managed objects, that can be passed to a method.
        /// If unsuccessful, returns null and may set a Python error.
        /// </summary>
        /// <param name="pi">Information about expected parameters</param>
        /// <param name="paramsArray"><c>true</c>, if the last parameter is a params array.</param>
        /// <param name="args">A pointer to the Python argument tuple</param>
        /// <param name="pyArgCount">Number of arguments, passed by Python</param>
        /// <param name="kwargDict">Dictionary of keyword argument name to python object pointer</param>
        /// <param name="defaultArgList">A list of default values for omitted parameters</param>
        /// <param name="outs">Returns number of output parameters</param>
        /// <returns>If successful, an array of .NET arguments that can be passed to the method.  Otherwise null.</returns>
        static object?[]? TryConvertArguments(ParameterInfo[] pi, bool paramsArray,
                                              BorrowedReference args, int pyArgCount,
                                              Dictionary<string, PyObject> kwargDict,
                                              ArrayList? defaultArgList,
                                              out int outs)
        {
            outs = 0;
            var margs = new object?[pi.Length];
            int arrayStart = paramsArray ? pi.Length - 1 : -1;

            for (int paramIndex = 0; paramIndex < pi.Length; paramIndex++)
            {
                var parameter = pi[paramIndex];
                bool hasNamedParam = parameter.Name != null && kwargDict.ContainsKey(parameter.Name);

                if (paramIndex >= pyArgCount && !(hasNamedParam || (paramsArray && paramIndex == arrayStart)))
                {
                    if (defaultArgList != null)
                    {
                        margs[paramIndex] = defaultArgList[paramIndex - pyArgCount];
                    }

                    if (parameter.ParameterType.IsByRef)
                    {
                        outs++;
                    }

                    continue;
                }

                BorrowedReference op;
                NewReference tempObject = default;
                if (hasNamedParam)
                {
                    op = kwargDict[parameter.Name!];
                }
                else
                {
                    if (arrayStart == paramIndex)
                    {
                        op = HandleParamsArray(args, arrayStart, pyArgCount, out tempObject);
                    }
                    else
                    {
                        op = Runtime.PyTuple_GetItem(args, paramIndex);
                    }
                }

                if (!TryConvertArgument(op, parameter.ParameterType, out margs[paramIndex], out bool isOut))
                {
                    tempObject.Dispose();
                    return null;
                }

                tempObject.Dispose();

                if (isOut)
                {
                    outs++;
                }
            }

            return margs;
        }


        static object? GetDefaultValue(ParameterInfo parameterInfo)
        {
            if (parameterInfo.HasDefaultValue)
            {
                return parameterInfo.DefaultValue;
            }
            else
            {
                // [OptionalAttribute] was specified for the parameter.
                // See https://stackoverflow.com/questions/3416216/optionalattribute-parameters-default-value
                // for rules on determining the value to pass to the parameter
                var type = parameterInfo.ParameterType;
                if (type == typeof(object))
                    return Type.Missing;
                else if (type.IsValueType)
                    return Activator.CreateInstance(type);
                else
                    return null;
            }
        }
    }
}
#endif
