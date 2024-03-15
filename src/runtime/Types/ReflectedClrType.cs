using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Serialization;

using static Python.Runtime.PythonException;

namespace Python.Runtime;

[Serializable]
internal sealed class ReflectedClrType : PyType
{
    private ReflectedClrType(StolenReference reference) : base(reference, prevalidated: true) { }
    internal ReflectedClrType(ReflectedClrType original) : base(original, prevalidated: true) { }
    internal ReflectedClrType(BorrowedReference original) : base(original) { }
    ReflectedClrType(SerializationInfo info, StreamingContext context) : base(info, context) { }

    internal ClassBase Impl => (ClassBase)ManagedType.GetManagedObject(this)!;

    /// <summary>
    /// Get the Python type that reflects the given CLR type.
    /// </summary>
    /// <remarks>
    /// Returned <see cref="ReflectedClrType"/> might be partially initialized.
    /// </remarks>
    public static ReflectedClrType GetOrCreate(Type type)
    {
        if (ClassManager.cache.TryGetValue(type, out ReflectedClrType pyType))
        {
            return pyType;
        }

        pyType = AllocateClass(type);
        ClassManager.cache.Add(type, pyType);

        ClassBase impl = ClassManager.CreateClass(type);

        TypeManager.InitializeClassCore(type, pyType, impl);
        ClassManager.InitClassBase(type, impl, pyType);
        // Now we force initialize the Python type object to reflect the given
        // managed type, filling the Python type slots with thunks that
        // point to the managed methods providing the implementation.
        TypeManager.InitializeClass(pyType, impl, type);

        return pyType;
    }

    internal void Restore(Dictionary<string, object?> context)
    {
        var cb = (ClassBase)context["impl"]!;

        Debug.Assert(cb is not null);

        cb!.Load(this, context);

        Restore(cb);
    }

    internal void Restore(ClassBase cb)
    {
        ClassManager.InitClassBase(cb.type.Value, cb, this);

        TypeManager.InitializeClass(this, cb, cb.type.Value);
    }

    internal static NewReference CreateSubclass(ClassBase baseType, IEnumerable<Type> interfaces,
                                                string name, string? assembly, string? ns,
                                                BorrowedReference dict)
    {
        try
        {
            Type subType;

            subType = ClassDerivedObject.CreateDerivedType(name,
                baseType.type.Value,
                interfaces,
                dict,
                ns,
                assembly);

            ClassManager.cache.Remove(subType);
            ReflectedClrType pyTypeObj = GetOrCreate(subType);

            // by default the class dict will have all the C# methods in it, but as this is a
            // derived class we want the python overrides in there instead if they exist.
            var cls_dict = Util.ReadRef(pyTypeObj, TypeOffset.tp_dict);
            ThrowIfIsNotZero(Runtime.PyDict_Update(cls_dict, dict));
            // Update the __classcell__ if it exists
            BorrowedReference cell = Runtime.PyDict_GetItemString(cls_dict, "__classcell__");
            if (!cell.IsNull)
            {
                ThrowIfIsNotZero(Runtime.PyCell_Set(cell, pyTypeObj));
                ThrowIfIsNotZero(Runtime.PyDict_DelItemString(cls_dict, "__classcell__"));
            }

            const BindingFlags tbFlags = BindingFlags.Public | BindingFlags.Static;
            using var clsDict = new PyDict(dict);
            using var keys = clsDict.Keys();
            foreach (PyObject pyKey in keys)
            {
                string? keyStr = Runtime.GetManagedString(pyKey);
                if (keyStr is null)
                {
                    continue;
                }

                var tp_getattro_default = typeof(ReflectedClrType).GetMethod(nameof(ReflectedClrType.tp_getattro), tbFlags);
                Util.WriteIntPtr(pyTypeObj, TypeOffset.tp_getattro, Interop.GetThunk(tp_getattro_default).Address);


                if (keyStr.StartsWith(nameof(PyIdentifier.__getitem__)))
                {
                    var mp_subscript = typeof(ReflectedClrType).GetMethod(nameof(ReflectedClrType.mp_subscript), tbFlags);
                    Util.WriteIntPtr(pyTypeObj, TypeOffset.mp_subscript, Interop.GetThunk(mp_subscript).Address);
                }

                if (keyStr.StartsWith(nameof(PyIdentifier.__len__)))
                {
                    var sq_length = typeof(ReflectedClrType).GetMethod(nameof(ReflectedClrType.sq_length), tbFlags);
                    Util.WriteIntPtr(pyTypeObj, TypeOffset.sq_length, Interop.GetThunk(sq_length).Address);
                }

                if (keyStr.StartsWith(nameof(PyIdentifier.__iter__)))
                {
                    var tp_iter = typeof(ReflectedClrType).GetMethod(nameof(ReflectedClrType.tp_iter), tbFlags);
                    Util.WriteIntPtr(pyTypeObj, TypeOffset.tp_iter, Interop.GetThunk(tp_iter).Address);
                }

                if (keyStr.StartsWith(nameof(PyIdentifier.__str__)))
                {
                    var tp_str = typeof(ReflectedClrType).GetMethod(nameof(ReflectedClrType.tp_str), tbFlags);
                    Util.WriteIntPtr(pyTypeObj, TypeOffset.tp_str, Interop.GetThunk(tp_str).Address);
                }

                if (keyStr.StartsWith(nameof(PyIdentifier.__repr__)))
                {
                    var tp_repr = typeof(ReflectedClrType).GetMethod(nameof(ReflectedClrType.tp_repr), tbFlags);
                    Util.WriteIntPtr(pyTypeObj, TypeOffset.tp_repr, Interop.GetThunk(tp_repr).Address);
                }

                pyKey.Dispose();
            }

            return new NewReference(pyTypeObj);
        }
        catch (Exception e)
        {
            return Exceptions.RaiseTypeError(e.Message);
        }
    }

