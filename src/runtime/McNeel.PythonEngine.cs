using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Dynamic;

using Python.Runtime.Platform;
using Python.Runtime.Native;

#pragma warning disable CS0612
#pragma warning disable CS8601
#pragma warning disable CS8603
#pragma warning disable CS8604
#pragma warning disable CS8625

namespace Python.Runtime
{
    public unsafe class PythonEngineWrapper
    {
        #region Fields
        // NOTE:
        // PyGILState_Ensure automatically creates threadstate for calling
        // threads. When using sub-interpretters, we need to manage our
        // threadstates manually since we use PyEval_AcquireThread to lock
        readonly ThreadLocal<IntPtr> m_threadState = new ThreadLocal<IntPtr>();
        readonly PyThreadState* m_mainInterpState;
        readonly PyThreadState* m_swapInterpState;
        readonly string[] m_searchPaths = Array.Empty<string>();
        #endregion

        #region Initialization
        public PythonEngineWrapper(string pythonHome, int major, int minor)
        {
            Debug.WriteLine($"CPython engine path: {pythonHome}");

            // setup the darwin loader manually so it can find the native python shared lib
            // this is so less code changes are done the pythonnet source
            string pythonLib;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                pythonLib = $"python{major}{minor}.dll";
            else
                pythonLib = $"libpython{major}.{minor}.dylib";

            string pythonLibPath = Path.Combine(pythonHome, pythonLib);
            LibraryLoader.Instance.Load(pythonLibPath);
            PythonEngine.PythonHome = pythonHome;
            Debug.WriteLine($"Library loader set to: {pythonLibPath}");

            // NOTE:
            // code here is configured to allow creating a new subinterpretter
            // using Runtime.Py_NewInterpreter() and assign to m_swapInterpState.
            // however at this point, pythonnet is not designed to support
            // subinterpretters. see https://github.com/pythonnet/pythonnet/issues/2333
            // under current implementation, BeginInterpretter and EndInterpretter
            // methods are not called so threadstate fields (m_*InterpState)
            // are not used. Once support for subinterpretters is added, you have
            // to decide whether it needs a new instance of this assembly to
            // be loaded in a different netcore LoadContext, or another
            // subinterpretter can be created using this already loaded engine.
            // My assumption is that when ticket above is fixed, there would
            // be new api to create new subinterpretters and GIL object would
            // be modified to know how to handle the subinterpretters.
            PythonEngine.Initialize();
            using (Py.GIL())
            {
                m_mainInterpState = Runtime.PyThreadState_Get();
                // see not above on subinterpretters
                // m_swapInterpState = useSubInterp ? Runtime.Py_NewInterpreter() : m_mainInterpState;
                m_swapInterpState = m_mainInterpState;
                m_threadState.Value = (IntPtr)m_swapInterpState;

                Runtime.InitializeCLR();
                PythonEngine.InitializeCLR();

                // store the default search paths for resetting the engine later
                // it is okay to use main interpretter for this since we do not
                // hold on the python objects for the path items.
                m_searchPaths = GetSearchPaths();

                // see not above on subinterpretters
                // if (useSubInterp)
                // {
                //     // revert back to the main interpretter
                //     Runtime.PyThreadState_Swap(m_mainInterpState);
                // }
            }

            PyObjectConversions.RegisterDecoder(Python.Runtime.Codecs.SequenceDecoder.Instance);
            PyObjectConversions.RegisterDecoder(Python.Runtime.Codecs.IterableDecoder.Instance);
            PyObjectConversions.RegisterDecoder(Python.Runtime.Codecs.ListDecoder.Instance);

            PythonEngine.BeginAllowThreads();

            Debug.WriteLine($"Initialized python engine");
        }
        #endregion

        #region Interpretters
        public void BeginInterpretter()
        {
            if (m_mainInterpState == m_swapInterpState)
            {
                return;
            }

            IntPtr state = m_threadState.Value;
            if (IntPtr.Zero == state)
            {
                m_threadState.Value = (IntPtr)Runtime.PyThreadState_New((PyInterpreterState*)m_swapInterpState->interp);
            }

            Runtime.PyEval_AcquireThread((PyThreadState*)m_threadState.Value);
        }

        public void EndInterpretter()
        {
            if (m_mainInterpState == m_swapInterpState)
            {
                return;
            }

            IntPtr state = m_threadState.Value;
            if (IntPtr.Zero == state)
            {
                return;
            }

            Runtime.PyEval_ReleaseThread((PyThreadState*)m_threadState.Value);
        }
        #endregion

        #region Search Paths
        public string[] GetSearchPaths()
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

            return sysPaths.ToArray();
        }

