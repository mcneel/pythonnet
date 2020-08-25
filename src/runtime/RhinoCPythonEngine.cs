using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Python.Runtime
{
    public class RhinoCPythonEngine
    {
        internal class RhinoCPythonEngineStandardIO : Stream, IDisposable
        {
            private Stream _stdin = null;
            private Stream _stdout = null;

            public RhinoCPythonEngineStandardIO(Stream stdin, Stream stdout)
            {
                _stdin = stdin;
                _stdout = stdout;
            }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotImplementedException();

            public override long Position { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

            public override void Flush()
            {
            }

            public object read()
            {
                return null;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return 0;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotImplementedException();
            }

            public override void SetLength(long value)
            {
                throw new NotImplementedException();
            }

            // python write method
            public void write(string content)
            {
                var buffer = Encoding.UTF8.GetBytes(content);
                Write(buffer, 0, buffer.Length);
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                _stdout?.Write(buffer, offset, count);
            }
        }

        public Version Version { get; private set; }

        public RhinoCPythonEngine(Version version)
        {
            Version = version;
            Initialize();
        }

        public void Initialize() => PythonEngine.Initialize();

        public void SetSearchPaths(IEnumerable<string> searchPaths)
        {

        }

        public void ClearSearchPaths()
        {

        }

        public void SetEnvVars(IDictionary<string, string> envVars)
        {

        }

        public void ClearEnvVars()
        {

        }

        public void SetBuiltins(IDictionary<string, object> builtins)
        {

        }

        public void ClearBuiltins()
        {

        }

        public void SetSysArgs(IEnumerable<string> args)
        {

        }

        public void ClearSysArgs()
        {

        }

        public void SetTrace()
        {
            //PyObject sys = PythonEngine.ImportModule("sys");
            //var traceFuncMethod = new MethodWrapper(GetType(), "TraceFunc");
            //sys.InvokeMethod("settrace", new PyObject(traceFuncMethod.ptr));
        }

        public void ClearTrace()
        {
            //PyObject sys = PythonEngine.ImportModule("sys");
            //sys.InvokeMethod("settrace", PyObject.FromManagedObject(null));
        }

        public void SetStdIO(Stream stdin, Stream stdout)
        {
            var pythonIO = new RhinoCPythonEngineStandardIO(stdin, stdout);

            PyObject sys = PythonEngine.ImportModule("sys");
            sys.SetAttr("stdin", PyObject.FromManagedObject(pythonIO));
            sys.SetAttr("stdout", PyObject.FromManagedObject(pythonIO));
        }

        public void ClearStdIO() => SetStdIO(null, null);

        public bool Run(string pythonScript,
                        IDictionary<string, object> locals,
                        ref IDictionary<string, object> globals)
        {
            PyScope scope = Py.CreateScope("__main__");
            scope.Set("__file__", pythonScript);

            var scriptContents =
                File.ReadAllText(pythonScript,
                                 encoding: System.Text.Encoding.UTF8);

            // execute
            using (scope)
            {
                using (Py.GIL())
                    scope.Exec(scriptContents);
            }

            // var pyMain = _runtime.GetMethod(
            //     "Py_Main",
            //     new Type[] {
            //         typeof(int),
            //         typeof(string[])
            //     }
            // );
            // pyMain.Invoke(
            //     null,
            //     new object[] {
            //         9,
            //         new string[] {
            //             "", "-m", "ptvsd",
            //             "--host", DAPDebugHost,
            //             "--port", DAPDebugPort.ToString(),
            //             "--wait", pythonScript
            //         }
            //     }
            // );

            return true;
        }

        public static int TraceFunc(object frame, object evnt, object arg)
        {
            return 0;
            // if trace function of debug controls is connected,
            // collect frame info and send to the trace function
            //RhinoApp.WriteLine($"{frame}");
            //if (_traceFunc != null) {
            //    var mockExecFrame = new ExecutionFrame {
            //        ThreadId = 0,
            //        SourceFilePath = "",
            //        LineNumber = 0,
            //        Globals = new Dictionary<string, object>() {
            //            { "meaning_of_universe", 42 }
            //        },
            //        Locals = new Dictionary<string, object>() {
            //            { "local_var1", "some value " },
            //            { "local_var2", 37 },
            //            { "local_var3", 545.56}
            //        }
            //    };
            //    _traceFunc(ExecutionEvent.Line, mockExecFrame);
            //}
        }
    }
}
