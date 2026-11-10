using System;
using System.Runtime.Serialization;

namespace Python.Test
{
    // Fixtures for tests/test_pickle.py

    [Serializable]
    public struct SerializableStruct
    {
        public double X;
        public double Y;
        public SerializableStruct(double x, double y) { X = x; Y = y; }
    }

    [Serializable]
    public class SerializableClass
    {
        public string Name;
        public int Count;
        public SerializableStruct Point;
        public SerializableClass Child;
        public SerializableEnum Kind;
        [NonSerialized] public string Cache;
    }

    [Serializable]
    public class DerivedSerializableClass : SerializableClass
    {
        public double Extra;
    }

    [Serializable]
    public enum SerializableEnum
    {
        None,
        One,
        Two,
    }

    // mirrors RhinoCommon CommonObject: state is an opaque byte archive plus a nested object
    [Serializable]
    public class CustomSerializableClass : ISerializable
    {
        public byte[] Data;
        public SerializableStruct Point;
        public object Boxed;
        public bool WasDeserialized { get; private set; }

        public CustomSerializableClass() { }

        protected CustomSerializableClass(SerializationInfo info, StreamingContext context)
        {
            Data = (byte[])info.GetValue("data", typeof(byte[]));
            Point = (SerializableStruct)info.GetValue("point", typeof(SerializableStruct));
            Boxed = info.GetValue("boxed", typeof(object));
            WasDeserialized = true;
        }

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("data", Data);
            info.AddValue("point", Point);
            info.AddValue("boxed", Boxed, typeof(object));
        }
    }

    [Serializable]
    public struct CustomSerializableStruct : ISerializable
    {
        public double Value;
        public CustomSerializableStruct(double value) { Value = value; }

        CustomSerializableStruct(SerializationInfo info, StreamingContext context)
        {
            Value = info.GetDouble("value");
        }

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("value", Value);
        }
    }

    public class NonSerializableClass
    {
        public int Value;
    }
}
