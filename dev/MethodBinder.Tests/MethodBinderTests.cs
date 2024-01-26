using System;
using System.Numerics;

using Python.Runtime;

using BINDER = Python.Runtime.MethodBinder;

namespace MethodBinder.Tests
{
    using KeywordArgs = Dictionary<string, object?>;

    public abstract class InvokeTests
    {
        #region Fields
        protected const string M = "Foo";

#pragma warning disable IDE0051 // Remove unused private members
        protected const string TEST = "TEST";
#pragma warning restore IDE0051 // Remove unused private members

        protected static readonly TargetTypeA A = new();
        protected static readonly TargetTypeB B = new();
        protected static readonly TargetTypeC C = new();
        protected static readonly TargetTypeD D = new();
        protected static readonly TargetTypeE E = new();
        protected static readonly TargetTypeF F = new();

        protected static readonly object[] NoArgs = Array.Empty<object>();
        protected static readonly KeywordArgs NoKwargs = new();
        #endregion
    }

    public sealed class InvokeTests_0 : InvokeTests
    {
        [Test]
        public void TestFooParam_0()
        {
            // Foo()
            object[] args = Array.Empty<object>();
            Guid t = A.Foo();
            NewReference r = BINDER.Invoke(A, M, args, NoKwargs);
            Assert.That(r.Value, Is.EqualTo(t));
        }

        [Test]
        public void TestFooParam_0_Missing()
        {
            // Missing Foo()
            Assert.Throws<MethodAccessException>(() =>
            {
                object[] args = Array.Empty<object>();
                NewReference r = BINDER.Invoke(E, M, args, NoKwargs);
            });
        }

    }

    public sealed class InvokeTests_1 : InvokeTests
    {
        [Test]
        public void TestFooParam_1_ShortAsGeneric()
        {
            // Foo(int _1)
            object[] args = new object[] { (ushort)0 };
            Guid t = A.Foo((ushort)0);
            NewReference r = BINDER.Invoke(A, M, args, NoKwargs);
            Assert.That(r.Value, Is.EqualTo(t));
        }

        [Test]
        public void TestFooParam_1_Int()
        {
            // Foo(int _1)
            object[] args = new object[] { 0 };
            Guid t = A.Foo(0);
            NewReference r = BINDER.Invoke(A, M, args, NoKwargs);
            Assert.That(r.Value, Is.EqualTo(t));
        }

        [Test]
        public void TestFooParam_1_Int_OptionalDouble()
        {
            // Foo(int _1, double _2 = 0)
            object[] args = new object[] { 0 };
            Guid t = C.Foo(0);
            NewReference r = BINDER.Invoke(C, M, args, NoKwargs);
            Assert.That(r.Value, Is.EqualTo(t));
        }

        [Test]
        public void TestFooParam_1_Byte()
        {
            // Foo(double _1)
            object[] args = new object[] { (byte)0xFF };
            Guid t = A.Foo((byte)0xFF);
            NewReference r = BINDER.Invoke(A, M, args, NoKwargs);
            Assert.That(r.Value, Is.EqualTo(t));
        }

        [Test]
        public void TestFooParam_1_String()
        {
            // Foo(double _1)
            object[] args = new object[] { "Test" };
            Guid t = A.Foo("Test");
            NewReference r = BINDER.Invoke(A, M, args, NoKwargs);
            Assert.That(r.Value, Is.EqualTo(t));
        }

        [Test]
        public void TestFooParam_1_Float()
        {
            // Foo(float _1)
            object[] args = new object[] { 0.0F };
            Guid t = B.Foo(0.0F);
            NewReference r = BINDER.Invoke(B, M, args, NoKwargs);
            Assert.That(r.Value, Is.EqualTo(t));
        }

        [Test]
        public void TestFooParam_1_FloatAsDouble()
        {
            // Foo(double _1)
            object[] args = new object[] { 0.0F };
            Guid t = A.Foo(0.0F);
            NewReference r = BINDER.Invoke(A, M, args, NoKwargs);
            Assert.That(r.Value, Is.EqualTo(t));
        }

        [Test]
        public void TestFooParam_1_Double()
        {
            // Foo(double _1)
            object[] args = new object[] { 0.0D };
            Guid t = C.Foo(0.0D);
            NewReference r = BINDER.Invoke(C, M, args, NoKwargs);
            Assert.That(r.Value, Is.EqualTo(t));
        }

