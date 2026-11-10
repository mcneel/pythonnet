using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;

namespace Python.Runtime
{
    /// <summary>
    /// Saves and rebuilds serializable .NET objects for Python's pickle and copy.
    /// Called from <c>clr._extras.serialization</c> through <c>clr._pickle_state</c> and <c>clr._unpickle_state</c>.
    /// </summary>
    static class PickleSupport
    {
        const string KIND_SERIALIZABLE = "iserializable";
        const string KIND_FIELDS = "fields";
        const string KIND_ARRAY = "array";
        const string KIND_ENUM = "enum";

        const BindingFlags DECLARED_INSTANCE_FIELDS =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        const BindingFlags ANY_INSTANCE_CONSTRUCTOR =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        static readonly Type[] s_serializationConstructorArguments = { typeof(SerializationInfo), typeof(StreamingContext) };

        static readonly StreamingContext s_context = new(StreamingContextStates.All);

        // Built-in .NET types are saved without an assembly name,
        // so a pickle made on .NET Framework still loads on .NET and the other way round
        static readonly Assembly s_coreLibrary = typeof(object).Assembly;

        #region Internal

        /// <summary>True when objects of <paramref name="type"/> can be pickled.</summary>
        internal static bool IsPicklable(Type type)
        {
            // System.Object has nothing to save, and marking it would break Python's class ordering
            if (type == typeof(object))
            {
                return false;
            }

            // Exceptions already pickle their own way
            if (typeof(Exception).IsAssignableFrom(type))
            {
                return false;
            }

            // Delegates can't be saved on modern .NET
            if (typeof(Delegate).IsAssignableFrom(type))
            {
                return false;
            }

            if (type.IsSerializable)
            {
                return true;
            }

            return typeof(ISerializable).IsAssignableFrom(type);
        }

        /// <summary>Returns <c>(type_name, kind, state)</c> for <paramref name="obj"/>.</summary>
        internal static PyObject GetState(object obj)
        {
            Type type = obj.GetType();
            if (!IsPicklable(type))
            {
                throw TypeError($"cannot pickle '{type.FullName}' object: type is not serializable");
            }

            if (type.IsEnum)
            {
                return MakeState(type, KIND_ENUM, EnumToState(obj));
            }

            if (type.IsArray)
            {
                if (!IsSimpleArray(type))
                {
                    throw TypeError($"cannot pickle '{type.FullName}' object: only one-dimensional, zero-based arrays are supported");
                }

                return MakeState(type.GetElementType()!, KIND_ARRAY, ArrayToState((Array)obj));
            }

            if (obj is ISerializable serializable)
            {
                var info = new SerializationInfo(type, new FormatterConverter());
                serializable.GetObjectData(info, s_context);

                // Some types ask to be saved as a different type, so rebuild that one instead
                return MakeState(info.ObjectType, KIND_SERIALIZABLE, SerializationInfoToState(info));
            }

            return MakeState(type, KIND_FIELDS, FieldsToState(obj, type));
        }

        /// <summary>Rebuilds an object from what <see cref="GetState"/> returned.</summary>
        internal static object? FromState(string typeName, string kind, PyObject state)
        {
            Type type = ResolveType(typeName);

            switch (kind)
            {
                case KIND_ENUM:
                    return EnumFromState(type, state);

                case KIND_ARRAY:
                    return ArrayFromState(type, state);

                case KIND_SERIALIZABLE:
                    return SerializableFromState(type, typeName, state);

                case KIND_FIELDS:
                    return FieldsFromState(type, state);

                default:
                    throw TypeError($"cannot unpickle '{typeName}': unknown state kind '{kind}'");
            }
        }

        /// <summary>Type name with assembly but no version, e.g. <c>Rhino.Geometry.Point3d, RhinoCommon</c>.</summary>
        internal static string GetTypeName(Type type)
        {
            string name = GetNameWithoutAssembly(type);

            Assembly assembly = GetRootElementType(type).Assembly;
            if (assembly == s_coreLibrary)
            {
                return name;
            }

            return name + ", " + assembly.GetName().Name;
        }

        /// <summary>Finds a type saved by <see cref="GetTypeName"/> in the assemblies that are already loaded.</summary>
        internal static Type ResolveType(string name)
        {
            var lookup = new TypeLookup();
            Type? type = Type.GetType(name, lookup.FindAssembly, lookup.FindType, throwOnError: false);
            if (type is not null)
            {
                return type;
            }

            if (lookup.MissingAssembly is not null)
            {
                string message = string.Format(
                    "cannot unpickle .NET type '{0}': assembly '{1}' is not loaded. Load it first with clr.AddReference(\"{1}\")",
                    name,
                    lookup.MissingAssembly);
                throw TypeError(message);
            }

            throw TypeError($"cannot unpickle: .NET type '{name}' was not found");
        }

        #endregion

        #region Private

        static PyObject MakeState(Type type, string kind, PyObject state)
        {
            return new PyTuple(new[] { GetTypeName(type).ToPython(), kind.ToPython(), state });
        }

