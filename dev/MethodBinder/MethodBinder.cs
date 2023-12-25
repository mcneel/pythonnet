using System;
using System.Linq;
using System.Dynamic;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;

namespace Python.Runtime
{
    public readonly struct BorrowedReference
    {
        public static BorrowedReference Null => new();

        public readonly object? Value = default;

        public BorrowedReference(object? value) => Value = value;

        public bool IsNull => Value is null;

        public static bool operator ==(BorrowedReference br, object? _)
            => br.IsNull;

        public static bool operator !=(BorrowedReference br, object? _)
            => !br.IsNull;

        public static implicit operator BorrowedReference(PyObject pyObject)
            => new BorrowedReference(pyObject.Value);

        public override bool Equals(object? obj)
            => ReferenceEquals(Value, obj);

        public override int GetHashCode()
        {
            if (Value is null)
            {
                return 0;
            }

            return Value.GetHashCode();
        }
    }

    public readonly struct NewReference : IDisposable
    {
        public readonly object? Value = default;

        public NewReference(object? value) => Value = value;

        public BorrowedReference Borrow() => new(Value);

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }

    public sealed class PyObject : DynamicObject, IDisposable
    {
        public readonly object? Value = default;

        public PyObject(object? value) => Value = value;

        public PyObject(BorrowedReference br) => Value = br.Value;

        internal static PyObject? FromNullableReference(BorrowedReference br)
            => br.IsNull ? null : new PyObject(br);

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }

    static class Runtime
    {
        public static nint
        PyTuple_Size(BorrowedReference br)
        {
            if (br.Value is object?[] array)
            {
                return array.Length;
            }

            return 0;
        }

        public static BorrowedReference
        PyTuple_GetItem(BorrowedReference br, nint index)
        {
            if (br.Value is object?[] array)
            {
                return new BorrowedReference(array[index]);
            }

            return BorrowedReference.Null;
        }

        public static BorrowedReference
        PyList_GetItem(BorrowedReference br, nint index)
        {
            if (br.Value is object?[] array)
            {
                return new BorrowedReference(array[index]);
            }

            return BorrowedReference.Null;
        }

        public static nint
        PyDict_Size(BorrowedReference br)
        {
            if (br.Value is IDictionary dict)
            {
                return dict.Count;
            }

            return 0;
        }

        public static NewReference
        PyDict_Keys(BorrowedReference br)
        {
            if (br.Value is Dictionary<string, object?> dict)
            {
                return new NewReference(dict.Keys.ToArray());
            }

            return new NewReference(Array.Empty<string>());
        }

        public static NewReference
        PyDict_Values(BorrowedReference br)
        {
            if (br.Value is Dictionary<string, object?> dict)
            {
                return new NewReference(dict.Values.ToArray());
            }

            return new NewReference(Array.Empty<object?>());
        }

        public static BorrowedReference
        PyDict_GetItemString(BorrowedReference br, string key)
        {
            if (br.Value is Dictionary<string, object?> dict
                    && dict.ContainsKey(key))
            {
                return new BorrowedReference(dict[key]);
            }

            return BorrowedReference.Null;
        }

        public static string?
        GetManagedString(BorrowedReference br)
        {
            if (br.Value is string str)
            {
                return str;
            }

            return string.Empty;
        }
    }

    public partial class MethodBinder
    {
        public static NewReference Invoke(object instance,
                                          string name,
                                          object?[] args,
                                          Dictionary<string, object?> kwargs)
        {
            BorrowedReference instRef = new(instance);
            BorrowedReference argsRef = new(args);
            BorrowedReference kwargsRef = new(kwargs);

            MethodInfo[] methods =
                instance.GetType()
                        .GetMethods()
                        .Where(m => m.Name == name)
                        .ToArray();

            if (TryBind(methods, argsRef, kwargsRef, out BindSpec? spec))
            {
                object? result =
                    spec!.Method.Invoke(instance,
                                       spec.GetArguments());

                if (result is null)
                {
                    return new NewReference(BorrowedReference.Null);
                }
                else
                {
                    return new NewReference(result);
                }
            }

            throw new MethodAccessException(name);
        }


        const string OP_IMPLICIT = "op_Implicit";
        const string OP_EXPLICIT = "op_Explicit";

        static readonly BindingFlags FLAGS = BindingFlags.Static
                                           | BindingFlags.Public
                                           | BindingFlags.NonPublic;

        static Type? GetCLRType(BorrowedReference br, Type _)
            => br.Value?.GetType();

        static bool TryGetManagedValue(BorrowedReference br, Type type,
                                       out object? value)
        {
            return TryCast(br.Value, type, out value);
        }

        static bool TryCast(object? source, Type to, out object? cast)
        {
            cast = default;

            if (source is null)
            {
                return false;
            }

            Type from = source.GetType();

            MethodInfo castMethod =
                GetCast(from, from, to) ?? // cast operator in from type
                GetCast(to, from, to);     // cast operator in to type

            if (castMethod != null)
            {
                cast = castMethod.Invoke(null, new[] { source });
                return true;
            }

            return false;
        }

        static MethodInfo GetCast(Type @in, Type from, Type to)
        {
            return @in.GetMethods(FLAGS)
                       .FirstOrDefault(m =>
                       {
                           return (m.Name == OP_IMPLICIT
                                        || m.Name == OP_EXPLICIT)
                                && m.ReturnType == to
                                && m.GetParameters() is ParameterInfo[] pi
                                && pi.Length == 1
                                && pi[0].ParameterType == from;
                       });
        }
    }
}