        [Test]
        public void TestFooParam_1_DoubleArray()
        {
            // Foo(double[] _1)
            object[] args = new object[] { new double[] { 0.0D } };
            Guid t = A.Foo(new double[] { 0.0D });
            NewReference r = BINDER.Invoke(A, M, args, NoKwargs);
            Assert.That(r.Value, Is.EqualTo(t));
        }

        [Test]
        public void TestFooParam_1_Double_Kwarg()
        {
            // Foo(double _1)
            var kwargs = new KeywordArgs
            {
                ["_1"] = 0.0F,
            };
            Guid t = A.Foo(_1: 0.0F);
            NewReference r = BINDER.Invoke(A, M, NoArgs, kwargs);
            Assert.That(r.Value, Is.EqualTo(t));
        }

        [Test]
        public void TestFooParam_1_DataA()
        {
            // Foo(DataA _1)
            object[] args = new object[] { new DataA() };
            Guid t = B.Foo(new DataA());
            NewReference r = BINDER.Invoke(B, M, args, NoKwargs);
            Assert.That(r.Value, Is.EqualTo(t));
        }

        [Test]
        public void TestFooParam_1_DataB()
        {
            // Foo(DataB _1)
            object[] args = new object[] { new DataB() };
            Guid t = B.Foo(new DataB());
            NewReference r = BINDER.Invoke(B, M, args, NoKwargs);
            Assert.That(r.Value, Is.EqualTo(t));
        }

        [Test]
        public void TestFooParam_1_DataC()
        {
            // Foo(DataB _1)
            object[] args = new object[] { new DataC() };
            Guid t = B.Foo(new DataC());
            NewReference r = BINDER.Invoke(B, M, args, NoKwargs);
            Assert.That(r.Value, Is.EqualTo(t));
        }
    }

    public sealed class InvokeTests_2 : InvokeTests
    {
        [Test]
        public void TestFooParam_2_Int_Int()
        {
            // Foo(int _1, int _2)
            object[] args = new object[] { 0, 1 };
            Guid t = A.Foo(0, 1);
            NewReference r = BINDER.Invoke(A, M, args, NoKwargs);
            Assert.That(r.Value, Is.EqualTo(t));
        }

        [Test]
        public void TestFooParam_2_Float_Float()
        {
            // Foo(float _1, float _2)
            object[] args = new object[] { 0.0F, 1.0F };
            Guid t = A.Foo(0.0F, 1.0F);
            NewReference r = BINDER.Invoke(A, M, args, NoKwargs);
            Assert.That(r.Value, Is.EqualTo(t));
        }

        [Test]
        public void TestFooParam_2_Int_OptionalBool()
        {
            // Foo(int _1, bool _2 = false)
            object[] args = new object[] { 0, false };
            Guid t = A.Foo(0, false);
            NewReference r = BINDER.Invoke(A, M, args, NoKwargs);
            Assert.That(r.Value, Is.EqualTo(t));
        }

        [Test]
        public void TestFooParam_2_OptionalNInt_UInt()
        {
            // Foo([Optional, DefaultParameterValue(12)] nint _1, uint _2)
            object[] args = new object[] { IntPtr.Zero, 0 };
            Guid t = A.Foo(IntPtr.Zero, 0);
            NewReference r = BINDER.Invoke(A, M, args, NoKwargs);
            Assert.That(r.Value, Is.EqualTo(t));
        }

        [Test]
        public void TestFooParam_2_OptionalNInt_UInt_Kwarg()
        {
            // Foo([Optional, DefaultParameterValue(12)] nint _1, uint _2)
            object[] args = new object[] { 0 };
            var kwargs = new KeywordArgs
            {
                ["_1"] = IntPtr.Zero,
            };
            Guid t = A.Foo(_1: IntPtr.Zero, 0);
            NewReference r = BINDER.Invoke(A, M, args, kwargs);
            Assert.That(r.Value, Is.EqualTo(t));
        }

        [Test]
        public void TestFooParam_2_IntAsFloat_IntAsFloat()
        {
            // Foo(float _1, float _2)
            object[] args = new object[] { 0, 1 };
            Guid t = B.Foo(0, 1);
            NewReference r = BINDER.Invoke(B, M, args, NoKwargs);
            Assert.That(r.Value, Is.EqualTo(t));
        }