        public void SetSearchPaths(IEnumerable<string> paths)
        {
            // set sys paths
            PyList sysPaths = RestoreSysPaths(m_searchPaths);

            // manually add PYTHONPATH since we are overwriting the sys paths
            var pythonPath = Environment.GetEnvironmentVariable("PYTHONPATH");
            if (!string.IsNullOrWhiteSpace(pythonPath))
            {
                using var searthPathStr = new PyString(pythonPath);
                sysPaths.Insert(0, searthPathStr);
            }

            // now add the search paths for the script bundle
            foreach (string path in paths.Reverse<string>())
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    using var searthPathStr = new PyString(path);
                    sysPaths.Insert(0, searthPathStr);
                }
            }
        }

        PyList RestoreSysPaths(string[] syspaths)
        {
            var newList = new PyList();
            int i = 0;
            foreach (var searchPath in syspaths ?? new string[] { })
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

        #region Standard IO
        public object SetStdOut(object stream)
        {
            using var sys = Runtime.PyImport_ImportModule("sys");
            using (PyObject sysObj = sys.MoveToPyObject())
            {
                object prevStdout = sysObj.GetAttr("stdout");
                if (stream is PyObject streamObj)
                {
                    sysObj.SetAttr("stdout", streamObj);
                }
                else
                {
                    using var newStdout = PyObject.FromManagedObject(stream);
                    sysObj.SetAttr("stdout", newStdout);
                }
                return prevStdout;
            }
        }

        public object SetStdErr(object stream)
        {
            using var sys = Runtime.PyImport_ImportModule("sys");
            using (PyObject sysObj = sys.MoveToPyObject())
            {
                object prevStderr = sysObj.GetAttr("stderr");

                if (stream is PyObject streamObj)
                {
                    sysObj.SetAttr("stderr", streamObj);
                }
                else
                {
                    using var newStderr = PyObject.FromManagedObject(stream);
                    sysObj.SetAttr("stderr", newStderr);
                }

                return prevStderr;
            }
        }

        public void ResetStdOut()
        {
            using var sys = Runtime.PyImport_ImportModule("sys");
            using (PyObject sysObj = sys.MoveToPyObject())
            {
                sysObj.SetAttr("stdout", sysObj.GetAttr("__stdout__"));
            }
        }

        public void ResetStdErr()
        {
            using var sys = Runtime.PyImport_ImportModule("sys");
            using (PyObject sysObj = sys.MoveToPyObject())
            {
                sysObj.SetAttr("stderr", sysObj.GetAttr("__stderr__"));
            }
        }
        #endregion

        #region Support
        sealed class PyCompletion : Tuple<string, string>, IEquatable<PyCompletion>
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

        public bool TryGetManagedType(object pyObject, out Type managed)
        {
            managed = default;

            switch (pyObject)
            {
                case PyObject pyObj:
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
                    break;

                case Type type:
                    managed = type;
                    return true;
            }

            return false;
        }

        // NOTE:
        // returns Tuple<string, string>
        // index 0: member name
        // index 1: member kind (module, class, struct, function, property)
        // loosly following https://jedi.readthedocs.io/en/latest/docs/api-classes.html#jedi.api.classes.BaseName.type
        public bool TryGetMembers(object pyObject, out IEnumerable<Tuple<string, string>> members, bool privates = true)
        {
            const string COMPLETE_ATTR = "__complete__";
            members = default;

            switch (pyObject)
            {
                case PyObject pyObj:
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
                    break;
            }

            return false;
        }

        public bool TryGetItems(object pyObject, out IEnumerable<object> items)
        {
            items = default;

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
        #endregion

        #region Execution
        // NOTE:
        // this class must be public otherwise returned type from .ParseException
        // will be System.Exception type and called can not lookup .LineNumber
        // by reflecting over the type
        public sealed class PyException : Exception
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

        public bool TryGetExeptionLineNumber(object exception, out int linenum)
        {
            linenum = default;
            if (exception is PyException pyex)
            {
                linenum = pyex.LineNumber;
                return true;
            }
            return false;
        }

        public int GetLineNumber(object frame) => Runtime.PyFrame_GetLineNumber(((PyObject)frame).Handle);

        public object CompileCode(string pythonCode, string pythonFile)
        {
            try
            {
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
                PythonEngine.RunSimpleString(script);
            }
            catch (PythonException pyEx)
            {
                throw new PyException(pyEx);
            }
        }

        public bool HasGIL() => Runtime.PyGILState_Check() != 0;

        public IDisposable GIL() => Py.GIL();

        public object CreateScope(string scopeName, string pythonFile) => PrepareScope(scopeName, pythonFile);

        public void ScopeSet(object scope, string attrName, object attrValue) => ((PyModule)scope).Set(attrName, attrValue);

        public bool ScopeTryGet(object scope, string attrName, out object attrValue)
        {
            attrValue = default;
            if (((PyModule)scope).TryGet(attrName, out PyObject? valueObj))
            {
                attrValue = valueObj;
                return true;
            }
            return false;
        }

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

        public void RunScope(object scope, object code, string pythonFile)
        {
            PyModule pyscope = (PyModule)scope;
            PyObject pycode = (PyObject)code;

            try
            {
                pyscope.Set(nameof(PyIdentifier.__file__), pythonFile ?? string.Empty);
                pyscope.Execute(pycode);
            }
            catch (PythonException pyEx)
            {
                throw new PyException(pyEx);
            }
        }

        public void RunScope(object scope, object code, string pythonFile, string beforeScript, string afterScript)
        {
            PyModule pyscope = (PyModule)scope;
            PyObject pycode = (PyObject)code;

            try
            {
                pyscope.Set(nameof(PyIdentifier.__file__), pythonFile ?? string.Empty);
                pyscope.Exec(beforeScript);
                pyscope.Execute(pycode);
                pyscope.Exec(afterScript);
            }
            catch (PythonException pyEx)
            {
                throw new PyException(pyEx);
            }
        }

        public void DisposeCode(object code)
        {
            (code as PyObject)?.Dispose();
        }

        public void DisposeScope(object scope)
        {
            (scope as PyModule)?.Dispose();
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
        sealed class MarshContext
        {
            readonly Stack<object> _parents = new Stack<object>();

            public bool IsSeen(object parent) => _parents.Contains(parent);

            public void Push(object parent) => _parents.Push(parent);

            public void Pop() => _parents.Pop();
        }

        public object GetManagedObject(object pyObject)
        {
            if (pyObject is PyObject pobj
                    && ManagedType.GetManagedObject(pobj) is CLRObject clrObj
                    && clrObj.inst is object clrInstance)
            {
                return clrInstance;
            }

            return default;
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
}
