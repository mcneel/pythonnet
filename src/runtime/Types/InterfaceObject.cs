using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Python.Runtime
{
    /// <summary>
    /// Provides the implementation for reflected interface types. Managed
    /// interfaces are represented in Python by actual Python type objects.
    /// Each of those type objects is associated with an instance of this
    /// class, which provides the implementation for the Python type.
    /// </summary>
    [Serializable]
    internal class InterfaceObject : ClassBase
    {
        [NonSerialized]
        internal ConstructorInfo? ctor;

        internal InterfaceObject(Type tp) : base(tp)
        {
            this.ctor = TryGetCOMConstructor(tp);
        }

        static ConstructorInfo? TryGetCOMConstructor(Type tp)
        {
            var comClass = (CoClassAttribute?)Attribute.GetCustomAttribute(tp, cc_attr);
            return comClass?.CoClass.GetConstructor(Type.EmptyTypes);
        }

        private static readonly Type cc_attr;

        static InterfaceObject()
        {
            cc_attr = typeof(CoClassAttribute);
        }

        /// <summary>
        /// Implements __new__ for reflected interface types.
        /// </summary>
        public static NewReference tp_new(BorrowedReference tp, BorrowedReference args, BorrowedReference kw)
        {
            var self = (InterfaceObject)GetManagedObject(tp)!;
            if (!self.type.Valid)
            {
                return Exceptions.RaiseTypeError(self.type.DeletedMessage);
            }
            var nargs = Runtime.PyTuple_Size(args);
            Type type = self.type.Value;
            object obj;

            if (nargs == 1)
            {
                BorrowedReference inst = Runtime.PyTuple_GetItem(args, 0);

                if (GetManagedObject(inst) is CLRObject co && type.IsInstanceOfType(co.inst))
                {
                    obj = co.inst;
                }
                else
                {
                    Exceptions.SetError(Exceptions.TypeError, $"object does not implement {type.Name}");
                    return default;
                }
            }

            else if (nargs == 0 && self.ctor != null)
            {
                obj = self.ctor.Invoke(null);

                if (obj == null || !type.IsInstanceOfType(obj))
                {
                    Exceptions.SetError(Exceptions.TypeError, "CoClass default constructor failed");
                    return default;
                }
            }

            else
            {
                Exceptions.SetError(Exceptions.TypeError, "interface takes exactly one argument");
                return default;
            }

            return self.TryWrapObject(obj);
        }

        /// <summary>
        /// Wrap the given object in an interface object, so that only methods
        /// of the interface are available.
        /// </summary>
        public NewReference TryWrapObject(object impl)
            => this.type.Valid
                ? CLRObject.GetReference(impl, ClassManager.GetClass(this.type.Value))
                : Exceptions.RaiseTypeError(this.type.DeletedMessage);

        /// <summary>
        /// Expose the wrapped implementation through attributes in both
        /// converted/encoded (__implementation__) and raw (__raw_implementation__) form.
        /// </summary>
        public static NewReference tp_getattro(BorrowedReference ob, BorrowedReference key)
        {
            var clrObj = (CLRObject)GetManagedObject(ob)!;

            if (!Runtime.PyString_Check(key))
            {
                return Exceptions.RaiseTypeError("string expected");
            }

            string? name = Runtime.GetManagedString(key);
            if (name != null)
            {
                if (name == "__implementation__")
                {
                    return Converter.ToPython(clrObj.inst);
                }
                else if (name == "__raw_implementation__")
                {
                    return CLRObject.GetReference(clrObj.inst);
                }
                else
                {
                    // try get attr from pure interface wrapper
                    var value = Runtime.PyObject_GenericGetAttr(ob, key);
                    if (Exceptions.ErrorOccurred())
                    {
                        // if that didn't work, clear errors
                        // and try get from wrapped object
                        Exceptions.Clear();

                        using var pyObj = Converter.ToPython(clrObj.inst);
                        var get = Runtime.PyObject_GenericGetAttr(pyObj.Borrow(), key);
                        return get;
                    }
                    return value;
                }
            }


            return Runtime.PyObject_GenericGetAttr(ob, key);
        }

        public static int tp_setattro(BorrowedReference ob, BorrowedReference key, BorrowedReference value)
        {
            var clrObj = (CLRObject)GetManagedObject(ob)!;

            if (!Runtime.PyString_Check(key))
            {
                Exceptions.RaiseTypeError("string expected");
                return -1;
            }

            string? name = Runtime.GetManagedString(key);
            if (name != null)
            {
                if (name == "__implementation__"
                        || name == "__raw_implementation__")
                {
                    Exceptions.SetError(Exceptions.AttributeError, "attribute is read-only");
                    return -1;
                }
                else
                {
                    // try set attr to wrapped object first we we do not
                    // add attributes to the object dictionary
                    using var pyObj = Converter.ToPython(clrObj.inst);
                    int res = Runtime.PyObject_GenericSetAttr(pyObj.Borrow(), key, value);
                    if (Exceptions.ErrorOccurred())
                    {
                        // if that didn't work, clear errors
                        // and try set to pure interface wrapper
                        Exceptions.Clear();

                        return Runtime.PyObject_GenericSetAttr(ob, key, value);
                    }
                    return res;
                }
            }


            return Runtime.PyObject_GenericSetAttr(ob, key, value);
        }


        protected override void OnDeserialization(object sender)
        {
            base.OnDeserialization(sender);
            if (this.type.Valid)
            {
                this.ctor = TryGetCOMConstructor(this.type.Value);
            }
        }
    }
}
