#pragma warning disable IDE1006 // Naming style
#pragma warning disable IDE0074 // Use compound assignment

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Python.Runtime
{
    using MaybeMethodInfo = MaybeMethodBase<MethodBase>;

    /// <summary>
    /// Implements a Python type that represents a CLR method. Method objects
    /// support a subscript syntax [] to allow explicit overload selection.
    /// </summary>
    /// <remarks>
    /// TODO: ForbidPythonThreadsAttribute per method info
    /// </remarks>
    [Serializable]
    internal class MethodObject : ExtensionType
    {
        private readonly List<MaybeMethodInfo> infoList;
        internal string name;
        internal readonly MethodBinder binder;
        internal bool is_static = false;

        internal bool SkipTypeErrors = false;

        internal PyString? doc;
        internal MaybeType type;

        protected MethodObject(MaybeType type, string name, MethodBase[] info, bool allow_threads)
        {
            this.type = type;
            this.name = name;
            this.infoList = new List<MaybeMethodInfo>();
            binder = new MethodBinder();
            foreach (MethodBase item in info)
            {
                this.infoList.Add(item);
                binder.AddMethod(item);
                if (item.IsStatic)
                {
                    this.is_static = true;
                }
            }
            binder.AllowThreads = allow_threads;
        }

        public MethodObject(MaybeType type, string name, MethodBase[] info)
            : this(type, name, info, allow_threads: AllowThreads(info))
        {
        }

        public bool IsInstanceConstructor => name == "__init__";

        public MethodObject WithOverloads(MethodBase[] overloads)
            => new(type, name, overloads, allow_threads: binder.AllowThreads);

        public bool TryInvoke(BorrowedReference inst, BorrowedReference args, BorrowedReference kw, out NewReference result)
        {
            result = InvokeMethod(inst, args, kw, null);

            // NOTE:
            // (in)equality checks in dotnet have typed arguments.
            // if case the type conversion failed from given arg to the
            // type required by the equality checker, lets continue and
            // use the default equality check. We will not clear any
            // other error that is thrown from the invokation.
            if (Exceptions.ExceptionMatches(Exceptions.TypeError))
            {
                Exceptions.Clear();
                // for operators like + or *, tell python "not supported"
                // so it tries the other operand or raises its usual error
                result = new NewReference(
                    OperatorMethod.OpMethodMap.ContainsKey(name) ?
                        Runtime.PyNotImplemented
                      : Runtime.PyFalse
                    );
                return false;
            }
            else
            {
                return true;
            }
        }

        public NewReference Invoke(BorrowedReference inst, BorrowedReference args, BorrowedReference kw)
        {
            if (SkipTypeErrors)
            {
                TryInvoke(inst, args, kw, out NewReference result);
                return result;
            }
            else
            {
                return InvokeMethod(inst, args, kw, null);
            }
        }

        public NewReference Invoke(BorrowedReference target, BorrowedReference args, BorrowedReference kw, MethodBase? info)
        {
            if (SkipTypeErrors)
            {
                TryInvoke(target, args, kw, out NewReference result);
                return result;
            }
            else
            {
                return InvokeMethod(target, args, kw, info);
            }
        }

        NewReference InvokeMethod(BorrowedReference target, BorrowedReference args, BorrowedReference kw, MethodBase? info)
        {
            // NOTE: do nothing on struct default ctor
            // do nothing and return 'void' when runtime is attempting
            // to call __init__ with no arguments on a struct.
            // ManagedType.GetManagedObject(target) returns the instance
            if (name == "__init__"
                    && !type.Value.IsClass
                    && Runtime.PyTuple_Size(args) == 0
                    && (kw.IsNull || (!kw.IsNull && Runtime.PyDict_Size(kw) == 0)))
            {
                return new NewReference(Runtime.PyNone);
            }

            return binder.Invoke(target, args, kw, info);
        }

        /// <summary>
        /// Helper to get docstrings from reflected method / param info.
        /// </summary>
        internal NewReference GetDocString()
        {
            if (doc is not null)
            {
                return new NewReference(doc);
            }
            var str = "";
            Type marker = typeof(DocStringAttribute);
            MethodBase[] methods = binder.GetMethods();
            foreach (MethodBase method in methods)
            {
                if (str.Length > 0)
                {
                    str += Environment.NewLine;
                }
                var attrs = (Attribute[])method.GetCustomAttributes(marker, false);
                if (attrs.Length == 0)
                {
                    str += method.ToString();
                }
                else
                {
                    var attr = (DocStringAttribute)attrs[0];
                    str += attr.DocString;
                }
            }
            doc = new PyString(str);
            return new NewReference(doc);
        }

        internal NewReference GetName()
        {
            var names = new HashSet<string>(binder.GetMethods().Select(m => m.Name));
            if (names.Count != 1)
            {
                Exceptions.SetError(Exceptions.AttributeError, "a method has no name");
                return default;
            }
            return Runtime.PyString_FromString(names.First());
        }

        /// <summary>
        /// This is a little tricky: a class can actually have a static method
        /// and instance methods all with the same name. That makes it tough
        /// to support calling a method 'unbound' (passing the instance as the
        /// first argument), because in this case we can't know whether to call
        /// the instance method unbound or call the static method.
        /// </summary>
        /// <remarks>
        /// The rule we is that if there are both instance and static methods
        /// with the same name, then we always call the static method. So this
        /// method returns true if any of the methods that are represented by
        /// the descriptor are static methods (called by MethodBinding).
        /// </remarks>
        internal bool IsStatic()
        {
            return is_static;
        }

        /// <summary>
        /// Descriptor __getattribute__ implementation.
        /// </summary>
        public static NewReference tp_getattro(BorrowedReference ob, BorrowedReference key)
        {
            var self = (MethodObject)GetManagedObject(ob)!;

            if (!Runtime.PyString_Check(key))
            {
                return Exceptions.RaiseTypeError("string expected");
            }

            if (Runtime.PyUnicode_Compare(key, PyIdentifier.__doc__) == 0)
            {
                return self.GetDocString();
            }

            return Runtime.PyObject_GenericGetAttr(ob, key);
        }

        /// <summary>
        /// Descriptor __get__ implementation. Accessing a CLR method returns
        /// a "bound" method similar to a Python bound method.
        /// </summary>
        /// <summary>
        /// Calls the method on the first argument: <c>m(obj, *args)</c> does what <c>obj.m(*args)</c> does.
        /// </summary>
        // Python calls this for `obj.m(...)` and operators instead of binding first, because the
        // type has TypeFlags.MethodDescriptor; it must match tp_descr_get + MethodBinding.tp_call.
        public static NewReference tp_call(BorrowedReference ds, BorrowedReference args, BorrowedReference kw)
        {
            var self = (MethodObject)GetManagedObject(ds)!;

            if (!self.type.Valid)
            {
                return Exceptions.RaiseTypeError(self.type.DeletedMessage);
            }

            nint count = Runtime.PyTuple_Size(args);
            if (count < 1)
            {
                return Exceptions.RaiseTypeError("not enough arguments");
            }

            BorrowedReference ob = Runtime.PyTuple_GetItem(args, 0);
            BorrowedReference tp = Runtime.PyObject_TYPE(ob);

            // as tp_descr_get: super(Derived, self).method(...) binds to the base class
            if (GetManagedObject(ob) is CLRObject obj
                && obj.inst.GetType() != self.type.Value
                && obj.inst is IPythonDerivedType
                && self.type.Value.IsInstanceOfType(obj.inst))
            {
                tp = ReflectedClrType.GetOrCreate(self.type.Value).Reference;
            }

            using NewReference rest = Runtime.PyTuple_GetSlice(args, 1, count);
            return MethodBinding.Call(self, null, ob, tp, rest.Borrow(), kw);
        }

        public static NewReference tp_descr_get(BorrowedReference ds, BorrowedReference ob, BorrowedReference tp)
        {
            var self = (MethodObject)GetManagedObject(ds)!;

            if (!self.type.Valid)
            {
                return Exceptions.RaiseTypeError(self.type.DeletedMessage);
            }

            // If the method is accessed through its type (rather than via
            // an instance) we return an 'unbound' MethodBinding that will
            // cached for future accesses through the type.

            if (ob == null)
            {
                var binding = new MethodBinding(self, target: null, targetType: new PyType(tp));
                return binding.Alloc();
            }

            if (Runtime.PyObject_IsInstance(ob, tp) < 1)
            {
                return Exceptions.RaiseTypeError("invalid argument");
            }

            // If the object this descriptor is being called with is a subclass of the type
            // this descriptor was defined on then it will be because the base class method
            // is being called via super(Derived, self).method(...).
            // In which case create a MethodBinding bound to the base class.
            if (GetManagedObject(ob) is CLRObject obj
                && obj.inst.GetType() != self.type.Value
                && obj.inst is IPythonDerivedType
                && self.type.Value.IsInstanceOfType(obj.inst))
            {
                var basecls = ReflectedClrType.GetOrCreate(self.type.Value);
                return new MethodBinding(self, new PyObject(ob), basecls.NewReference()).Alloc();
            }

            return new MethodBinding(self, target: new PyObject(ob), targetType: new PyType(tp)).Alloc();
        }

        /// <summary>
        /// Descriptor __repr__ implementation.
        /// </summary>
        public static NewReference tp_repr(BorrowedReference ob)
        {
            var self = (MethodObject)GetManagedObject(ob)!;
            return Runtime.PyString_FromString($"<method '{self.name}'>");
        }

        static bool AllowThreads(MethodBase[] methods)
        {
            bool hasAllowOverload = false, hasForbidOverload = false;
            foreach (var method in methods)
            {
                bool forbidsThreads = method.GetCustomAttribute<ForbidPythonThreadsAttribute>(inherit: false) != null;
                if (forbidsThreads)
                {
                    hasForbidOverload = true;
                }
                else
                {
                    hasAllowOverload = true;
                }
            }

            if (hasAllowOverload && hasForbidOverload)
                throw new NotImplementedException("All method overloads currently must either allow or forbid Python threads together");

            return !hasForbidOverload;
        }
    }
}
