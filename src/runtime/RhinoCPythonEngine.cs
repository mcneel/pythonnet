using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

using Python.Runtime.Platform;

namespace Python.Runtime
{
    public class RhinoCPythonEngine
    {
        public Version Version { get; private set; }

        #region Initialization
        public RhinoCPythonEngine(Version version)
        {
#if MONO_OSX
            Debug.WriteLine($"LD_LIBRARY_PATH: {Environment.GetEnvironmentVariable("LD_LIBRARY_PATH")}");
            Debug.WriteLine($"DYLD_LIBRARY_PATH: {Environment.GetEnvironmentVariable("DYLD_LIBRARY_PATH")}");
            Debug.WriteLine($"DYLD_FALLBACK_LIBRARY_PATH: {Environment.GetEnvironmentVariable("DYLD_FALLBACK_LIBRARY_PATH")}");
            Debug.WriteLine($"DYLD_FRAMEWORK_PATH: {Environment.GetEnvironmentVariable("DYLD_FRAMEWORK_PATH")}");
            Debug.WriteLine($"DYLD_FALLBACK_FRAMEWORK_PATH: {Environment.GetEnvironmentVariable("DYLD_FALLBACK_FRAMEWORK_PATH")}");

            var enigneRoot = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            Debug.WriteLine($"RhinoCPythonEngine assembly path: {enigneRoot}");
            var pylibName = $"libpython{version.Major}.{version.Minor}.dylib";
            var pylibPath = Path.Combine(enigneRoot, pylibName);
            Debug.WriteLine($"RhinoCPythonEngine pylibPath: {pylibPath}");
            DarwinLoader.PythonDLL = pylibPath;
#else
            var enigneRoot = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            Debug.WriteLine($"RhinoCPythonEngine assembly path: {enigneRoot}");
            Debug.WriteLine($"RhinoCPythonEngine pylibPath: {Runtime._PythonDll}");
#endif

            Version = version;

            // start cpython runtime
            Initialize();

            // store the default search paths for resetting the engine later
            StoreSearchPaths();
        }

        public void Initialize() => PythonEngine.Initialize();
#endregion

#region Search Paths
        private List<string> _sysPaths = new List<string>();

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
                    var searthPathStr = new PyString(pythonPath);
                    sysPaths.Insert(0, searthPathStr);
                }

                // now add the search paths for the script bundle
                foreach (string searchPath in searchPaths.Reverse<string>())
                {
                    if (searchPath != null && searchPath != string.Empty)
                    {
                        var searthPathStr = new PyString(searchPath);
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
                _sysPaths = new List<string>();
                long itemsCount = currentSysPath.Length();
                for (long i = 0; i < itemsCount; i++)
                {
                    BorrowedReference item =
                        Runtime.PyList_GetItem(currentSysPath.Handle, i);
                    string path = Runtime.GetManagedString(item);
                    _sysPaths.Add(path);
                }
            }
        }

        private PyList RestoreSearchPaths()
        {
            using (Py.GIL())
            {
                var newList = new PyList();
                int i = 0;
                foreach (var searchPath in _sysPaths)
                {
                    var searthPathStr = new PyString(searchPath);
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
                // set sys paths
                PyObject sys = PythonEngine.ImportModule("sys");
                PyObject sysPathsObj = sys.GetAttr("path");
                return PyList.AsList(sysPathsObj);
            }
        }

        private void SetSysPaths(PyList sysPaths)
        {
            using (Py.GIL())
            {
                PyObject sys = PythonEngine.ImportModule("sys");
                sys.SetAttr("path", sysPaths);
            }
        }
#endregion

#region Engine Globals (Builtins)
        private List<string> _builtinKeys = new List<string>();

        public void SetBuiltins(IDictionary<string, object> builtins)
        {
            using (Py.GIL())
            {
                IntPtr engineBuiltins = Runtime.PyEval_GetBuiltins();
                if (engineBuiltins != null)
                {
                    foreach (KeyValuePair<string, object> item in builtins)
                        Runtime.PyDict_SetItemString(
                            pointer: engineBuiltins,
                            key: item.Key,
                            value: PyObject.FromManagedObject(item.Value).Handle
                        );
                    _builtinKeys = builtins.Keys.ToList();
                }
            }
        }

        public void ClearBuiltins()
        {
            using (Py.GIL())
            {
                IntPtr engineBuiltins = Runtime.PyEval_GetBuiltins();
                if (engineBuiltins != null)
                {
                    foreach (var key in _builtinKeys)
                        Runtime.PyDict_DelItemString(
                            pointer: engineBuiltins,
                            key: key
                        );
                    _builtinKeys = new List<string>();
                }
            }
        }
#endregion

#region Input Arguments
        public void SetSysArgs(IEnumerable<string> args)
        {
            using (Py.GIL())
            {
                // setup arguments (sets sys.argv)
                PyObject sys = PythonEngine.ImportModule("sys");

                var pythonArgv = new PyList();

                // add the rest of the args
                foreach (string arg in args)
                {
                    var argStr = new PyString(arg);
                    pythonArgv.Append(argStr);
                }

                sys.SetAttr("argv", pythonArgv);
            }
        }

        public void ClearSysArgs()
        {
            using (Py.GIL())
            {
                PyObject sys = PythonEngine.ImportModule("sys");
                sys.SetAttr("argv", new PyList());
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
        internal class RhinoCPythonEngineStandardIO : Stream, IDisposable
        {
            private Stream _stdin = null;
            private Stream _stdout = null;

            public RhinoCPythonEngineStandardIO(Stream stdin, Stream stdout) { _stdin = stdin; _stdout = stdout; }

            public Encoding OutputEncoding { get; set; } = Encoding.UTF8;

            public override bool CanRead => true;
            public override bool CanWrite => true;
            public override bool CanSeek => false;
            public override long Length => throw new NotImplementedException();
            public override long Position { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotImplementedException();
            public override void SetLength(long value) => throw new NotImplementedException();
            public override void Flush() { }
        
            public bool isatty() => false;

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

        public void SetStdIO(Stream stdin, Stream stdout)
        {
            var pythonIO = new RhinoCPythonEngineStandardIO(stdin, stdout);

            using (Py.GIL())
            {
                PyObject sys = PythonEngine.ImportModule("sys");
                PyObject stdio = PyObject.FromManagedObject(pythonIO);
                sys.SetAttr("stdin", stdio);
                sys.SetAttr("stdout", stdio);
                sys.SetAttr("stderr", stdio);
            }
        }

        public void ClearStdIO() => SetStdIO(null, null);
#endregion

#region Execution
        private Dictionary<string, PyObject> _cache = new Dictionary<string, PyObject>();

        public void RunScope(string scopeName,
                             string pythonFile,
                             bool useCache = true)
        {
            // TODO: implement and test locals
            PyScope scope = Py.CreateScope(scopeName);
            scope.Set("__file__", pythonFile);

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

        public void ClearCache() => _cache.Clear();
    }
#endregion
}
