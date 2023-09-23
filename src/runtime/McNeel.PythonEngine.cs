using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using System.Dynamic;

using Python.Runtime.Platform;

#pragma warning disable
namespace Python.Runtime
{
    public unsafe class RhinoCodePythonEngine
    {
        const string COMPLETE_ATTR = "__complete__";

        static void Log(string message) => Debug.WriteLine($"mcneel.pythonnet: {message}");

        #region Initialization
        public RhinoCodePythonEngine(string enigneRoot, int major, int minor)
        {
            Log($"CPython engine path: {enigneRoot}");
#if MACOS
            // setup the darwin loader manually so it can find the native python shared lib
            // this is so less code changes are done the pythonnet source
            var pythonLib = $"libpython{major}.{minor}.dylib";
#elif WINDOWS
            var pythonLib = $"python{major}{minor}.dll";
#endif
            var dllPath = Path.Combine(enigneRoot, pythonLib);
            LibraryLoader.Instance.Load(dllPath);
            PythonEngine.PythonHome = enigneRoot;
            Log($"Library loader set to: {dllPath}");

            // start cpython runtime
            Start();
            Log($"Initialized python engine");

            // store the default search paths for resetting the engine later
            StoreSysPaths();
            Log($"Setup default search paths");
        }

        public void Start()
        {
            PythonEngine.Initialize();
            BeginThreads();
        }

        public void ShutDown()
        {
            EndThreads();
            PythonEngine.Shutdown();
        }

        IntPtr _main = IntPtr.Zero;
        public void BeginThreads()
        {
            _main = PythonEngine.BeginAllowThreads();
        }

        public void EndThreads()
        {
            PythonEngine.EndAllowThreads(_main);
            _main = IntPtr.Zero;
        }
        #endregion

        #region Search Paths
        string[] _defaultSysPaths = default;

        public void SetSearchPaths(IEnumerable<string> paths)
        {
            // set sys paths
            PyList sysPaths = RestoreSysPaths();
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
                foreach (string path in paths.Reverse<string>())
                {
                    if (path != null && path != string.Empty)
                    {
                        using var searthPathStr = new PyString(path);
                        sysPaths.Insert(0, searthPathStr);
                    }
                }
            }
        }

        public void ClearSearchPaths() => RestoreSysPaths();