    public static NewReference mp_subscript(BorrowedReference ob, BorrowedReference key)
    {
        CLRObject? clrObj = ManagedType.GetManagedObject(ob) as CLRObject;
        if (clrObj is null)
        {
            return Exceptions.RaiseTypeError("invalid object");
        }

        if (TryCallBoundMethod1(ob, key, nameof(PyIdentifier.__getitem__), out NewReference result))
        {
            return result;
        }

        using var objRepr = Runtime.PyObject_Repr(ob);
        using var keyRepr = Runtime.PyObject_Repr(key);
        Exceptions.SetError(
                Exceptions.KeyError,
                Runtime.GetManagedString(keyRepr.BorrowOrThrow())!
            );
        return default;
    }

    public static NewReference sq_length(BorrowedReference ob)
    {
        CLRObject? clrObj = ManagedType.GetManagedObject(ob) as CLRObject;
        if (clrObj is null)
        {
            return Exceptions.RaiseTypeError("invalid object");
        }

        if (TryCallBoundMethod0(ob, nameof(PyIdentifier.__len__), out NewReference result))
        {
            return result;
        }

        return Runtime.PyInt_FromInt32(0);
    }

    public static NewReference tp_iter(BorrowedReference ob)
    {
        CLRObject? clrObj = ManagedType.GetManagedObject(ob) as CLRObject;
        if (clrObj is null)
        {
            return Exceptions.RaiseTypeError("invalid object");
        }

        if (TryCallBoundMethod0(ob, nameof(PyIdentifier.__iter__), out NewReference result))
        {
            return result;
        }

        return new NewReference(Runtime.None);
    }

    public static NewReference tp_str(BorrowedReference ob)
    {
        CLRObject? clrObj = ManagedType.GetManagedObject(ob) as CLRObject;
        if (clrObj is null)
        {
            return Exceptions.RaiseTypeError("invalid object");
        }

        if (TryCallBoundMethod0(ob, nameof(PyIdentifier.__str__), out NewReference result))
        {
            return result;
        }

        return ClassObject.tp_str(ob);
    }

