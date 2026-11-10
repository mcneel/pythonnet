using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;

namespace Python.Runtime
{
    /// <summary>
    /// Pickle state for serializable .NET objects. Used by <c>clr._extras.serialization</c>
    /// through <c>clr._pickle_state</c> and <c>clr._unpickle_state</c>.
    /// </summary>
    static class PickleSupport
    {
        const string KindSerializable = "iserializable";
        const string KindFields = "fields";
        const string KindArray = "array";
        const string KindEnum = "enum";

        const BindingFlags DeclaredInstanceFields =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        static readonly StreamingContext Context = new(StreamingContextStates.All);

        // core library types are written without assembly so pickles move between .NET Framework and .NET
        static readonly Assembly CoreLibrary = typeof(object).Assembly;

        /// <summary>True when instances of <paramref name="type"/> can be pickled.</summary>
        internal static bool IsPicklable(Type type)
        {
            // System.Object is [Serializable] on .NET but has no state, and sits above Python's object in the MRO.
            // exceptions already pickle through BaseException, delegates do not serialize on .NET
            if (type == typeof(object)
                || typeof(Exception).IsAssignableFrom(type)
                || typeof(Delegate).IsAssignableFrom(type))
            {
                return false;
            }

            return type.IsSerializable || typeof(ISerializable).IsAssignableFrom(type);
        }

        /// <summary>Returns <c>(type_name, kind, state)</c> for <paramref name="obj"/>.</summary>
        internal static PyObject GetState(object obj)
        {
            Type type = obj.GetType();
            if (!IsPicklable(type))
            {
                throw TypeError($"cannot pickle '{type.FullName}' object: type is not serializable");
            }

            string kind;
            PyObject state;

            if (type.IsEnum)
            {
                kind = KindEnum;
                state = IsUnsigned(Enum.GetUnderlyingType(type))
                    ? Convert.ToUInt64(obj).ToPython()
                    : Convert.ToInt64(obj).ToPython();
            }
            else if (type.IsArray)
            {
                if (type.GetArrayRank() != 1 || type != type.GetElementType()!.MakeArrayType())
                {
                    throw TypeError($"cannot pickle '{type.FullName}' object: only one-dimensional, zero-based arrays are supported");
                }

                kind = KindArray;
                type = type.GetElementType()!;
                var items = ((Array)obj).Cast<object?>().Select(EncodeValue).ToArray();
                state = new PyList(items);
            }
            else if (obj is ISerializable serializable)
            {
                kind = KindSerializable;
                var info = new SerializationInfo(type, new FormatterConverter());
                serializable.GetObjectData(info, Context);

                // GetObjectData may redirect to a different type (e.g. a serialization holder)
                type = info.ObjectType;

                var entries = new List<PyObject>();
                foreach (SerializationEntry entry in info)
                {
                    entries.Add(new PyTuple(new[]
                    {
                        entry.Name.ToPython(),
                        GetTypeName(entry.ObjectType).ToPython(),
                        EncodeValue(entry.Value),
                    }));
                }
                state = new PyTuple(entries.ToArray());
            }
            else
            {
                kind = KindFields;
                var entries = new List<PyObject>();
                foreach (FieldInfo field in GetSerializableFields(type))
                {
                    entries.Add(new PyTuple(new[]
                    {
                        GetFieldKey(field).ToPython(),
                        EncodeValue(field.GetValue(obj)),
                    }));
                }
                state = new PyTuple(entries.ToArray());
            }

            return new PyTuple(new[] { GetTypeName(type).ToPython(), kind.ToPython(), state });
        }

        /// <summary>Rebuilds the object described by <see cref="GetState"/>.</summary>
        internal static object? FromState(string typeName, string kind, PyObject state)
        {
            Type type = ResolveType(typeName);

            switch (kind)
            {
                case KindEnum:
                    return Enum.ToObject(type, IsUnsigned(Enum.GetUnderlyingType(type))
                        ? state.As<ulong>()
                        : (object)state.As<long>());

                case KindArray:
                {
                    var items = new PyList(state);
                    var array = Array.CreateInstance(type, (int)items.Length());
                    for (int i = 0; i < array.Length; i++)
                    {
                        using var item = items[i];
                        array.SetValue(DecodeValue(item), i);
                    }
                    return array;
                }

                case KindSerializable:
                {
                    var info = new SerializationInfo(type, new FormatterConverter());
                    var values = new Dictionary<string, object?>();
                    foreach (PyObject entry in new PyIterable(state))
                    {
                        using var name = entry[0];
                        using var declaredType = entry[1];
                        using var value = entry[2];
                        string entryName = name.As<string>();
                        object? decoded = DecodeValue(value);
                        info.AddValue(entryName, decoded, ResolveType(declaredType.As<string>()));
                        values[entryName] = decoded;
                        entry.Dispose();
                    }

                    ConstructorInfo? ctor = type.GetConstructor(
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        binder: null,
                        new[] { typeof(SerializationInfo), typeof(StreamingContext) },
                        modifiers: null);
                    if (ctor is not null)
                    {
                        return Complete(ctor.Invoke(new object[] { info, Context }));
                    }

                    // GetObjectData redirected to a plain [Serializable] type: fill its fields by name, like BinaryFormatter
                    if (!type.IsSerializable || typeof(ISerializable).IsAssignableFrom(type))
                    {
                        throw TypeError($"cannot unpickle '{typeName}': missing (SerializationInfo, StreamingContext) constructor");
                    }

                    object target = FormatterServices.GetUninitializedObject(type);
                    foreach (FieldInfo field in GetSerializableFields(type))
                    {
                        if (values.TryGetValue(field.Name, out object? fieldValue))
                        {
                            field.SetValue(target, fieldValue);
                        }
                    }
                    return Complete(target);
                }

                case KindFields:
                {
                    var fields = GetSerializableFields(type).ToDictionary(GetFieldKey);
                    object obj = FormatterServices.GetUninitializedObject(type);
                    foreach (PyObject entry in new PyIterable(state))
                    {
                        using var key = entry[0];
                        using var value = entry[1];
                        // fields missing from this version of the type are dropped
                        if (fields.TryGetValue(key.As<string>(), out FieldInfo? field))
                        {
                            field.SetValue(obj, DecodeValue(value));
                        }
                        entry.Dispose();
                    }
                    return Complete(obj);
                }

                default:
                    throw TypeError($"cannot unpickle '{typeName}': unknown state kind '{kind}'");
            }
        }

