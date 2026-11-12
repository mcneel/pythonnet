using System;
using System.Linq.Expressions;
using System.Reflection;

namespace Python.Runtime
{
    /// <summary>Reads an instance property or field straight into a Python value.</summary>
    // Skips reflection's boxing and the general converter for the common simple types;
    // anything else returns null and keeps the regular path.
    internal static class DirectReader
    {
        internal delegate NewReference Reader(object target);

        internal static Reader? ForProperty(MethodInfo getter)
        {
            if (getter.IsStatic || getter.GetParameters().Length != 0) return null;
            return Build(getter.DeclaringType, getter.ReturnType, t => Expression.Call(t, getter));
        }

        internal static Reader? ForField(FieldInfo field)
        {
            if (field.IsStatic || field.IsLiteral) return null;
            return Build(field.DeclaringType, field.FieldType, t => Expression.Field(t, field));
        }

        // NOTE: only types that the regular converter (Converter.ToPython) always turns into a plain
        // Python value in one step (number, bool, str or None) are handled here, so the result is
        // exactly the same. That is every simple number type, char, bool and string. Enums, DateTime,
        // decimal, structs like Point3d and other objects are left out: user converters may handle
        // them or they get wrapped as .NET objects.
        static Reader? Build(Type? owner, Type valueType, Func<Expression, Expression> read)
        {
            if (owner is null || owner.ContainsGenericParameters) return null;
            try
            {
                if (valueType == typeof(double))
                {
                    var f = Compile<double>(owner, read);
                    return o => Runtime.PyFloat_FromDouble(f(o));
                }
                if (valueType == typeof(float))
                {
                    var f = Compile<float>(owner, read);
                    return o => Runtime.PyFloat_FromDouble(f(o));
                }
                if (valueType == typeof(int))
                {
                    var f = Compile<int>(owner, read);
                    return o => Runtime.PyInt_FromInt32(f(o));
                }
                if (valueType == typeof(long))
                {
                    var f = Compile<long>(owner, read);
                    return o => Runtime.PyLong_FromLongLong(f(o));
                }
                if (valueType == typeof(short))
                {
                    var f = Compile<short>(owner, read);
                    return o => Runtime.PyInt_FromInt32(f(o));
                }
                if (valueType == typeof(byte))
                {
                    var f = Compile<byte>(owner, read);
                    return o => Runtime.PyInt_FromInt32(f(o));
                }
                if (valueType == typeof(sbyte))
                {
                    var f = Compile<sbyte>(owner, read);
                    return o => Runtime.PyInt_FromInt32(f(o));
                }
                if (valueType == typeof(ushort))
                {
                    var f = Compile<ushort>(owner, read);
                    return o => Runtime.PyInt_FromInt32(f(o));
                }
                if (valueType == typeof(uint))
                {
                    var f = Compile<uint>(owner, read);
                    return o => Runtime.PyLong_FromUnsignedLongLong(f(o));
                }
                if (valueType == typeof(ulong))
                {
                    var f = Compile<ulong>(owner, read);
                    return o => Runtime.PyLong_FromUnsignedLongLong(f(o));
                }
                if (valueType == typeof(char))
                {
                    var f = Compile<char>(owner, read);
                    return o => Runtime.PyUnicode_FromOrdinal(f(o));
                }
                if (valueType == typeof(bool))
                {
                    var f = Compile<bool>(owner, read);
                    return o => new NewReference(f(o) ? Runtime.PyTrue : Runtime.PyFalse);
                }
                if (valueType == typeof(string))
                {
                    var f = Compile<string?>(owner, read);
                    return o => f(o) is { } s ? Runtime.PyString_FromString(s) : new NewReference(Runtime.PyNone);
                }
            }
            catch (Exception)
            {
                // e.g. no runtime code generation available: the regular path still works
            }
            return null;
        }

        static Func<object, T> Compile<T>(Type owner, Func<Expression, Expression> read)
        {
            var target = Expression.Parameter(typeof(object));
            Expression typed = owner.IsValueType ? Expression.Unbox(target, owner) : Expression.Convert(target, owner);
            return Expression.Lambda<Func<object, T>>(read(typed), target).Compile();
        }
    }
}