        [Test]
        public void TestFooParam_2_FloatArray_FloatArray()
        {
            // Foo(float[] _1, float[] _2)
            object[] args = new object[] {
                new float[] { 0.0F },
                new float[] { 1.0F },
            };
            Guid t = B.Foo(new float[] { 0.0F }, new float[] { 1.0F });
            NewReference r = BINDER.Invoke(B, M, args, NoKwargs);
            Assert.That(r.Value, Is.EqualTo(t));
        }
    }

    public sealed class InvokeTests_2_Ambig : InvokeTests
    {
        [Test]
        public void TestFooParam_2_Int16_Int32()
        {
            // Foo(short _1, short _2)
            object[] args = new object[] { (short)0, (int)1 };
            Guid t = D.Foo((short)0, (short)1);
            NewReference r = BINDER.Invoke(D, M, args, NoKwargs);
            Assert.That(r.Value, Is.EqualTo(t));
        }

        [Test]
        public void TestFooParam_2_Int64_Int32()
        {
            // Foo(long _1, long _2)
            object[] args = new object[] { (long)0, 1 };
            Guid t = D.Foo((long)0, 1);
            NewReference r = BINDER.Invoke(D, M, args, NoKwargs);
            Assert.That(r.Value, Is.EqualTo(t));
        }
    }

    public sealed class InvokeTests_3 : InvokeTests
    {
        [Test]
        public void TestFooParam_3()
        {
            // Foo()
            object[] args = new object[] { Array.Empty<byte>(), 5, 5 };
            Guid t = D.Foo(Array.Empty<byte>(), 5, 5);
            NewReference r = BINDER.Invoke(D, M, args, NoKwargs);
            Assert.That(r.Value, Is.EqualTo(t));
        }
    }


    public sealed class InvokeTests_N : InvokeTests
    {
        [Test]
        public void TestFooParam_X_FiveInt()
        {
            // Foo(params int[] _2)
            object[] args = new object[] { 0, 1, 2, 3, 4 };
            Guid t = A.Foo(0, 1, 2, 3, 4, 5);
            NewReference r = BINDER.Invoke(A, M, args, NoKwargs);
            Assert.That(r.Value, Is.EqualTo(t));
        }

        [Test]
        public void TestFooParam_X_FiveIntAsArray()
        {
            // Foo(params int[] _2)
            object[] args = new object[] { new int[] { 0, 1, 2, 3, 4 } };
            Guid t = A.Foo(0, 1, 2, 3, 4, 5);
            NewReference r = BINDER.Invoke(A, M, args, NoKwargs);
            Assert.That(r.Value, Is.EqualTo(t));
        }

        [Test]
        public void TestFooParam_X_Byte_TwoInt()
        {
            // Foo(byte _1, params int[] _2)
            object[] args = new object[] { (byte)0xFF, 0, 1 };
            Guid t = A.Foo((byte)0xFF, 0, 1);
            NewReference r = BINDER.Invoke(A, M, args, NoKwargs);
            Assert.That(r.Value, Is.EqualTo(t));
        }

        [Test]
        public void TestFooParam_X_Object_TwoInt()
        {
            // Foo(object _1, params int[] _2)
            object[] args = new object[] { new DataA(), 0, 1 };
            Guid t = C.Foo(new DataA(), 0, 1);
            NewReference r = BINDER.Invoke(C, M, args, NoKwargs);
            Assert.That(r.Value, Is.EqualTo(t));
        }

        [Test]
        public void TestFooParam_X_Object_TwoIntAsArray()
        {
            // Foo(object _1, params int[] _2)
            object[] args = new object[] { new DataA(), new int[] { 0, 1 } };
            Guid t = C.Foo(new DataA(), 0, 1);
            NewReference r = BINDER.Invoke(C, M, args, NoKwargs);
            Assert.That(r.Value, Is.EqualTo(t));
        }

        [Test]
        public void TestFooParam_X_OutSignatures()
        {
            // Foo(object _1, params int[] _2)
            object[] args = new object[] { 0.0F };
            Guid t = F.Foo(out Complex _, 0.0F);
            NewReference r = BINDER.Invoke(F, M, args, NoKwargs);
            Assert.That(r.Value, Is.EqualTo(t));
        }
    }
}


/*
 * UNUSED GUIDS
return new Guid("5755b90e-a11d-11ee-8c90-0242ac120002");
return new Guid("5755ba1c-a11d-11ee-8c90-0242ac120002");
return new Guid("5755bed6-a11d-11ee-8c90-0242ac120002");
return new Guid("5755c016-a11d-11ee-8c90-0242ac120002");
*/
