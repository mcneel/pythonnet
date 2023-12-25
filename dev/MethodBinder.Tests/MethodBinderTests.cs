using System;

using Python.Runtime;

namespace MethodBinderTests
{
    using KeywordArgs = Dictionary<string, object?>;

    public class Tests
    {
        #region Fields
        const string FOO = "Foo";

#pragma warning disable IDE0051 // Remove unused private members
        const string TEST = "TEST";
#pragma warning restore IDE0051 // Remove unused private members

        static readonly TargetTypeA A = new();
        static readonly TargetTypeB B = new();

        static readonly object[] NoArgs = Array.Empty<object>();
        static readonly KeywordArgs NoKwargs = new();
        #endregion

        [Test]
        public void TestFooParam_0()
        {
            object[] args;
            Guid t;
            NewReference r;

            // Foo()
            args = new object[0];
            t = A.Foo();
            r = MethodBinder.Invoke(A, FOO, args, NoKwargs);
            Assert.That(t, Is.EqualTo(r.Value));
        }

        [Test]
        public void TestFooParam_1()
        {
            object[] args;
            Guid t;
            NewReference r;

            // Foo(int _1)
            args = new object[] { 0 };
            t = A.Foo(0);
            r = MethodBinder.Invoke(A, FOO, args, NoKwargs);
            Assert.That(t, Is.EqualTo(r.Value));

            // Foo(double _1)
            args = new object[] { 0.0F };
            t = A.Foo(0.0F);
            r = MethodBinder.Invoke(A, FOO, args, NoKwargs);
            Assert.That(t, Is.EqualTo(r.Value));

            // Foo(double[] _1)
            args = new object[] { new double[] { 0.0D } };
            t = A.Foo(new double[] { 0.0D });
            r = MethodBinder.Invoke(A, FOO, args, NoKwargs);
            Assert.That(t, Is.EqualTo(r.Value));

            // Foo(double[] _1)
            var kwargs = new KeywordArgs
            {
                ["_1"] = 0.0F,
            };
            t = A.Foo(_1: 0.0F);
            r = MethodBinder.Invoke(A, FOO, NoArgs, kwargs);
            Assert.That(t, Is.EqualTo(r.Value));
        }

        [Test]
        public void TestFooParam_2()
        {
            object[] args;
            Guid t;
            NewReference r;

            // Foo(int _1, int _2)
            args = new object[] { 0, 1 };
            t = A.Foo(0, 1);
            r = MethodBinder.Invoke(A, FOO, args, NoKwargs);
            Assert.That(t, Is.EqualTo(r.Value));

            // Foo(float _1, float _2)
            args = new object[] { 0.0F, 1.0F };
            t = A.Foo(0.0F, 1.0F);
            r = MethodBinder.Invoke(A, FOO, args, NoKwargs);
            Assert.That(t, Is.EqualTo(r.Value));

            // Foo(int _1, bool _2 = false)
            args = new object[] { 0, false };
            t = A.Foo(0, false);
            r = MethodBinder.Invoke(A, FOO, args, NoKwargs);
            Assert.That(t, Is.EqualTo(r.Value));

            // Foo([Optional, DefaultParameterValue(12)] nint _1, uint _2)
            args = new object[] { IntPtr.Zero, 0 };
            t = A.Foo(IntPtr.Zero, 0);
            r = MethodBinder.Invoke(A, FOO, args, NoKwargs);
            Assert.That(t, Is.EqualTo(r.Value));

            // Foo([Optional, DefaultParameterValue(12)] nint _1, uint _2)
            args = new object[] { 0 };
            var kwargs = new KeywordArgs
            {
                ["_1"] = IntPtr.Zero,
            };
            t = A.Foo(_1: IntPtr.Zero, 0);
            r = MethodBinder.Invoke(A, FOO, args, kwargs);
            Assert.That(t, Is.EqualTo(r.Value));

            // Foo(float _1, float _2)
            args = new object[] { 0, 1 };
            t = B.Foo(0, 1);
            r = MethodBinder.Invoke(B, FOO, args, NoKwargs);
            Assert.That(t, Is.EqualTo(r.Value));
        }
    }
}
