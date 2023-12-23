using System;

using Python.Runtime;

namespace MethodBinderTests
{
    using KeywordArgs = Dictionary<string, object?>;

    public class Tests
    {
        #region Fields
        const string M = "Foo";

        static readonly TargetType I = new();

        static readonly object[] NoArgs = Array.Empty<object>();
        static readonly KeywordArgs NoKwargs = new();
        #endregion

        [Test]
        public void TestFooParam_0()
        {
            object[] args;
            Guid t;
            Guid r;

            // Foo()
            args = new object[0];
            t = I.Foo();
            r = MethodInvoke.Invoke<Guid>(I, M, args, NoKwargs);
            Assert.That(t, Is.EqualTo(r));
        }

        [Test]
        public void TestFooParam_1()
        {
            object[] args;
            Guid t;
            Guid r;

            // Foo(int _1)
            args = new object[] { 0 };
            t = I.Foo(0);
            r = MethodInvoke.Invoke<Guid>(I, M, args, NoKwargs);
            Assert.That(t, Is.EqualTo(r));

            // Foo(double _1)
            args = new object[] { 0.0F };
            t = I.Foo(0.0F);
            r = MethodInvoke.Invoke<Guid>(I, M, args, NoKwargs);
            Assert.That(t, Is.EqualTo(r));

            // Foo(double[] _1)
            args = new object[] { new double[] { 0.0D } };
            t = I.Foo(new double[] { 0.0D });
            r = MethodInvoke.Invoke<Guid>(I, M, args, NoKwargs);
            Assert.That(t, Is.EqualTo(r));

            // Foo(double[] _1)
            var kwargs = new KeywordArgs
            {
                ["_1"] = 0.0F,
            };
            t = I.Foo(_1: 0.0F);
            r = MethodInvoke.Invoke<Guid>(I, M, NoArgs, kwargs);
            Assert.That(t, Is.EqualTo(r));
        }

        [Test]
        public void TestFooParam_2()
        {
            object[] args;
            Guid t;
            Guid r;

            // Foo(int _1, int _2)
            args = new object[] { 0, 1 };
            t = I.Foo(0, 1);
            r = MethodInvoke.Invoke<Guid>(I, M, args, NoKwargs);
            Assert.That(t, Is.EqualTo(r));

            // Foo(float _1, float _2)
            args = new object[] { 0.0F, 1.0F };
            t = I.Foo(0.0F, 1.0F);
            r = MethodInvoke.Invoke<Guid>(I, M, args, NoKwargs);
            Assert.That(t, Is.EqualTo(r));

            // Foo(int _1, bool _2 = false)
            args = new object[] { 0, false };
            t = I.Foo(0, false);
            r = MethodInvoke.Invoke<Guid>(I, M, args, NoKwargs);
            Assert.That(t, Is.EqualTo(r));

            // Foo([Optional, DefaultParameterValue(12)] nint _1, uint _2)
            args = new object[] { IntPtr.Zero, 0 };
            t = I.Foo(IntPtr.Zero, 0);
            r = MethodInvoke.Invoke<Guid>(I, M, args, NoKwargs);
            Assert.That(t, Is.EqualTo(r));

            // Foo([Optional, DefaultParameterValue(12)] nint _1, uint _2)
            args = new object[] { 0 };
            var kwargs = new KeywordArgs
            {
                ["_1"] = IntPtr.Zero,
            };
            t = I.Foo(_1: IntPtr.Zero, 0);
            r = MethodInvoke.Invoke<Guid>(I, M, args, kwargs);
            Assert.That(t, Is.EqualTo(r));
        }
    }
}
