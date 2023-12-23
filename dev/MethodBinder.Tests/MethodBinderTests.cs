using System;

using MethodBinder;

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
            Guid t = I.Foo();
            Guid r = MethodInvoke.Invoke<Guid>(I, M, NoArgs, NoKwargs);
            Assert.That(t, Is.EqualTo(r));
        }

        [Test]
        public void TestFooParam_1()
        {
            object[] args;
            Guid t;
            Guid r;

            //args = new object[] { 0 };
            //t = I.Foo(0);
            //r = MethodInvoke.Invoke<Guid>(I, M, args, NoKwargs);
            //Assert.That(t, Is.EqualTo(r));

            args = new object[] { 0.0f };
            t = I.Foo(0.0f);
            r = MethodInvoke.Invoke<Guid>(I, M, args, NoKwargs);
            Assert.That(t, Is.EqualTo(r));

            //var kwargs = new KeywordArgs
            //{
            //    ["_1"] = 12,
            //};
            //t = I.Foo(0.0f);
            //r = MethodInvoke.Invoke<Guid>(I, M, NoArgs, kwargs);
            //Assert.That(t, Is.EqualTo(r));
        }

        [Test]
        public void TestFooParam_2()
        {
            object[] args;
            Guid t;
            Guid r;

            args = new object[] { 0, 1 };
            t = I.Foo(0, 1);
            r = MethodInvoke.Invoke<Guid>(I, M, args, NoKwargs);
            Assert.That(t, Is.EqualTo(r));

            args = new object[] { 0.0f, 1.0f };
            t = I.Foo(0.0f, 1.0f);
            r = MethodInvoke.Invoke<Guid>(I, M, args, NoKwargs);
            Assert.That(t, Is.EqualTo(r));

            I.Foo(12, (uint)12);
        }
    }
}