    public static NewReference tp_repr(BorrowedReference ob)
    {
        CLRObject? clrObj = ManagedType.GetManagedObject(ob) as CLRObject;
        if (clrObj is null)
        {
            return Exceptions.RaiseTypeError("invalid object");
        }

        if (TryCallBoundMethod0(ob, nameof(PyIdentifier.__repr__), out NewReference result))
        {
            return result;
        }

        return ClassObject.tp_repr(ob);
    }

    public static NewReference tp_getattro(BorrowedReference ob, BorrowedReference key)
    {
        CLRObject? clrObj = ManagedType.GetManagedObject(ob) as CLRObject;
        if (clrObj is null)
        {
            return Exceptions.RaiseTypeError("invalid object");
        }

        if (TryGetBuiltinAttr(ob, key, out NewReference builtinAttr))
        {
            return builtinAttr;
        }

        if (TryCallBoundMethod1(ob, key, nameof(PyIdentifier.__getattribute__), out NewReference getAttro))
        {
            if (Exceptions.ExceptionMatches(Exceptions.AttributeError))
            {
                Exceptions.Clear();

                if (TryCallBoundMethod1(ob, key, nameof(PyIdentifier.__getattr__), out NewReference getAttr))
                {
                    return getAttr;
                }

                Exceptions.SetError(Exceptions.AttributeError, $"");
            }

            return getAttro;
        }

        return Runtime.PyObject_GenericGetAttr(ob, key);
    }

    static bool TryCallBoundMethod0(BorrowedReference ob, string keyName, out NewReference result)
    {
        result = default;

        using var pyType = Runtime.PyObject_Type(ob);
        using var getAttrKey = new PyString(keyName);
        var getattr = Runtime._PyType_Lookup(pyType.Borrow(), getAttrKey);
        if (getattr.IsNull)
        {
            return false;
        }

        using var method = Runtime.PyObject_GenericGetAttr(ob, getAttrKey);
        using var args = Runtime.PyTuple_New(0);
        result = Runtime.PyObject_Call(method.Borrow(), args.Borrow(), null);
        return true;
    }

    static bool TryCallBoundMethod1(BorrowedReference ob, BorrowedReference key, string keyName, out NewReference result)
    {
        result = default;

        using var pyType = Runtime.PyObject_Type(ob);
        using var getAttrKey = new PyString(keyName);
        var getattr = Runtime._PyType_Lookup(pyType.Borrow(), getAttrKey);
        if (getattr.IsNull)
        {
            return false;
        }

        using var method = Runtime.PyObject_GenericGetAttr(ob, getAttrKey);
        using var args = Runtime.PyTuple_New(1);
        Runtime.PyTuple_SetItem(args.Borrow(), 0, key);
        result = Runtime.PyObject_Call(method.Borrow(), args.Borrow(), null);
        return true;
    }

    static bool TryGetBuiltinAttr(BorrowedReference ob, BorrowedReference key, out NewReference result)
    {
        result = default;

        string? keyStr = Runtime.GetManagedString(key);
        if (keyStr is null)
        {
            return false;
        }

        if (keyStr == nameof(PyIdentifier.__init__))
        {
            result = Runtime.PyObject_GenericGetAttr(ob, key);
            return true;
        }

        return false;
    }

    static ReflectedClrType AllocateClass(Type clrType)
    {
        string name = TypeManager.GetPythonTypeName(clrType);

        var type = TypeManager.AllocateTypeObject(name, Runtime.PyCLRMetaType);
        type.Flags = TypeFlags.Default
                        | TypeFlags.HasClrInstance
                        | TypeFlags.HeapType
                        | TypeFlags.BaseType
                        | TypeFlags.HaveGC;

        return new ReflectedClrType(type.Steal());
    }

    public override bool Equals(PyObject? other) => rawPtr == other?.DangerousGetAddressOrNull();
    public override int GetHashCode() => rawPtr.GetHashCode();
}
