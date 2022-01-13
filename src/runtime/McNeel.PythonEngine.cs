using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

using Python.Runtime.Platform;

#pragma warning disable
namespace Python.Runtime
{
    public class RhinoCodePythonNet
    {
        static void Log(string message) => Debug.WriteLine($"mcneel.pythonnet: {message}");

        public Version Version { get; private set; }

        #region Initialization
        public RhinoCodePythonNet(string enigneRoot, Version version)
        {
            Log($"CPython engine path: {enigneRoot}");
#if MACOS
            // setup the darwin loader manually so it can find the native python shared lib
            // this is so less code changes are done the pythonnet source
            var dylibName = $"libpython{version.Major}.{version.Minor}.dylib";
            var dllPath = Path.Combine(enigneRoot, dylibName);
            LibraryLoader.Instance.Load(dllPath);
            Log($"Library loader set to: {dllPath}");
#endif

            Version = version;

            // start cpython runtime
            Initialize();
            Log($"Initialized python engine");

            // store the default search paths for resetting the engine later
            StoreSearchPaths();
            Log($"Setup default search paths");
        }

        public void Initialize() => PythonEngine.Initialize();
        public void ShutDown() => PythonEngine.Shutdown();
        #endregion

        #region Search Paths
        string[] _sysPaths = default;

        // bool _mainInterpThreadState = true;
        // public bool MainInterpretter
        // {
        //     get => _mainInterpThreadState;
        //     set
        //     {
        //         _mainInterpThreadState = value;
        //         if (_mainInterpThreadState)
        //             Runtime.PyEval_AcquireThread(_mainstate);
        //         else
        //             Runtime.PyEval_ReleaseThread(_mainstate);
        //     }
        // }

        public void SetSearchPaths(IEnumerable<string> searchPaths)
        {
            // set sys paths
            PyList sysPaths = RestoreSearchPaths();
            using (Py.GIL())
            {
                // manually add PYTHONPATH since we are overwriting the sys paths
                var pythonPath = Environment.GetEnvironmentVariable("PYTHONPATH");
                if (pythonPath != null && pythonPath != string.Empty)
                {
                    using var searthPathStr = new PyString(pythonPath);
                    sysPaths.Insert(0, searthPathStr);
                }

                // now add the search paths for the script bundle
                foreach (string searchPath in searchPaths.Reverse<string>())
                {
                    if (searchPath != null && searchPath != string.Empty)
                    {
                        using var searthPathStr = new PyString(searchPath);
                        sysPaths.Insert(0, searthPathStr);
                    }
                }
            }
        }

        public void ClearSearchPaths() => RestoreSearchPaths();

        private void StoreSearchPaths()
        {
            using (Py.GIL())
            {
                var currentSysPath = GetSysPaths();
                var sysPathRef = currentSysPath.BorrowNullable();
                var sysPaths = new List<string>();
                long itemsCount = currentSysPath.Length();
                for (nint i = 0; i < itemsCount; i++)
                {
                    BorrowedReference item =
                        Runtime.PyList_GetItem(sysPathRef, i);
                    string path = Runtime.GetManagedString(item);
                    sysPaths.Add(path);
                }
                _sysPaths = sysPaths.ToArray();
            }
        }

        private PyList RestoreSearchPaths()
        {
            using (Py.GIL())
            {
                var newList = new PyList();
                int i = 0;
                foreach (var searchPath in _sysPaths ?? new string[] { })
                {
                    using var searthPathStr = new PyString(searchPath);
                    newList.Insert(i, searthPathStr);
                    i++;
                }
                SetSysPaths(newList);
                return newList;
            }
        }

        private PyList GetSysPaths()
        {
            using (Py.GIL())
            {
                // get sys paths
                using var sys = Runtime.PyImport_ImportModule("sys");
                using (PyObject sysObj = sys.MoveToPyObject())
                {
                    PyObject sysPathsObj = sysObj.GetAttr("path");
                    return PyList.AsList(sysPathsObj);
                }
            }
        }

        private void SetSysPaths(PyList sysPaths)
        {
            using (Py.GIL())
            {
                var existingPaths = GetSysPaths();

                // set sys path
                using var sys = Runtime.PyImport_ImportModule("sys");
                using (PyObject sysObj = sys.MoveToPyObject())
                {
                    sysObj.SetAttr("path", sysPaths);
                }

                // dispose existing paths
                foreach (PyObject path in existingPaths)
                    path.Dispose();
                existingPaths.Dispose();
            }
        }
        #endregion

