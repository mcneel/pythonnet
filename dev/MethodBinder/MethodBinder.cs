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
            => new(pyObject.Value);

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

        public bool IsNull() => Value is null;

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
        public static PyObject None => new(BorrowedReference.Null);

        public static nint
        PyTuple_Size(BorrowedReference br)
        {
            if (br.Value?.GetType().IsArray ?? false)
            {
                return ((Array)br.Value).Length;
            }

            return 0;
        }

        public static bool
        PySequence_Check(BorrowedReference br)
        {
            if (br.Value?.GetType().IsArray ?? false)
            {
                return true;
            }

            return false;
        }

        public static nint
        PySequence_Size(BorrowedReference br)
        {
            if (br.Value?.GetType().IsArray ?? false)
            {
                return ((Array)br.Value).Length;
            }

            return 0;
        }

        public static BorrowedReference
        PyTuple_GetItem(BorrowedReference br, nint index)
        {
            if (br.Value?.GetType().IsArray ?? false)
            {
                return new BorrowedReference(((Array)br.Value).GetValue(index));
            }

            return BorrowedReference.Null;
        }

        public static BorrowedReference
        PyList_GetItem(BorrowedReference br, nint index)
        {
            if (br.Value?.GetType().IsArray ?? false)
            {
                return new BorrowedReference(((Array)br.Value).GetValue(index));
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

        public static NewReference
        PyObject_GetIter(BorrowedReference br)
        {
            if (br.Value?.GetType().IsArray ?? false)
            {
                return new NewReference(((Array)br.Value).GetEnumerator());
            }

            return new NewReference(BorrowedReference.Null);
        }

        public static NewReference
        PyIter_Next(BorrowedReference br)
        {
            if (br.Value is IEnumerator enumerator)
            {
                enumerator.MoveNext();
                return new NewReference(enumerator.Current);
            }

            return new NewReference(BorrowedReference.Null);
        }
    }

    public class OriginalMethod : Attribute { }

    public class RedirectedMethod : Attribute { }

    public static class OperatorMethod
    {
        static readonly HashSet<string> s_opMap = new()
        {
            "op_Addition",
            "op_Subtraction",
            "op_Multiply",
            "op_Division",
            "op_Modulus",
            "op_BitwiseAnd",
            "op_BitwiseOr",
            "op_ExclusiveOr",
            "op_LeftShift",
            "op_RightShift",
            "op_OnesComplement",
            "op_UnaryNegation",
            "op_UnaryPlus",
            "__int__",
            "__float__",
            "__index__",
        };

        static readonly HashSet<string> s_compOpMap = new()
        {
            "op_Equality",
            "op_Inequality",
            "op_LessThanOrEqual",
            "op_GreaterThanOrEqual",
            "op_LessThan",
            "op_GreaterThan",
        };

        public static bool IsOperatorMethod(MethodBase method)
        {
            if (!method.IsSpecialName)
            {
                return false;
            }

            return s_opMap.Contains(method.Name)
                || s_compOpMap.Contains(method.Name);
        }

        public static bool IsComparisonOp(MethodBase method)
        {
            return s_compOpMap.Contains(method.Name);
        }

        public static bool IsReverse(MethodBase method)
        {
            Type leftOperandType = method.GetParameters()[0].ParameterType;
            return leftOperandType != method.DeclaringType;
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

            if (TryBind(methods, argsRef, kwargsRef, true,
                        out BindSpec? spec, out BindError? err))
            {
                if (spec!.TryGetArguments(instance,
                                          out MethodBase method,
                                          out object?[] arguments))
                {
                    try
                    {
                        object? result = method.Invoke(instance, arguments);

                        if (result is null)
                        {
                            return new NewReference(BorrowedReference.Null);
                        }
                        else
                        {
                            return new NewReference(result);
                        }
                    }
                    catch (Exception ex)
                    {
                        throw new MethodAccessException(
                                $"Error invoking {spec!.Method} | {ex}"
                            );
                    }
                }
                else
                {
                    throw new MethodAccessException(
                            $"Bad arguments for {spec!.Method}"
                        );
                }
            }

            throw new MethodAccessException(err!.ToString());
        }


        const string OP_IMPLICIT = "op_Implicit";
        const string OP_EXPLICIT = "op_Explicit";

        static readonly BindingFlags FLAGS = BindingFlags.Static
                                           | BindingFlags.Public
                                           | BindingFlags.NonPublic;

        static Type? GetCLRType(BorrowedReference br) => br.Value?.GetType();

        static bool TryGetManagedValue(BorrowedReference br,
                                       out object? value,
                                       bool setError = true)
        {
            return TryCast(br.Value, out value);
        }

        static bool TryGetManagedValue(BorrowedReference br, Type type,
                                       out object? value,
                                       bool setError = true)
        {
            return TryCast(br.Value, type, out value);
        }

        static bool TryCast(object? source, out object? cast)
        {
            cast = default;

            if (source is null)
            {
                return false;
            }

            cast = source;
            return true;
        }

        static bool TryCast(object? source, Type to, out object? cast)
        {
            cast = default;

            if (source is null)
            {
                return false;
            }

            Type from = source.GetType();

            if (from == to
                    || to.IsAssignableFrom(from))
            {
                cast = source;
                return true;
            }

            MethodInfo castMethod =
                GetCast(from, from, to) ?? // cast operator in from type
                GetCast(to, from, to);     // cast operator in to type

            if (castMethod != null)
            {
                cast = castMethod.Invoke(null, new[] { source });
                return true;
            }

            try
            {
                cast = Convert.ChangeType(source, to);
                return true;
            }
            catch { }

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