        // Enums are saved as their number
        static PyObject EnumToState(object value)
        {
            if (IsUnsigned(Enum.GetUnderlyingType(value.GetType())))
            {
                return Convert.ToUInt64(value).ToPython();
            }

            return Convert.ToInt64(value).ToPython();
        }

        static object EnumFromState(Type type, PyObject state)
        {
            if (IsUnsigned(Enum.GetUnderlyingType(type)))
            {
                return Enum.ToObject(type, state.As<ulong>());
            }

            return Enum.ToObject(type, state.As<long>());
        }

        // Arrays are saved as a list of their items
        static PyObject ArrayToState(Array array)
        {
            var items = new PyList();
            foreach (object? item in array)
            {
                using PyObject encoded = EncodeValue(item);
                items.Append(encoded);
            }
            return items;
        }

        static Array ArrayFromState(Type elementType, PyObject state)
        {
            using var items = new PyList(state);
            var array = Array.CreateInstance(elementType, (int)items.Length());
            for (int i = 0; i < array.Length; i++)
            {
                using PyObject item = items[i];
                array.SetValue(DecodeValue(item), i);
            }
            return array;
        }

        // ISerializable types are saved as the (name, declared type, value) entries they wrote in GetObjectData
        static PyObject SerializationInfoToState(SerializationInfo info)
        {
            var entries = new List<PyObject>();
            foreach (SerializationEntry entry in info)
            {
                PyObject name = entry.Name.ToPython();
                PyObject declaredType = GetTypeName(entry.ObjectType).ToPython();
                PyObject value = EncodeValue(entry.Value);
                entries.Add(new PyTuple(new[] { name, declaredType, value }));
            }
            return new PyTuple(entries.ToArray());
        }

        static object SerializableFromState(Type type, string typeName, PyObject state)
        {
            var info = new SerializationInfo(type, new FormatterConverter());
            var values = new Dictionary<string, object?>();
            foreach (PyObject item in new PyIterable(state))
            {
                using PyObject entry = item;
                using PyObject name = entry[0];
                using PyObject declaredType = entry[1];
                using PyObject value = entry[2];

                string entryName = name.As<string>();
                object? decoded = DecodeValue(value);
                info.AddValue(entryName, decoded, ResolveType(declaredType.As<string>()));
                values[entryName] = decoded;
            }

            ConstructorInfo? constructor = type.GetConstructor(
                ANY_INSTANCE_CONSTRUCTOR,
                binder: null,
                s_serializationConstructorArguments,
                modifiers: null);
            if (constructor is not null)
            {
                object rebuilt = constructor.Invoke(new object[] { info, s_context });
                return Complete(rebuilt);
            }

            // The type we were asked to rebuild has no special constructor.
            // That's fine for plain [Serializable] types: fill their fields by name, as .NET's old serializer did.
            bool isPlainSerializable = type.IsSerializable && !typeof(ISerializable).IsAssignableFrom(type);
            if (!isPlainSerializable)
            {
                throw TypeError($"cannot unpickle '{typeName}': missing (SerializationInfo, StreamingContext) constructor");
            }

            object obj = FormatterServices.GetUninitializedObject(type);
            foreach (FieldInfo field in GetSerializableFields(type))
            {
                if (values.TryGetValue(field.Name, out object? fieldValue))
                {
                    field.SetValue(obj, fieldValue);
                }
            }
            return Complete(obj);
        }

        // Plain [Serializable] types are saved as (field key, value) pairs
        static PyObject FieldsToState(object obj, Type type)
        {
            var entries = new List<PyObject>();
            foreach (FieldInfo field in GetSerializableFields(type))
            {
                PyObject key = GetFieldKey(field).ToPython();
                PyObject value = EncodeValue(field.GetValue(obj));
                entries.Add(new PyTuple(new[] { key, value }));
            }
            return new PyTuple(entries.ToArray());
        }

        static object FieldsFromState(Type type, PyObject state)
        {
            var fields = new Dictionary<string, FieldInfo>();
            foreach (FieldInfo field in GetSerializableFields(type))
            {
                fields[GetFieldKey(field)] = field;
            }

            object obj = FormatterServices.GetUninitializedObject(type);
            foreach (PyObject item in new PyIterable(state))
            {
                using PyObject entry = item;
                using PyObject key = entry[0];
                using PyObject value = entry[1];

                // A field that no longer exists in this version of the type is skipped
                if (fields.TryGetValue(key.As<string>(), out FieldInfo? field))
                {
                    field.SetValue(obj, DecodeValue(value));
                }
            }
            return Complete(obj);
        }

        // Runs the "finished loading" hooks some types have, the same ones .NET's own serializer runs
        static object Complete(object obj)
        {
            if (obj is IDeserializationCallback callback)
            {
                callback.OnDeserialization(null);
            }

            if (obj is IObjectReference reference)
            {
                return reference.GetRealObject(s_context);
            }

            return obj;
        }

        static IEnumerable<FieldInfo> GetSerializableFields(Type type)
        {
            // Walk up through parent classes, but stop at the first one that isn't serializable
            Type? current = type;
            while (current is not null && current.IsSerializable)
            {
                foreach (FieldInfo field in current.GetFields(DECLARED_INSTANCE_FIELDS))
                {
                    if (!field.IsNotSerialized)
                    {
                        yield return field;
                    }
                }

                current = current.BaseType;
            }
        }

