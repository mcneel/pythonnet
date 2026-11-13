using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using NUnit.Framework;

using Python.Runtime;

namespace Python.EmbeddingTest
{
    // RH-100238: errors raised in Python callbacks called from .NET go to the host handler
    public class TestCallbackErrors
    {
        readonly List<Exception> _received = new();

        [TearDown]
        public void TearDown()
        {
            PythonEngine.BindDefaultDelegateExceptionHandler();
            _received.Clear();
        }

        void Handle(bool swallow) => PythonEngine.BindDelegateExceptionHandler(ex => { _received.Add(ex); return swallow; });

        static T Callback<T>(string code, string name = "f")
        {
            using var scope = Py.CreateScope();
            using var compiled = PythonEngine.Compile(code, "script.py");
            scope.Execute(compiled);
            using var func = scope.Get(name);
            return (T)func.AsManagedObject(typeof(T));
        }

        [Test]
        public void NoHandler_Throws()
        {
            var f = Callback<Func<int>>("def f():\n    raise ValueError('boom')\n");
            var ex = Assert.Throws<PythonException>(() => f());
            Assert.That(ex.Type.Name, Is.EqualTo("ValueError"));
        }

        [Test]
        public void HandlerSwallows_ReturnsDefault()
        {
            Handle(swallow: true);
            var f = Callback<Func<int>>("def f():\n    raise ValueError('boom')\n");

            Assert.That(f(), Is.EqualTo(0));
            Assert.That(_received, Has.Count.EqualTo(1));
            var pyEx = (PythonException)_received[0];
            Assert.That(pyEx.Type.Name, Is.EqualTo("ValueError"));
            Assert.That(pyEx.Traceback, Is.Not.Null);
        }

        [Test]
        public void HandlerSwallows_ReferenceReturnIsNull()
        {
            Handle(swallow: true);
            var f = Callback<Func<object>>("def f():\n    raise ValueError('boom')\n");
            Assert.That(f(), Is.Null);
        }

        [Test]
        public void HandlerDeclines_Rethrows()
        {
            Handle(swallow: false);
            var f = Callback<Action>("def f():\n    raise ValueError('boom')\n");

            Assert.Throws<PythonException>(() => f());
            Assert.That(_received, Has.Count.EqualTo(1));
        }

        [Test]
        public void WrongSignature_GoesToHandler()
        {
            Handle(swallow: true);
            var f = Callback<Action<int>>("def f():\n    pass\n");

            f(1);
            Assert.That(((PythonException)_received[0]).Type.Name, Is.EqualTo("TypeError"));
        }

        [Test]
        public void BadReturnValue_GoesToHandler()
        {
            Handle(swallow: true);
            var f = Callback<Func<int>>("def f():\n    return 'x'\n");

            Assert.That(f(), Is.EqualTo(0));
            Assert.That(_received, Has.Count.EqualTo(1));
        }

        [Test]
        public void DotNetErrorInCallback_GoesToHandler()
        {
            Handle(swallow: true);
            var f = Callback<Action>("import System\ndef f():\n    System.Int32.Parse('x')\n");

            f();
            Assert.That(_received[0], Is.InstanceOf<FormatException>());
        }

        [Test]
        public void EventHandler_GoesToHandler()
        {
            Handle(swallow: true);
            var source = new CallbackErrorSource();
            using (var scope = Py.CreateScope())
            {
                scope.Set("source", source);
                scope.Exec("def on_fired(sender, e):\n    raise ValueError('boom')\nsource.Fired += on_fired\n");
            }

            source.Fire();
            Assert.That(((PythonException)_received[0]).Type.Name, Is.EqualTo("ValueError"));
        }

        [Test]
        public void Override_GoesToHandler()
        {
            Handle(swallow: true);
            CallbackErrorBase obj;
            using (var scope = Py.CreateScope())
            {
                scope.Exec(@"
from Python.EmbeddingTest import CallbackErrorBase
class Derived(CallbackErrorBase):
    __namespace__ = 'TestCallbackErrors'
    def Compute(self):
        raise ValueError('boom')
obj = Derived()
");
                obj = scope.Get("obj").As<CallbackErrorBase>();
            }

            Assert.That(obj.Compute(), Is.EqualTo(0));
            Assert.That(((PythonException)_received[0]).Type.Name, Is.EqualTo("ValueError"));
        }

        // the modal-dialog case: the script is still on the stack below the callback
        [Test]
        public void NestedCallback_GoesToHandler()
        {
            Handle(swallow: true);
            using var scope = Py.CreateScope();
            scope.Exec(@"
from Python.EmbeddingTest import CallbackErrorSource
def f():
    raise ValueError('boom')
CallbackErrorSource.Run(f)
done = True
");
            Assert.That(scope.Get<bool>("done"), Is.True);
            Assert.That(_received, Has.Count.EqualTo(1));
        }

        [Test]
        public void Wrapper_ReceivesScriptLineNumber()
        {
            // skips the constructor, which would initialize a second engine
            var wrapper = (PythonEngineWrapper)RuntimeHelpers.GetUninitializedObject(typeof(PythonEngineWrapper));
            var received = new List<Exception>();
            var f = Callback<Action>("def f():\n    x = 1\n    raise ValueError('boom')\n");
            wrapper.BindDelegateExceptionHandler(ex => { received.Add(ex); return true; });
            try
            {
                f();

                var pyEx = (PythonEngineWrapper.PyException)received[0];
                Assert.That(pyEx.LineNumber, Is.EqualTo(3));
            }
            finally
            {
                wrapper.BindDefaultDelegateExceptionHandler();
            }
            Assert.Throws<PythonException>(() => f());
        }
    }

    public class CallbackErrorSource
    {
        public event EventHandler Fired;
        public void Fire() => Fired?.Invoke(this, EventArgs.Empty);
        public static void Run(Action callback) => callback();
    }

    public class CallbackErrorBase
    {
        public virtual int Compute() => 1;
    }
}
