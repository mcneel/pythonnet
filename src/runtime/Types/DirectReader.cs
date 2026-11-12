using System;
using System.Linq.Expressions;
using System.Reflection;

namespace Python.Runtime
{
    /// <summary>
    /// Reads an instance property or field straight into a Python value.
    /// <c>TryRead</c> gives false when the regular path must be used instead.
    /// </summary>
    // Skips reflection's boxing and the general converter for the common simple types.
    internal sealed class DirectReader
    {
        delegate NewReference Reader(object target);

        static readonly DirectReader s_unsupported = new(typeof(object), null);

        readonly Type _owner;
        readonly Reader? _read;

        DirectReader(Type owner, Reader? read)
        {
            _owner = owner;
            _read = read;
        }

        internal static DirectReader ForProperty(MethodInfo getter)
        {
            if (getter.IsStatic || getter.GetParameters().Length != 0) return s_unsupported;
            return Build(getter.DeclaringType, getter.ReturnType, t => Expression.Call(t, getter));
        }

        internal static DirectReader ForField(FieldInfo field)
        {
            if (field.IsStatic || field.IsLiteral) return s_unsupported;
            return Build(field.DeclaringType, field.FieldType, t => Expression.Field(t, field));
        }

        internal bool TryRead(object target, out NewReference value)
        {
            if (_read is null)
            {
                value = default;
                return false;
            }

            try
            {
                value = _read(target);
                return true;
            }
            // checking the target only once the read fails keeps the check off the usual path;
            // the regular path then reports the mismatch
            catch (Exception e) when (e is InvalidCastException or NullReferenceException && !_owner.IsInstanceOfType(target))
            {
                value = default;
                return false;
            }
        }

        // NOTE: only types that the regular converter (Converter.ToPython) always turns into a plain
        // Python value in one step (number, bool, str or None) are handled here, so the result is
        // exactly the same. That is every simple number type, char, bool and string. Enums, DateTime,
        // decimal, structs like Point3d and other objects are left out: user converters may handle
        // them or they get wrapped as .NET objects.
        static DirectReader Build(Type? owner, Type valueType, Func<Expression, Expression> read)
        {
            if (owner is null || owner.ContainsGenericParameters || valueType.IsEnum) return s_unsupported;
            try
            {
                Reader? reader = Type.GetTypeCode(valueType) switch
                {
                    TypeCode.Double or TypeCode.Single => Floats(Compile<double>(owner, read)),
                    TypeCode.Int32 or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Byte or TypeCode.SByte
                        => Ints(Compile<int>(owner, read)),
                    TypeCode.Int64 => Longs(Compile<long>(owner, read)),
                    TypeCode.UInt32 or TypeCode.UInt64 => UnsignedLongs(Compile<ulong>(owner, read)),
                    TypeCode.Char => Chars(Compile<int>(owner, read)),
                    TypeCode.Boolean => Bools(Compile<bool>(owner, read)),
                    TypeCode.String => Strings(Compile<string?>(owner, read)),
                    _ => null,
                };
                return reader is null ? s_unsupported : new DirectReader(owner, reader);
            }
            catch (Exception)
            {
                // e.g. no runtime code generation available: the regular path still works
                return s_unsupported;
            }
        }

        static Reader Floats(Func<object, double> f) => o => Runtime.PyFloat_FromDouble(f(o));
        static Reader Ints(Func<object, int> f) => o => Runtime.PyInt_FromInt32(f(o));
        static Reader Longs(Func<object, long> f) => o => Runtime.PyLong_FromLongLong(f(o));
        static Reader UnsignedLongs(Func<object, ulong> f) => o => Runtime.PyLong_FromUnsignedLongLong(f(o));
        static Reader Chars(Func<object, int> f) => o => Runtime.PyUnicode_FromOrdinal(f(o));
        static Reader Bools(Func<object, bool> f) => o => new NewReference(f(o) ? Runtime.PyTrue : Runtime.PyFalse);
        static Reader Strings(Func<object, string?> f)
            => o => f(o) is { } s ? Runtime.PyString_FromString(s) : new NewReference(Runtime.PyNone);

        // The value is widened to T as part of the read, so one T serves several value types.
        static Func<object, T> Compile<T>(Type owner, Func<Expression, Expression> read)
        {
            var target = Expression.Parameter(typeof(object));
            Expression typed = owner.IsValueType ? Expression.Unbox(target, owner) : Expression.Convert(target, owner);
            Expression value = read(typed);
            if (value.Type != typeof(T))
            {
                value = Expression.Convert(value, typeof(T));
            }
            return Expression.Lambda<Func<object, T>>(value, target).Compile();
        }
    }
}