        #region Engine Globals (Builtins)
        string[] _builtinKeys = default;

        public void SetBuiltins(IDictionary<string, object> builtins)
        {
            using (Py.GIL())
            {
                BorrowedReference engineBuiltins = Runtime.PyEval_GetBuiltins();
                if (engineBuiltins != null)
                {
                    foreach (KeyValuePair<string, object> item in builtins)
                    {
                        // PyDict_SetItemString _does not_ steal ref
                        // make sure this ref is disposed
                        using var value = PyObject.FromManagedObject(item.Value);
                        Runtime.PyDict_SetItemString(
                            dict: engineBuiltins,
                            key: item.Key,
                            value: value
                        );
                    }
                    _builtinKeys = builtins.Keys.ToArray();
                }
            }
        }

        public void ClearBuiltins()
        {
            using (Py.GIL())
            {
                BorrowedReference engineBuiltins = Runtime.PyEval_GetBuiltins();
                if (engineBuiltins != null)
                {
                    foreach (var key in _builtinKeys ?? new string[] { })
                        Runtime.PyDict_DelItemString(
                            pointer: engineBuiltins,
                            key: key
                        );
                    _builtinKeys = default;
                }
            }
        }
        #endregion

        #region Input Arguments
        public void ClearSysArgs() => SetSysArgs(new string[] { });

        public PyList GetSysArgs()
        {
            using (Py.GIL())
            {
                // get sys paths
                using var sys = Runtime.PyImport_ImportModule("sys");
                using (PyObject sysObj = sys.MoveToPyObject())
                {
                    PyObject sysArgvObj = sysObj.GetAttr("argv");
                    return PyList.AsList(sysArgvObj);
                }
            }
        }

        public void SetSysArgs(IEnumerable<string> args)
        {
            using (Py.GIL())
            {
                var existingSysArgs = GetSysArgs();

                // setup arguments (sets sys.argv)
                using var sys = Runtime.PyImport_ImportModule("sys");
                using (PyObject sysObj = sys.MoveToPyObject())
                {
                    var sysArgs = new PyList();
                    // add the rest of the args
                    foreach (string arg in args)
                    {
                        using var argStr = new PyString(arg);
                        sysArgs.Append(argStr);
                    }

                    sysObj.SetAttr("argv", sysArgs);
                }

                // dispose existing paths
                foreach (PyObject path in existingSysArgs)
                    path.Dispose();
                existingSysArgs.Dispose();
            }
        }
        #endregion

        #region Debugging
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
        #endregion

        #region Standard IO
        public void SetStdIO(Stream stdin, Stream stdout)
        {
            using (Py.GIL())
            {
                using var sys = Runtime.PyImport_ImportModule("sys");
                using (PyObject sysObj = sys.MoveToPyObject())
                {
                    using var stdio = PyObject.FromManagedObject(
                        new RhinoCodePythonNetIO(stdin, stdout)
                    );
                    sysObj.SetAttr("stdin", stdio);
                    sysObj.SetAttr("stdout", stdio);
                    sysObj.SetAttr("stderr", stdio);
                }
            }
        }

        public void ClearStdIO() => SetStdIO(null, null);
        #endregion

        #region Execution
        private Dictionary<string, PyObject> _cache = new Dictionary<string, PyObject>();

        public void RunScope(string scopeName, string pythonFile, string bootstrapScript = null, bool tempFile = false, bool useCache = true)
        {
            // TODO: implement and test locals
            PyModule scope = Py.CreateScope(scopeName);
            scope.Set("__file__", tempFile ? string.Empty : pythonFile);

            // add default references
            if (bootstrapScript is string)
                scope.Exec(bootstrapScript);

            // execute
            using (Py.GIL())
            {

                try
                {
                    PyObject codeObj;
                    if (useCache && _cache.ContainsKey(pythonFile))
                    {
                        codeObj = _cache[pythonFile];
                    }
                    else
                    {
                        codeObj = PythonEngine.Compile(
                            code: File.ReadAllText(pythonFile, encoding: Encoding.UTF8),
                            filename: pythonFile,
                            mode: RunFlagType.File
                            );
                        // cache the compiled code object
                        _cache[pythonFile] = codeObj;
                    }

                    scope.Execute(codeObj);
                }
                catch (PythonException pyEx)
                {
                    throw new Exception(
                        message: string.Join(
                            Environment.NewLine,
                            new string[] { pyEx.Message, pyEx.StackTrace }
                            ),
                        innerException: pyEx
                        );
                }
                finally
                {
                    scope.Dispose();
                }
            }
        }

