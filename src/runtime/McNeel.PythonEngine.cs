using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Collections;
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
            using (Py.GIL())
            {
                StoreStdStreams();
                StoreSysPaths();
            }

            Log($"Setup default search paths");
        }

        public void Start()
        {
            PythonEngine.Initialize();
            BeginThreads();

            PyObjectConversions.RegisterDecoder(Python.Runtime.Codecs.SequenceDecoder.Instance);
            PyObjectConversions.RegisterDecoder(Python.Runtime.Codecs.IterableDecoder.Instance);
            PyObjectConversions.RegisterDecoder(Python.Runtime.Codecs.ListDecoder.Instance);
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
            using (Py.GIL())
            {
                // set sys paths
                PyList sysPaths = RestoreSysPaths();

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

        public void ClearSearchPaths()
        {
            using (Py.GIL())
            {
                RestoreSysPaths();
            }
        }

        void StoreSysPaths()
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

        PyList RestoreSysPaths()
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

        PyList GetSysPaths()
        {
            // get sys paths
            using var sys = Runtime.PyImport_ImportModule("sys");
            using (PyObject sysObj = sys.MoveToPyObject())
            {
                PyObject sysPathsObj = sysObj.GetAttr("path");
                return PyList.AsList(sysPathsObj);
            }
        }

        void SetSysPaths(PyList sysPaths)
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
        static PyObject s_stdout = default;
        static PyObject s_stderr = default;
        static PyObject s_stdout_prev = default;
        static PyObject s_stderr_prev = default;

        enum ResetStreamPolicy : int
        {
            ResetToStream = 0,          // equals Rhino.Runtime.Code.Execution.ResetStreamPolicy.ResetToPlatformStream
            ResetToStandardStream = 1,  // equals Rhino.Runtime.Code.Execution.ResetStreamPolicy.ResetToStandardStream
            ResetToPreviousStream = 2,  // equals Rhino.Runtime.Code.Execution.ResetStreamPolicy.ResetToPreviousStream
        }

        public void SetStdOut(Stream stdout)
        {
            using (Py.GIL())
            {
                using var sys = Runtime.PyImport_ImportModule("sys");
                using (PyObject sysObj = sys.MoveToPyObject())
                {
                    SetStdOutObj(sysObj, stdout);
                }
            }
        }

        public void SetStdErr(Stream stderr)
        {
            using (Py.GIL())
            {
                using var sys = Runtime.PyImport_ImportModule("sys");
                using (PyObject sysObj = sys.MoveToPyObject())
                {
                    SetStdErrObj(sysObj, stderr);
                }
            }
        }

        public void SetStdOutErr(Stream stdout, Stream stderr)
        {
            using (Py.GIL())
            {
                using var sys = Runtime.PyImport_ImportModule("sys");
                using (PyObject sysObj = sys.MoveToPyObject())
                {
                    SetStdOutObj(sysObj, stdout);
                    SetStdErrObj(sysObj, stderr);
                }
            }
        }

        public void ClearStdOut(int resetPolicy, Stream stdout = default)
        {
            using (Py.GIL())
            {
                using var sys = Runtime.PyImport_ImportModule("sys");
                using (PyObject sysObj = sys.MoveToPyObject())
                {
                    ResetStdOutObj(sysObj, resetPolicy, stdout);
                }
            }
        }

        public void ClearStdErr(int resetPolicy, Stream stderr = default)
        {
            using (Py.GIL())
            {
                using var sys = Runtime.PyImport_ImportModule("sys");
                using (PyObject sysObj = sys.MoveToPyObject())
                {
                    ResetStdErrObj(sysObj, resetPolicy, stderr);
                }
            }
        }

        public void ClearStdOutErr(int resetPolicy, Stream stdout = default, Stream stderr = default)
        {
            using (Py.GIL())
            {
                using var sys = Runtime.PyImport_ImportModule("sys");
                using (PyObject sysObj = sys.MoveToPyObject())
                {
                    ResetStdOutObj(sysObj, resetPolicy, stdout);
                    ResetStdErrObj(sysObj, resetPolicy, stderr);
                }
            }
        }

        void StoreStdStreams()
        {
            using (Py.GIL())
            {
                using var sys = Runtime.PyImport_ImportModule("sys");
                using (PyObject sysObj = sys.MoveToPyObject())
                {
                    s_stdout = sysObj.GetAttr("stdout");
                    s_stderr = sysObj.GetAttr("stderr");
                    s_stdout_prev = s_stdout;
                    s_stderr_prev = s_stdout;
                }
            }
        }

        void SetStdOutObj(PyObject sysObj, Stream stream)
        {
            s_stdout_prev = sysObj.GetAttr("stdout");
            using var stdio = PyObject.FromManagedObject(new PythonStream(stream, 1));
            sysObj.SetAttr("stdout", stdio);
        }

        void SetStdErrObj(PyObject sysObj, Stream stream = default)
        {
            s_stderr_prev = sysObj.GetAttr("stderr");
            using var newStderr = PyObject.FromManagedObject(new PythonStream(stream, 2));
            sysObj.SetAttr("stderr", newStderr);
        }

        void ResetStdOutObj(PyObject sysObj, int resetPolicy, Stream stdout = default)
        {
            switch ((ResetStreamPolicy)resetPolicy)
            {
                case ResetStreamPolicy.ResetToStream:
                    DisposePrevStream(s_stdout_prev);
                    s_stdout_prev = default;
                    if (stdout is null) break;
                    using (var newStdout = PyObject.FromManagedObject(new PythonStream(stdout, 1)))
                        sysObj.SetAttr("stdout", newStdout);
                    break;

                case ResetStreamPolicy.ResetToStandardStream:
                    DisposePrevStream(s_stdout_prev);
                    s_stdout_prev = default;
                    sysObj.SetAttr("stdout", s_stdout);
                    break;

                case ResetStreamPolicy.ResetToPreviousStream:
                    sysObj.SetAttr("stdout", s_stdout_prev ?? s_stdout);
                    break;
            }
        }

        void ResetStdErrObj(PyObject sysObj, int resetPolicy, Stream stderr = default)
        {
            switch ((ResetStreamPolicy)resetPolicy)
            {
                case ResetStreamPolicy.ResetToStream:
                    DisposePrevStream(s_stderr_prev);
                    s_stderr_prev = default;
                    if (stderr is null) break;
                    using (var newStderr = PyObject.FromManagedObject(new PythonStream(stderr, 2)))
                        sysObj.SetAttr("stderr", newStderr);
                    break;

                case ResetStreamPolicy.ResetToStandardStream:
                    DisposePrevStream(s_stderr_prev);
                    s_stderr_prev = default;
                    sysObj.SetAttr("stderr", s_stderr);
                    break;

                case ResetStreamPolicy.ResetToPreviousStream:
                    sysObj.SetAttr("stderr", s_stderr_prev ?? s_stderr);
                    break;
            }
        }

        void DisposePrevStream(PyObject streamObj)
        {
            if (streamObj is null)
                return;

            if (ManagedType.GetManagedObject(streamObj) is CLRObject clrObj
                    && clrObj.inst is PythonStream exstPyStream)
            {
                exstPyStream.Dispose();
            }
        }
        #endregion

        #region Execution
        public class PyException : Exception
        {
            static readonly Regex s_msgParser = new Regex(@"(?<message>.+)\(.+line\s(?<line>\d+?)\)");
            static readonly Regex s_pathParser = new Regex(@"File ""(?<path>.+)"",\sline");
            static readonly Regex s_tbParser = new Regex(@"File "".+"",\sline\s(?<line>\d+?),\sin\s(?<module>.+)");

            readonly string _message;
            readonly string _pyStackTrace;

            public override string Message => _message;

            public int LineNumber { get; } = -1;

            public PyException(PythonException pyEx) : base()
            {
                string traceback = pyEx.Traceback is null ?
                    string.Empty
                  : PythonException.TracebackToString(pyEx.Traceback);

                _message = ParseMessage(pyEx);

                // NOTE:
                // assumes the first line in traceback is pointing to a line
                // in the executing script.

                // Example (Error on line importing a module):
                //   File "schema:///990e0d1a/f15866f6", line 2, in <module>
                //   File "C:\module\__init__.py", line 87, in <module>
                //     lib = CDLL(libpath)
                //   File "ctypes\__init__.py", line 364, in __init__

                // Example (Error on line calling method in script):
                //   File "C:\scripts\script.py", line 4, in <module>
                //   File "C:\scripts\script.py", line 3, in foo

                // lets find the last line number before exiting the script.
                // first we need the file path. lets grab that from the first
                // line of the traceback. see notes above.
                string scriptTraceback = traceback;
                string scriptPath = string.Empty;
                Match um = s_pathParser.Match(traceback);
                if (um.Success
                        && um.Groups["path"].Value is string path)
                {
                    scriptPath = path;
                }
                // if file path is found, iterate over traceback lines,
                // and find the last line that references the executing script
                // before the stack dives outside, into imported modules
                if (!string.IsNullOrEmpty(scriptPath))
                {
                    foreach (string tbl in traceback.Split('\n'))
                    {
                        if (tbl.Contains(scriptPath))
                            scriptTraceback = tbl;
                        else
                            break;
                    }
                }

                Match m = s_tbParser.Match(scriptTraceback);
                if (m.Success
                        && int.TryParse(m.Groups["line"].Value, out int line))
                {
                    LineNumber = line;
                }
                else
                {
                    m = s_msgParser.Match(_message);
                    if (m.Success
                            && int.TryParse(m.Groups["line"].Value, out line))
                    {
                        _message = m.Groups["message"].Value;
                        LineNumber = line;
                    }
                }

                _pyStackTrace = pyEx.Traceback is null ? string.Empty : string.Join(
                    Environment.NewLine,
                    "Traceback (most recent call last):",
                    traceback,
                    $"{pyEx.Type.Name}: {_message}"
                );
            }

            public override string ToString() => _pyStackTrace;

            static string ParseMessage(PythonException pyEx)
            {
                if (string.IsNullOrEmpty(pyEx.Message))
                {
                    return string.Empty;
                }

                if (pyEx.Message.Equals("None", StringComparison.InvariantCultureIgnoreCase))
                {
                    return $"{pyEx.Type.Name} exception";
                }

                return pyEx.Message;
            }
        }

        public Exception ParseException(Exception exception)
        {
            if (exception is PythonException pyEx)
                return new PyException(pyEx);
            return exception;
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

        public class PyCompletion : Tuple<string, string>, IEquatable<PyCompletion>
        {
            public PyCompletion(string text, string kind)
                : base(text, kind)
            {

            }

            public bool Equals(PyCompletion other)
            {
                if (other is null)
                    return false;

                return Item1 == other.Item1;
            }

            public override bool Equals(object obj) => Equals(obj as PyCompletion);

            public override int GetHashCode() => Item1.GetHashCode();
        }

        // NOTE:
        // returns Tuple<string, string>
        // index 0: member name
        // index 1: member kind (module, class, struct, function, property)
        // loosly following https://jedi.readthedocs.io/en/latest/docs/api-classes.html#jedi.api.classes.BaseName.type
        public bool TryGetMembers(object pyObject, out IEnumerable<Tuple<string, string>> members, bool privates = true)
        {
            members = default;

            switch (pyObject)
            {
                case PyObject pyObj:
                    using (Py.GIL())
                    {
                        var all = new HashSet<PyCompletion>();

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

                                    all.Add(new PyCompletion(subnspace, "module"));
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
                                    foreach (PyObject item in list)
                                    {
                                        string key = item.ToString()!;

                                        if (!privates && key.StartsWith("_"))
                                            continue;

                                        using var attr = pyObj.GetAttr(item);
                                        all.Add(new PyCompletion(key, GetObjectKind(attr)));
                                    }
                                }
                            }
                            else if (pyObj.HasAttr(nameof(PyIdentifier.__dict__)))
                            {
                                using (dynamic __dict__ = pyObj.GetAttr(nameof(PyIdentifier.__dict__)))
                                {
                                    // __dict__ is either a 'dict' or a 'mappingproxy'
                                    foreach (string key in __dict__.keys())
                                    {
                                        if (!privates && key.StartsWith("_"))
                                            continue;

                                        using var attr = pyObj.GetAttr(key);

                                        // ensure generic type names are not included. Their non-generic
                                        // name is already included e.g. 'Stack' and 'Stack`1' (RH-85354)
                                        if (key.Contains("`"))
                                            continue;

                                        all.Add(new PyCompletion(key, GetObjectKind(attr)));
                                    }
                                }
                            }
                            else
                                foreach (PyObject item in pyObj.Dir())
                                {
                                    string name = item.ToString()!;

                                    if (!privates && name.StartsWith("_"))
                                        continue;

                                    using var attr = pyObj.GetAttr(item);
                                    all.Add(new PyCompletion(name, GetObjectKind(attr)));
                                }

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

        public bool TryGetFunctionSignature(object pyObject, out string signature)
        {
            signature = default;

            using (Py.GIL())
            using (PyObject pyObj = pyObject.ToPython())
            {
                if (pyObj.IsCallable())
                {
                    using PyObject threading = Py.Import("inspect");
                    using PyObject callableSignature = threading.InvokeMethod("signature", pyObj);
                    using PyObject callableName = pyObj.GetAttr(nameof(PyIdentifier.__name__));
                    signature = callableName.ToString() + callableSignature.ToString();
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

        public IDisposable GIL() => Py.GIL();

        public object CreateScope(string scopeName, string pythonFile) => PrepareScope(scopeName, pythonFile);

        public void RunScope(object scope, string script)
        {
            PyModule pyscope = (PyModule)scope;

            try
            {
                pyscope.Exec(script);
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
            string beforeScript = null,
            string afterScript = null
        )
        {
            PyModule pyscope = (PyModule)scope;
            PyObject pycode = (PyObject)code;

            try
            {
                pyscope.Set(nameof(PyIdentifier.__file__), pythonFile ?? string.Empty);

                // add default references
                if (beforeScript is string)
                    pyscope.Exec(beforeScript);

                pyscope.Execute(pycode);

                if (afterScript is string)
                    pyscope.Exec(afterScript);
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
            pyscope.Set(nameof(PyIdentifier.__file__), pythonFile ?? string.Empty);
            return pyscope;
        }

        string GetObjectKind(PyObject pyObj)
        {
            if (ManagedType.GetManagedObject(pyObj) is ClassObject co
                    && co.type.Value is Type type)
            {
                if (type.IsEnum)
                    return "enum";

                if (type.IsValueType)
                    return "struct";

                return "class";
            }

            if (Runtime.PyType_Check(pyObj))
                return "class";

            if (Runtime.PyObject_TypeCheck(pyObj, Runtime.PyModuleType))
                return "module";

            if (Runtime.PyObject_TypeCheck(pyObj, Runtime.PyFunctionType))
                return "function";

            if (Runtime.PyObject_TypeCheck(pyObj, Runtime.PyMethodType)
                || Runtime.PyObject_TypeCheck(pyObj, Runtime.PyBoundMethodType))
                return "method";

            if (Runtime.PyObject_TypeCheck(pyObj, Runtime.PyStringType))
                return "text";

            return "value";
        }
        #endregion

        #region Marshalling
        class MarshContext
        {
            readonly Stack<object> _parents = new Stack<object>();

            public bool IsSeen(object parent) => _parents.Contains(parent);

            public void Push(object parent) => _parents.Push(parent);

            public void Pop() => _parents.Pop();
        }

        public object CreateMarshContext() => new MarshContext();

        public object CreatePyList() => new PyList();

        // NOTE:
        // ensures incoming data is marshalled into
        // closest-matching Python objects
        public object MarshInput(object value) => MarshInput(value, new MarshContext());

        public object MarshInput(object value, object context) => MarshInput(value, (MarshContext)context);

        PyObject MarshInput(object value, MarshContext context)
        {
            if (context.IsSeen(value))
            {
                return MarshToPyObject(value);
            }

            if (value is null)
            {
                return PyObject.None;
            }

            if (value is PyObject pyObj)
            {
                return pyObj;
            }

            Type valueType = value.GetType();

            // marshall dotnet lists and dictionaries into python list and dict
            if (IsGenericType(valueType, typeof(List<>)))
            {
                return MarshInputList((IList)value, context);
            }

            if (IsGenericType(valueType, typeof(Dictionary<,>)))
            {
                return MarshInputDict((IDictionary)value, context);
            }

            return MarshToPyObject(value);
        }

        PyObject MarshInputList(IList value, MarshContext context)
        {
            context.Push(value);

            PyList pyList = new PyList();
            foreach (object obj in value)
            {
                pyList.Append(MarshInput(obj, context));
            }

            context.Pop();

            return pyList;
        }

        PyObject MarshInputDict(IDictionary value, MarshContext context)
        {
            context.Push(value);

            PyDict pyDict = new PyDict();
            foreach (object item in value)
            {
                switch (item)
                {
                    case KeyValuePair<object, object> pair:
                        pyDict[MarshInput(pair.Key, context)] = MarshInput(pair.Value, context);
                        break;

                    case DictionaryEntry entry:
                        pyDict[MarshInput(entry.Key, context)] = MarshInput(entry.Value, context);
                        break;
                }
            }

            context.Pop();

            return pyDict;
        }

        PyObject MarshToPyObject(object value)
        {
            using var _value = Converter.ToPythonDetectType(value);
            return PyObject.FromNullableReference(_value.Borrow());
        }

        // NOTE:
        // ensures outgoing data is marshalled into
        // closest-matching dotnet objects
        public object MarshOutput(object value) => MarshOutput(value, new MarshContext());

        public object MarshOutput(object value, object context) => MarshOutput(value, (MarshContext)context);

        object MarshOutput(object value, MarshContext context)
        {
            if (context.IsSeen(value))
            {
                return MarshToPyObject(value);
            }

            switch (value)
            {
                case object[] array:
                    return array.Select(i => MarshOutput(i, context)).ToArray();

                case List<object> list:
                    return MarshOutputList(list, context);

                case Dictionary<object, object> dict:
                    return MarshOutputDict(dict, context);

                case PyObject pyObj:
                    return MarshFromPyObject(pyObj, context);
            }

            return value;
        }

        object MarshFromPyObject(PyObject pyObj, MarshContext context)
        {
            if (context.IsSeen(pyObj))
            {
                return pyObj;
            }

            if (ManagedType.GetManagedObject(pyObj) is CLRObject co)
            {
                if (context.IsSeen(co))
                {
                    return pyObj;
                }

                return MarshOutput(co.inst, context);
            }

            else if (Runtime.PyObject_TYPE(pyObj) == Runtime.PyNoneType)
            {
                return null;
            }

            else if (Runtime.PyList_Check(pyObj))
            {
                var l = new PyList(pyObj);

                context.Push(l);

                var toCLRList = l.Select(i => MarshFromPyObject(i, context)).ToList();

                context.Pop();

                return toCLRList;
            }

            else if (Runtime.PyDict_Check(pyObj))
            {
                var d = new PyDict(pyObj);

                context.Push(d);

                var toCLRDict = d.Keys().ToDictionary(k => MarshFromPyObject(k, context), k => MarshFromPyObject(d[k], context));

                context.Pop();

                return toCLRDict;
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

        object MarshOutputList(List<object> value, MarshContext context)
        {
            context.Push(value);

            var fromClrList = new List<object>();
            foreach (object obj in value)
            {
                fromClrList.Add(MarshOutput(obj, context));
            }

            context.Pop();

            return fromClrList;
        }

        object MarshOutputDict(Dictionary<object, object> value, MarshContext context)
        {
            context.Push(value);

            var fromClrDict = value.Select(p =>
            {
                return new KeyValuePair<object, object>(MarshOutput(p.Key, context), MarshOutput(p.Value, context));
            }).ToDictionary(p => p.Key, p => p.Value);

            context.Pop();

            return fromClrDict;
        }

        static bool IsGenericType(Type type, Type genericType)
        {
            while (type != null && type != typeof(object))
            {
                Type currentType = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
                if (genericType == currentType)
                {
                    return true;
                }

                type = type.BaseType;
            }

            return false;
        }
        #endregion
    }

    [SuppressMessage("Python.Runtime", "IDE1006")]
    public class PythonStream : Stream, IDisposable
    {
        readonly int _fileno = -1;
        Stream _stream = default;

        public PythonStream(Stream stream, int fileno)
        {
            _stream = stream;
            _fileno = fileno;
        }

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

        public int fileno() => _fileno;
        #endregion

        #region Stream
        public Encoding OutputEncoding { get; set; } = Encoding.UTF8;

        public override bool CanRead => false;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => _stream?.Length ?? 0;
        public override long Position { get => _stream?.Position ?? 0; set { if (_stream is null) return; _stream.Position = value; } }
        public override long Seek(long offset, SeekOrigin origin) => _stream?.Seek(offset, origin) ?? 0;
        public override void SetLength(long value) => _stream.SetLength(value);
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override void Write(byte[] buffer, int offset, int count) => _stream?.Write(buffer, offset, count);
        public override void Flush() => _stream?.Flush();
        #endregion

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _stream = default;
            }

            base.Dispose(disposing);
        }
    }
}
#pragma warning restore
