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
        // exactly the same. short, byte, sbyte, ushort, uint, ulong and char also qualify and can be
        // added. Enums, DateTime, decimal, structs like Point3d and other objects are left out: user
        // converters may handle them or they get wrapped as .NET objects.
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