        void StoreSysPaths()
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
                _defaultSysPaths = sysPaths.ToArray();
            }
        }

        PyList RestoreSysPaths()
        {
            using (Py.GIL())
            {
                var newList = new PyList();
                int i = 0;
                foreach (var searchPath in _defaultSysPaths ?? new string[] { })
                {
                    using var searthPathStr = new PyString(searchPath);
                    newList.Insert(i, searthPathStr);
                    i++;
                }
                SetSysPaths(newList);
                return newList;
            }
        }

        PyList GetSysPaths()
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

        void SetSysPaths(PyList sysPaths)
        {
            using (Py.GIL())
            {
                var paths = GetSysPaths();

                // set sys path
                using var sys = Runtime.PyImport_ImportModule("sys");
                using (PyObject sysObj = sys.MoveToPyObject())
                {
                    sysObj.SetAttr("path", sysPaths);
                }

                // dispose existing paths
                foreach (PyObject path in paths)
                    path.Dispose();
                paths.Dispose();
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

        #region Standard IO
        public void SetStdIO(Stream stdin, Stream stdout)
        {
            using (Py.GIL())
            {
                using var sys = Runtime.PyImport_ImportModule("sys");
                using (PyObject sysObj = sys.MoveToPyObject())
                {
                    using var stdio = PyObject.FromManagedObject(
                        new RhinoCodePythonEngineIO(stdin, stdout)
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
        public class PyException : Exception
        {
            static readonly Regex s_msgParser = new Regex(@",\sline\s(?<line>\d+?),\sin\s(?<module>.+)");
            readonly string _pyStackTrace;

            public int LineNumber { get; } = -1;

            public PyException(PythonException pyEx)
                : base(pyEx.Message)
            {
                string traceback = pyEx.Traceback is null ? string.Empty : PythonException.TracebackToString(pyEx.Traceback);

                _pyStackTrace = pyEx.Traceback is null ? string.Empty : string.Join(
                    Environment.NewLine,
                    "Traceback (most recent call last):",
                    traceback,
                    $"{pyEx.Type.Name}: {pyEx.Message}"
                );

                Match m = s_msgParser.Match(traceback);
                if (m.Success
                        && int.TryParse(m.Groups["line"].Value, out int line))
                {
                    LineNumber = line;
                }
            }

            public override string ToString() => _pyStackTrace;
        }

        public int GetLineNumber(object frame) => Runtime.PyFrame_GetLineNumber(((PyObject)frame).Handle);

        public bool TryGetManaged(object pyObject, out Type managed)
        {
            managed = default;

            switch (pyObject)
            {
                case PyObject pyObj:
                    using (Py.GIL())
                    {
                        ManagedType? managedType = ManagedType.GetManagedObject(pyObj);

                        switch (managedType)
                        {
                            case ClassBase b:
                                var _type = b.type;
                                managed = _type.Valid ? _type.Value : null;
                                return true;

                            case CLRObject c:
                                switch (c.inst)
                                {
                                    case Type type:
                                        managed = type;
                                        return true;

                                    case object obj:
                                        managed = obj.GetType();
                                        return true;

                                }
                                break;

                        }
                    }
                    break;

                case Type type:
                    managed = type;
                    return true;
            }


            return false;
        }

        public bool TryGetMembers(object pyObject, out IEnumerable<string> members, bool privates = true)
        {
            members = default;

            switch (pyObject)
            {
                case PyObject pyObj:
                    using (Py.GIL())
                    {
                        var all = new HashSet<string>();

                        if (ManagedType.GetManagedObject(pyObj) is ModuleObject module)
                        {
                            module.LoadNames();

                            string modnspace = module.moduleName + '.';
                            foreach (string nspace in AssemblyManager.GetNamespaces())
                            {
                                if (nspace.StartsWith(modnspace))
                                {
                                    string subnspace = nspace.Remove(0, modnspace.Length);
                                    int dotindex = subnspace.IndexOf('.');
                                    if (dotindex > 0)
                                        subnspace = subnspace.Substring(0, dotindex);

                                    all.Add(subnspace);
                                }
                            }

                        }

                        if (pyObj is PyObject)
                        {
                            if (pyObj.HasAttr(COMPLETE_ATTR))
                            {
                                using (PyObject __complete__ = pyObj.GetAttr(COMPLETE_ATTR))
                                using (PyList list = new PyList(__complete__))
                                {
                                    members = list.Select(i => i.ToString()!).ToArray();
                                    return true;
                                }
                            }

                            IEnumerable<string> pymembers = pyObj.GetDynamicMemberNames();
                            if (!privates)
                                pymembers = pymembers.Where(m => !m.StartsWith("_"));

                            all.UnionWith(pymembers.ToArray());

                            members = all.ToArray();
                            return true;
                        }
                    }
                    break;
            }

            return false;
        }

        public bool TryGetItems(object pyObject, out IEnumerable<object> items)
        {
            items = default;

            using (Py.GIL())
            using (PyObject pyObj = pyObject.ToPython())
            {
                if (Runtime.PyTuple_Check(pyObj))
                {
                    var tuple = new PyTuple(pyObj);
                    items = tuple.ToArray();

                    return true;
                }
            }

            return false;
        }

        public bool Evaluate<T>(string pythonCode, object locals, out T? value)
        {
            value = default;

            using (Py.GIL())
            {
                // PyDict g = new PyDict(((PyObject)globals).BorrowNullable());
                PyObject pyObj = PythonEngine.Eval(pythonCode, locals: locals as PyObject);
                if (pyObj is null)
                    return false;

                value = (T)pyObj.As<T>();
                return true;
            }
        }

        public object CompileCode(string codeId, string pythonCode, string pythonFile)
        {
            try
            {
                using (Py.GIL())
                    return PythonEngine.Compile(
                        code: pythonCode,
                        filename: pythonFile ?? string.Empty,
                        mode: RunFlagType.File
                        );
            }
            catch (PythonException pyEx)
            {
                throw new PyException(pyEx);
            }
        }

        public object CreateScope(string scopeName, string pythonFile)
        {
            using (Py.GIL())
                return PrepareScope(scopeName, pythonFile);
        }

        public void RunScript(string script)
        {
            try
            {
                using (Py.GIL())
                    PythonEngine.RunSimpleString(script);
            }
            catch (PythonException pyEx)
            {
                throw new PyException(pyEx);
            }
        }

        public void RunCode(
            string scopeName,
            object code,
            string pythonFile,
            IDictionary<string, object> inputs,
            IDictionary<string, object> outputs,
            string beforeScript = null,
            string afterScript = null,
            bool marshallOutputs = true
        )
        {
            try
            {
                using (Py.GIL())
                using (PyModule pyscope = PrepareScope(scopeName, pythonFile))
                    ExecuteScope(
                            pyscope, (PyObject)code, pythonFile,
                            inputs, outputs,
                            beforeScript, afterScript,
                            marshallOutputs
                        );
            }
            catch (PythonException pyEx)
            {
                throw new PyException(pyEx);
            }
        }

        public void RunScope(
            object scope,
            object code,
            string pythonFile,
            IDictionary<string, object> inputs,
            IDictionary<string, object> outputs,
            string beforeScript = null,
            string afterScript = null,
            bool marshallOutputs = true
        )
        {
            try
            {
                using (Py.GIL())
                    ExecuteScope(
                            (PyModule)scope, (PyObject)code, pythonFile,
                            inputs, outputs,
                            beforeScript, afterScript,
                            marshallOutputs
                        );
            }
            catch (PythonException pyEx)
            {
                throw new PyException(pyEx);
            }
        }

        public void DisposeCode(object code)
        {
            if (code is PyObject pycode)
                using (Py.GIL())
                    pycode.Dispose();
        }

        public void DisposeScope(object scope)
        {
            if (scope is PyModule pyscope)
                using (Py.GIL())
                    pyscope.Dispose();
        }

        PyModule PrepareScope(string scopeName, string pythonFile)
        {
            PyModule pyscope = Py.CreateScope(scopeName);
            pyscope.Set("__file__", pythonFile ?? string.Empty);
            return pyscope;
        }

        void ExecuteScope(
            PyModule pyscope,
            PyObject pycode,
            string pythonFile,
            IDictionary<string, object> inputs,
            IDictionary<string, object> outputs,
            string beforeScript = null,
            string afterScript = null,
            bool marshallOutputs = true)
        {
            pyscope.Set("__file__", pythonFile ?? string.Empty);

            // set inputs and unwrap possible python objects
            foreach (var pair in inputs)
            {
                pyscope.Set(pair.Key, pair.Value);
            }

            // add default references
            if (beforeScript is string)
                pyscope.Exec(beforeScript);

            pyscope.Execute(pycode);

            if (afterScript is string)
                pyscope.Exec(afterScript);

            // set outputs and wrap possible python objects
            foreach (var pair in new Dictionary<string, object>(outputs))
                if (pyscope.TryGet(pair.Key, out object outputValue))
                {
                    if (marshallOutputs)
                        outputs[pair.Key] = MarshallOutput(outputValue);
                    else
                        outputs[pair.Key] = outputValue;
                }
        }

        static object MarshallOutput(object value)
        {
            switch (value)
            {
                case IEnumerable<object> enumerable:
                    return enumerable.Select(i => MarshallOutput(i)).ToList();

                case IDictionary<object, object> dict:
                    return dict.Select(p =>
                    {
                        return new KeyValuePair<object, object>(MarshallOutput(p.Key), MarshallOutput(p.Value));
                    }).ToDictionary(p => p.Key, p => p.Value);

                case PyObject pyObj:
                    return MarshallOutput(pyObj);
            }

            return value;
        }

        static object MarshallOutput(PyObject pyObj)
        {
            if (ManagedType.GetManagedObject(pyObj) is CLRObject co)
            {
                return MarshallOutput(co.inst);
            }

            else if (Runtime.PyList_Check(pyObj))
            {
                var l = new PyList(pyObj);
                return l.Select(i => MarshallOutput(i)).ToList();
            }

            else if (Runtime.PyDict_Check(pyObj))
            {
                var d = new PyDict(pyObj);
                return d.Keys().ToDictionary(k => MarshallOutput(k), k => MarshallOutput(d[k]));
            }

            else if (Runtime.PyString_CheckExact(pyObj))
            {
                if (Converter.ToPrimitive(pyObj, typeof(string), out object? result, false))
                    return result;
                return pyObj;
            }

            else if (Runtime.PyBool_CheckExact(pyObj))
            {
                if (Converter.ToPrimitive(pyObj, typeof(bool), out object? result, false))
                    return result;
                return pyObj;
            }

            else if (Runtime.PyFloat_CheckExact(pyObj))
            {
                if (Converter.ToPrimitive(pyObj, typeof(double), out object? result, false))
                    return result;
                return pyObj;
            }

            else if (Runtime.PyString_Check(pyObj))
            {
                if (Converter.ToPrimitive(pyObj, typeof(string), out object? result, false))
                    return result;
                return pyObj;
            }

            else if (Runtime.PyBool_Check(pyObj))
            {
                if (Converter.ToPrimitive(pyObj, typeof(bool), out object? result, false))
                    return result;
                return pyObj;
            }

            else if (Runtime.PyFloat_Check(pyObj))
            {
                if (Converter.ToPrimitive(pyObj, typeof(double), out object? result, false))
                    return result;
                return pyObj;
            }

            else if (Runtime.PyInt_Check(pyObj))
            {
                if (Converter.ToPrimitive(pyObj, typeof(int), out object? result, false))
                    return result;
                return pyObj;
            }

            return pyObj;
        }
        #endregion
    }

    [SuppressMessage("Python.Runtime", "IDE1006")]
    public class RhinoCodePythonEngineIO : Stream, IDisposable
    {
        readonly Stream _stdin = null;
        readonly Stream _stdout = null;

        public RhinoCodePythonEngineIO(Stream stdin, Stream stdout) { _stdin = stdin; _stdout = stdout; }

        #region Python stream
        public bool isatty() => false;
        public void flush() => Flush();

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
        #endregion

        #region Stream
        public Encoding OutputEncoding { get; set; } = Encoding.UTF8;

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => _stdout?.Length ?? 0;
        public override long Position { get => _stdout?.Position ?? 0; set { if (_stdout is null) return; _stdout.Position = value; } }
        public override long Seek(long offset, SeekOrigin origin) => _stdout?.Seek(offset, origin) ?? 0;
        public override void SetLength(long value) => _stdout.SetLength(value);
        public override int Read(byte[] buffer, int offset, int count) => _stdin?.Read(buffer, offset, count) ?? 0;
        public override void Write(byte[] buffer, int offset, int count) => _stdout?.Write(buffer, offset, count);
        public override void Flush() => _stdout?.Flush();
        #endregion
    }
}
#pragma warning restore