        // IntPtr _mainstate = IntPtr.Zero;
        // IntPtr _gilstate = IntPtr.Zero;
        public void RunBackgroundScope(string scopeName, string pythonFile, Stream stdout = null, Stream stderr = null)
        {
            // Runtime.PyEval_ReleaseThread(_mainstate);
            // _gilstate = Runtime.PyGILState_Ensure();

            // {
            //     Runtime.PyGILState_Release(_gilstate);
            //     Runtime.PyThreadState_Swap(IntPtr.Zero);
            //     {
            //         Task.Run(() =>
            //         {
            //             IntPtr gilstate = Runtime.PyGILState_Ensure();
            //             IntPtr ts = Runtime.PyThreadState_Get();

            //             IntPtr substate = Runtime.Py_NewInterpreter();
            //             IntPtr newTs = Runtime.PyThreadState_Get();
            //             Debug.WriteLine($"subinterp: new python interpretter thread state: {newTs}");
            //             Runtime.Initialize();
            //             PythonEngine.LoadCLR();

            //             PyObject sys = PythonEngine.ImportModule("sys");
            //             if (stdout is Stream)
            //             {
            //                 PyObject _stdout = PyObject.FromManagedObject(stdout);
            //                 sys.SetAttr("stdout", _stdout);
            //             }
            //             if (stderr is Stream)
            //             {
            //                 PyObject _stderr = PyObject.FromManagedObject(stderr);
            //                 sys.SetAttr("stderr", _stderr);
            //             }

            //             PyScope scope = Py.CreateScope(scopeName);
            //             scope.Set("__file__", pythonFile);

            //             PyObject codeObj = PythonEngine.Compile(
            //                 code: File.ReadAllText(pythonFile, encoding: Encoding.UTF8),
            //                 filename: pythonFile,
            //                 mode: RunFlagType.File
            //                 );

            //             scope.Execute(codeObj);

            //             if (Runtime.PyErr_Occurred() != IntPtr.Zero)
            //                 Debug.WriteLine($"subinterp: {new PythonException().Message}");

            //             Runtime.Py_EndInterpreter(substate);
            //             Debug.WriteLine($"subinterp: end subinterp");

            //             Runtime.PyThreadState_Swap(ts);
            //             Runtime.PyGILState_Release(gilstate);
            //         }
            //         );
            //     }
            // }
        }

        public void StopBackgroundScope()
        {
            // Runtime.PyThreadState_Swap(_mainstate);
            // _gilstate = Runtime.PyGILState_Ensure();

            // Runtime.PyGILState_Release(_gilstate);
            // Runtime.PyEval_RestoreThread(_mainstate);

            // // var c = new TcpClient("localhost", 8002);
            // // var d = Encoding.ASCII.GetBytes("GET /end HTTP/1.1");
            // // c.GetStream().Write(d, 0, d.Length);
            // // c.Close();
        }

        public void ClearCache()
        {
            foreach (PyObject codeObj in _cache.Values)
                codeObj.Dispose();
            _cache.Clear();
        }
        #endregion
    }

    [SuppressMessage("Python.Runtime", "IDE1006")]
    public class RhinoCodePythonNetIO : Stream, IDisposable
    {
        private readonly Stream _stdin = null;
        private readonly Stream _stdout = null;

        public RhinoCodePythonNetIO(Stream stdin, Stream stdout) { _stdin = stdin; _stdout = stdout; }

        public Encoding OutputEncoding { get; set; } = Encoding.UTF8;

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotImplementedException();
        public override long Position { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotImplementedException();
        public override void SetLength(long value) => throw new NotImplementedException();
        public override void Flush() { }

        public bool isatty()
        {
            return false;
        }

        public void flush()
        {
            Flush();
        }

        // python read method
        public string read(int size = -1) => readline(size);
        public string readline(int size = -1)
        {
            var buffer = new byte[1024];
            // we know how read works so don't need to read size until
            // zero and make multiple calls
            Read(buffer, 0, 1024);
            // second call to clear the flag
            Read(buffer, 0, 1024);
            return OutputEncoding.GetString(buffer);
        }

        // python write method
        public void write(string content)
        {
            var buffer = OutputEncoding.GetBytes(content);
            Write(buffer, 0, buffer.Length);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return _stdin != null ? _stdin.Read(buffer, offset, count) : 0;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _stdout?.Write(buffer, offset, count);
        }
    }
}
#pragma warning restore