        // Includes the class name, because a parent and child class can each have a private field with the same name
        static string GetFieldKey(FieldInfo field)
        {
            return field.DeclaringType!.Name + "." + field.Name;
        }

        // Saves each value with its type name, so a number saved as Int64 comes back as Int64 and not just "a number"
        static PyObject EncodeValue(object? value)
        {
            if (value is null)
            {
                return new PyTuple(new[] { string.Empty.ToPython(), PyObject.None });
            }

            PyObject typeName = GetTypeName(value.GetType()).ToPython();

            // Byte arrays (e.g. Rhino geometry data) become Python bytes, which is much faster than item by item
            if (value is byte[] bytes)
            {
                return new PyTuple(new[] { typeName, ToByteArray(bytes) });
            }

            return new PyTuple(new[] { typeName, value.ToPython() });
        }

        static object? DecodeValue(PyObject pair)
        {
            using PyObject typeName = pair[0];
            using PyObject value = pair[1];

            string name = typeName.As<string>();
            if (name.Length == 0)
            {
                return null;
            }

            // Pickle has already rebuilt nested .NET objects, maybe as a different type than they were saved as,
            // so use them as they are instead of converting again
            if (ManagedType.GetManagedObject(value) is CLRObject clrObject)
            {
                return clrObject.inst;
            }

            Type type = ResolveType(name);
            if (type == typeof(byte[]))
            {
                return FromBuffer(value);
            }

            return value.AsManagedObject(type);
        }

        static PyObject ToByteArray(byte[] bytes)
        {
            // Hold the array still in memory while Python copies it
            GCHandle handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                IntPtr start = handle.AddrOfPinnedObject();
                return Runtime.PyByteArray_FromStringAndSize(start, bytes.Length).MoveToPyObject();
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
        {
            return type == typeof(byte)
                || type == typeof(ushort)
                || type == typeof(uint)
                || type == typeof(ulong);
        }

        // True for normal arrays like int[], false for grids like int[,] or arrays that don't start at index 0
        static bool IsSimpleArray(Type type)
        {
            Type simpleArrayType = type.GetElementType()!.MakeArrayType();
            return type == simpleArrayType;
        }

        // Built by hand because .NET's own full names put assembly versions inside generic types like List<Point3d>
        static string GetNameWithoutAssembly(Type type)
        {
            if (type.IsArray)
            {
                string elementName = GetNameWithoutAssembly(type.GetElementType()!);
                if (IsSimpleArray(type))
                {
                    return elementName + "[]";
                }

                string commas = new string(',', type.GetArrayRank() - 1);
                return elementName + "[" + commas + "]";
            }

            if (type.IsGenericType && !type.IsGenericTypeDefinition)
            {
                var arguments = new List<string>();
                foreach (Type argument in type.GetGenericArguments())
                {
                    arguments.Add("[" + GetTypeName(argument) + "]");
                }

                return type.GetGenericTypeDefinition().FullName + "[" + string.Join(",", arguments) + "]";
            }

            if (type.FullName is not null)
            {
                return type.FullName;
            }

            return type.Name;
        }

        // Point3d[][] lives in the same assembly as Point3d
        static Type GetRootElementType(Type type)
        {
            while (type.HasElementType)
            {
                type = type.GetElementType()!;
            }
            return type;
        }

        // Raises a Python TypeError, which is what pickle and copy users expect to catch
        static Exception TypeError(string message)
        {
            Exceptions.SetError(Exceptions.TypeError, message);
            return PythonException.ThrowLastAsClrException();
        }

        /// <summary>
        /// Lookup callbacks for <see cref="Type.GetType(string, Func{AssemblyName, Assembly}, Func{Assembly, string, bool, Type}, bool)"/>.
        /// Remembers the first assembly that wasn't loaded, so the error can name it.
        /// </summary>
        sealed class TypeLookup
        {
            public string? MissingAssembly { get; private set; }

            // Never loads an assembly just because a pickle names it; the user loads it with clr.AddReference
            public Assembly? FindAssembly(AssemblyName assemblyName)
            {
                Assembly? assembly = AssemblyManager.FindLoadedAssembly(assemblyName.Name);
                if (assembly is null)
                {
                    assembly = FindInAppDomain(assemblyName.Name);
                }

                if (assembly is null && MissingAssembly is null)
                {
                    MissingAssembly = assemblyName.Name;
                }

                return assembly;
            }

            // A type name without an assembly is a built-in .NET type
            public Type? FindType(Assembly? assembly, string typeName, bool ignoreCase)
            {
                if (assembly is null)
                {
                    return Type.GetType(typeName, throwOnError: false, ignoreCase);
                }

                return assembly.GetType(typeName, throwOnError: false, ignoreCase);
            }

            static Assembly? FindInAppDomain(string? name)
            {
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.GetName().Name == name)
                    {
                        return assembly;
                    }
                }

                return null;
            }
        }

        #endregion
    }
}
