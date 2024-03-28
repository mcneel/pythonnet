using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Python.Runtime
{
    [Serializable]
    internal sealed class EnumObject : ClassBase
    {
        readonly bool _hasFlagsAttribute = false;

        internal EnumObject(Type tp) : base(tp)
        {
            _hasFlagsAttribute = tp.GetCustomAttribute<FlagsAttribute>() is not null;
        }

        internal override bool CanSubclass() => false;

        public static NewReference tp_new(BorrowedReference tp, BorrowedReference args, BorrowedReference kw)
        {
            var self = (EnumObject)GetManagedObject(tp)!;

            if (!self.type.Valid)
            {
                return Exceptions.RaiseTypeError(self.type.DeletedMessage);
            }


            Type type = self.type.Value;
            nint argSize = Runtime.PyTuple_Size(args);

            if (argSize > 2)
            {
                return Exceptions.RaiseTypeError($"class takes at most 2 argument ({argSize} given)");
            }

            object enumInstance;
            bool allowUnchecked = self._hasFlagsAttribute;
            if (argSize >= 1)
            {
                if (argSize == 2)
                {
                    var allow = Runtime.PyTuple_GetItem(args, 1);
                    if (!Converter.ToManaged(allow, typeof(bool), out var allowObj, true) || allowObj is null)
                    {
                        Exceptions.RaiseTypeError("second argument to enum constructor must be a boolean");
                        return default;
                    }
                    allowUnchecked |= (bool)allowObj;
                }

                BorrowedReference pyArg = Runtime.PyTuple_GetItem(args, 0);
                if (Runtime.PyInt_Check(pyArg)
                        && Converter.ToPrimitive(pyArg, Enum.GetUnderlyingType(type), out object? result, setError: true)
                        && result is not null
                        && (Enum.IsDefined(type, result) || allowUnchecked))
                {
                    enumInstance = Enum.ToObject(type, result);
                }
                else if (PyObject.FromNullableReference(pyArg) is PyObject pyObj)
                {
                    if (Exceptions.ErrorOccurred())
                        return default;

                    return Exceptions.RaiseValueError($"{pyObj.Repr()} is not a valid {type.FullName}");
                }
                else
                {
                    if (Exceptions.ErrorOccurred())
                        return default;

                    return Exceptions.RaiseValueError($"None is not a valid {type.FullName}");
                }
            }
            else
                enumInstance = Activator.CreateInstance(type);

            return CLRObject.GetReference(enumInstance, type);
        }

        public static NewReference nb_int(BorrowedReference ob)
        {
            var co = GetManagedObject(ob) as CLRObject;
            if (co == null)
            {
                return Exceptions.RaiseTypeError("invalid enum object");
            }

            try
            {
                if (co.inst is null)
                {
                    return new NewReference(Runtime.PyFalse);
                }

                int value = Convert.ToInt32(co.inst);
                return Runtime.PyInt_FromInt32(value);
            }
            catch (Exception e)
            {
                if (e.InnerException != null)
                {
                    e = e.InnerException;
                }
                Exceptions.SetError(e);
                return default;
            }
        }

        public static int nb_bool(BorrowedReference ob)
        {
            var co = GetManagedObject(ob) as CLRObject;
            if (co == null)
            {
                return 0;
            }

            try
            {
                if (co.inst is null)
                {
                    return 0;
                }

                int value = Convert.ToInt32(co.inst);
                return value;
            }
            catch
            {
                return 0;
            }
        }

        public new static NewReference tp_repr(BorrowedReference ob)
        {
            if (GetManagedObject(ob) is not CLRObject co)
            {
                return Exceptions.RaiseTypeError("invalid enum object");
            }

            if (co.inst.GetType().IsEnum)
            {
                return Runtime.PyString_FromString(GetEnumReprString((Enum)co.inst));
            }

            return ClassBase.tp_repr(ob);
        }

        private static string GetEnumReprString(Enum inst)
        {
            var obType = inst.GetType();

            string strValue2 = obType.IsFlagsEnum() ? ConvertFlags(inst) : ConvertValue(inst);

            var repr = $"<{obType.Name}.{inst}: {strValue2}>";
            return repr;
        }

        private static string ConvertFlags(Enum value)
        {
            Type primitiveType = value.GetType().GetEnumUnderlyingType();
            string format = "X" + (Marshal.SizeOf(primitiveType) * 2).ToString(CultureInfo.InvariantCulture);
            var primitive = (IFormattable)Convert.ChangeType(value, primitiveType);
            return "0x" + primitive.ToString(format, null);

        }

        private static string ConvertValue(Enum value)
        {
            Type primitiveType = value.GetType().GetEnumUnderlyingType();
            return Convert.ChangeType(value, primitiveType).ToString()!;
        }
    }
}
