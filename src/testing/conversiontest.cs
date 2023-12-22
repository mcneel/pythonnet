#pragma warning disable IDE0090 // Use 'new(...)'

namespace Python.Test
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Supports unit tests for field access.
    /// </summary>
    public class ConversionTest
    {
        public ConversionTest()
        {
            EnumField = ShortEnum.Zero;
            SpamField = new Spam("spam");
            StringField = "spam";
        }

        public bool BooleanField = false;
        public byte ByteField = 0;
        public sbyte SByteField = 0;
        public char CharField = 'A';
        public short Int16Field = 0;
        public int Int32Field = 0;
        public long Int64Field = 0;
        public ushort UInt16Field = 0;
        public uint UInt32Field = 0;
        public ulong UInt64Field = 0;
        public float SingleField = 0.0F;
        public double DoubleField = 0.0;
        public IntPtr IntPtrField = IntPtr.Zero;
        public UIntPtr UIntPtrField = UIntPtr.Zero;
        public decimal DecimalField = 0;
        public string StringField;
        public ShortEnum EnumField;
        public object ObjectField = null;
        public ISpam SpamField;

        public byte[] ByteArrayField;
        public sbyte[] SByteArrayField;
        public readonly List<int> ListField = new List<int>();

#pragma warning disable CA1822 // Mark members as static
        public T? Echo<T>(T? arg) where T : struct
        {
            return arg;
        }
#pragma warning restore CA1822 // Mark members as static
    }



    public interface ISpam
    {
        string GetValue();
    }

    public class Spam : ISpam
    {
        private readonly string value;

        public Spam(string value)
        {
            this.value = value;
        }

        public string GetValue()
        {
            return value;
        }
    }

    public class UnicodeString
    {
        public string value = "안녕";

        public string GetString()
        {
            return value;
        }

        public override string ToString()
        {
            return value;
        }
    }

#pragma warning disable CA1822 // Mark members as static
    public class MethodResolutionInt
    {
        public IEnumerable<byte> MethodA(ulong address, int size)
        {
            return new byte[10];
        }

        public int MethodA(string dummy, ulong address, int size)
        {
            return 0;
        }
    }
#pragma warning restore CA1822 // Mark members as static

    public class ImplicitCastTypeA
    {
        public ImplicitCastTypeA(int value) => Value = value;
        public int Value { get; }

        public static implicit operator ImplicitCastTypeB(ImplicitCastTypeA d) => new ImplicitCastTypeB(d.Value);
        public static bool operator ==(ImplicitCastTypeA a, ImplicitCastTypeA b) => a.Value == b.Value;
        public static bool operator !=(ImplicitCastTypeA a, ImplicitCastTypeA b) => a.Value != b.Value;
        public static bool operator ==(ImplicitCastTypeA a, ImplicitCastTypeB b) => a.Value == b.Value;
        public static bool operator !=(ImplicitCastTypeA a, ImplicitCastTypeB b) => a.Value != b.Value;
        public static ImplicitCastTypeA operator +(ImplicitCastTypeA a, ImplicitCastTypeB b) => new ImplicitCastTypeA(a.Value + b.Value);

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(this, obj))
            {
                return true;
            }

            if (obj is null)
            {
                return false;
            }

            throw new NotImplementedException();
        }

        public override int GetHashCode()
        {
            throw new NotImplementedException();
        }
    }

    public class ImplicitCastTypeB
    {
        public ImplicitCastTypeB(int value) => Value = value;
        public int Value { get; }

        public static implicit operator ImplicitCastTypeA(ImplicitCastTypeB d) => new ImplicitCastTypeA(d.Value);
        public static bool operator ==(ImplicitCastTypeB a, ImplicitCastTypeB b) => a.Value == b.Value;
        public static bool operator !=(ImplicitCastTypeB a, ImplicitCastTypeB b) => a.Value != b.Value;
        public static bool operator ==(ImplicitCastTypeB a, ImplicitCastTypeA b) => a.Value == b.Value;
        public static bool operator !=(ImplicitCastTypeB a, ImplicitCastTypeA b) => a.Value != b.Value;
        public static ImplicitCastTypeB operator +(ImplicitCastTypeB a, ImplicitCastTypeA b) => new ImplicitCastTypeB(a.Value + b.Value);

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(this, obj))
            {
                return true;
            }

            if (obj is null)
            {
                return false;
            }

            throw new NotImplementedException();
        }

        public override int GetHashCode()
        {
            throw new NotImplementedException();
        }
    }
}