        static object Complete(object obj)
        {
            if (obj is IDeserializationCallback callback)
            {
                callback.OnDeserialization(null);
            }

            return obj is IObjectReference reference ? reference.GetRealObject(Context) : obj;
        }

        static IEnumerable<FieldInfo> GetSerializableFields(Type type)
        {
            // stop at the first base that is not itself serializable, like BinaryFormatter
            for (Type? t = type; t is not null && t.IsSerializable; t = t.BaseType)
            {
                foreach (FieldInfo field in t.GetFields(DeclaredInstanceFields))
                {
                    if (!field.IsNotSerialized)
                    {
                        yield return field;
                    }
                }
            }
        }

        // qualified by declaring type so same-named private fields in base classes do not collide
        static string GetFieldKey(FieldInfo field) => field.DeclaringType!.Name + "." + field.Name;

        // value is (type_name, python_value) so primitives come back as their original .NET type
        static PyObject EncodeValue(object? value)
        {
            if (value is null)
            {
                return new PyTuple(new[] { string.Empty.ToPython(), PyObject.None });
            }

            PyObject encoded = value is byte[] bytes ? ToByteArray(bytes) : value.ToPython();
            return new PyTuple(new[] { GetTypeName(value.GetType()).ToPython(), encoded });
        }

        static object? DecodeValue(PyObject pair)
        {
            using var typeName = pair[0];
            using var value = pair[1];

            string name = typeName.As<string>();
            if (name.Length == 0)
            {
                return null;
            }

            // .NET objects were rebuilt by their own reducer, possibly as a redirected type, so keep them as is
            if (ManagedType.GetManagedObject(value) is CLRObject clrObject)
            {
                return clrObject.inst;
            }

            Type type = ResolveType(name);
            return type == typeof(byte[]) ? FromBuffer(value) : value.AsManagedObject(type);
        }

        static PyObject ToByteArray(byte[] bytes)
        {
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                return Runtime.PyByteArray_FromStringAndSize(handle.AddrOfPinnedObject(), bytes.Length).MoveToPyObject();
            }
            finally
            {
                handle.Free();
            }
        }

        static byte[] FromBuffer(PyObject value)
        {
            using PyBuffer buffer = value.GetBuffer();
            var bytes = new byte[buffer.Length];
            buffer.Read(bytes, 0, bytes.Length, 0);
            return bytes;
        }

        static bool IsUnsigned(Type type)
            => type == typeof(byte) || type == typeof(ushort) || type == typeof(uint) || type == typeof(ulong);

        /// <summary>Type name with assembly but no version, e.g. <c>Rhino.Geometry.Point3d, RhinoCommon</c>.</summary>
        internal static string GetTypeName(Type type)
        {
            string name = GetNameWithoutAssembly(type);
            Assembly assembly = GetRootElementType(type).Assembly;
            return assembly == CoreLibrary ? name : name + ", " + assembly.GetName().Name;
        }

        static string GetNameWithoutAssembly(Type type)
        {
            if (type.IsArray)
            {
                Type element = type.GetElementType()!;
                int rank = type.GetArrayRank();
                string suffix = type == element.MakeArrayType() ? "[]" : "[" + new string(',', rank - 1) + "]";
                return GetNameWithoutAssembly(element) + suffix;
            }

            if (type.IsGenericType && !type.IsGenericTypeDefinition)
            {
                string arguments = string.Join(",", type.GetGenericArguments().Select(a => "[" + GetTypeName(a) + "]"));
                return type.GetGenericTypeDefinition().FullName + "[" + arguments + "]";
            }

            return type.FullName ?? type.Name;
        }

        static Type GetRootElementType(Type type)
        {
            while (type.HasElementType)
            {
                type = type.GetElementType()!;
            }
            return type;
        }

        internal static Type ResolveType(string name)
        {
            Type? type = Type.GetType(
                name,
                assemblyName => AssemblyManager.FindLoadedAssembly(assemblyName.Name)
                                ?? FindInAppDomain(assemblyName.Name)
                                ?? Assembly.Load(new AssemblyName(assemblyName.Name)),
                (assembly, typeName, ignoreCase) => assembly is null
                    ? Type.GetType(typeName, throwOnError: false, ignoreCase)
                    : assembly.GetType(typeName, throwOnError: false, ignoreCase),
                throwOnError: false);

            return type ?? throw TypeError($"cannot unpickle: .NET type '{name}' was not found");
        }

        static Assembly? FindInAppDomain(string? name)
            => AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);

        static Exception TypeError(string message)
        {
            Exceptions.SetError(Exceptions.TypeError, message);
            return PythonException.ThrowLastAsClrException();
        }
    }
}
