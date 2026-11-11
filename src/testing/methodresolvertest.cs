using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Python.Test
{
    // Every overload returns a tag naming its own signature,
    // so Python tests can assert which overload was picked.

    public class ResolverBase { }
    public class ResolverDerived : ResolverBase { }
    public class ResolverMoreDerived : ResolverDerived { }
    public class ResolverUnrelated { }

    public interface IResolverShape { }
    public interface IResolverSolid : IResolverShape { }
    public class ResolverShape : IResolverShape { }
    public class ResolverSolid : IResolverSolid { }

    public enum ResolverColor
    {
        Red = 0,
        Green = 1,
    }

    public class ResolverConvertible
    {
        public static implicit operator double(ResolverConvertible _) => 42.0;
    }

    public struct ResolverPoint
    {
        public double X;
        public double Y;

        public ResolverPoint(double x, double y)
        {
            X = x;
            Y = y;
        }
    }

    /// <summary>Overloads that differ only by number of parameters.</summary>
    public class ResolverArity
    {
        public string Single() => "single";
        public string SingleInt(int a) => "single:int";
        public string SingleThree(int a, double b, string c) => "single:int,double,string";

        public string Count() => "";
        public string Count(int a) => "int";
        public string Count(int a, int b) => "int,int";
        public string Count(int a, int b, int c) => "int,int,int";

        public string ZeroOrOptional() => "";
        public string ZeroOrOptional(int a = 1) => "int=";

        public string OnlyTwo(int a, int b) => "int,int";
    }

    /// <summary>Overloads that differ by primitive type.</summary>
    public class ResolverPrimitives
    {
        public string Pick(bool a) => "bool";
        public string Pick(byte a) => "byte";
        public string Pick(short a) => "short";
        public string Pick(int a) => "int";
        public string Pick(long a) => "long";
        public string Pick(float a) => "float";
        public string Pick(double a) => "double";
        public string Pick(decimal a) => "decimal";
        public string Pick(char a) => "char";
        public string Pick(string a) => "string";
        public string Pick(object a) => "object";

        public string IntOrDouble(int a) => "int";
        public string IntOrDouble(double a) => "double";

        public string LongOrString(long a) => "long";
        public string LongOrString(string a) => "string";

        public string FloatOrDouble(float a) => "float";
        public string FloatOrDouble(double a) => "double";

        public string ShortOrLong(short a) => "short";
        public string ShortOrLong(long a) => "long";

        public string StringOrObject(string a) => "string";
        public string StringOrObject(object a) => "object";

        public string BoolOrInt(bool a) => "bool";
        public string BoolOrInt(int a) => "int";

        public string SignedOrUnsigned(sbyte a) => "sbyte";
        public string SignedOrUnsigned(byte a) => "byte";

        public string IntOrString(int a) => "int";
        public string IntOrString(string a) => "string";

        public string FloatOrInt(float a) => "float";
        public string FloatOrInt(int a) => "int";

        public string OnlyDouble(double a) => "double:" + a.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public string OnlyFloat(float a) => "float:" + a.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public string OnlyByte(byte a) => "byte:" + a;
        public string OnlyLong(long a) => "long:" + a;
        public string OnlyString(string a) => "string:" + (a ?? "<null>");
        public string OnlyObject(object a) => "object:" + (a?.GetType().Name ?? "<null>");

        public string Pair(short a, short b) => "short,short";
        public string Pair(int a, int b) => "int,int";
        public string Pair(long a, long b) => "long,long";

        public string Mixed(int a, double b) => "int,double";
        public string Mixed(double a, int b) => "double,int";
        public string Mixed(string a, string b) => "string,string";
    }

    /// <summary>Overloads that differ by class or interface in a hierarchy.</summary>
    public class ResolverHierarchy
    {
        public string Pick(object a) => "object";
        public string Pick(ResolverBase a) => "base";
        public string Pick(ResolverDerived a) => "derived";

        public string BaseOnly(ResolverBase a) => "base";

        public string BaseOrObject(object a) => "object";
        public string BaseOrObject(ResolverBase a) => "base";

        public string Shape(object a) => "object";
        public string Shape(IResolverShape a) => "shape";
        public string Shape(IResolverSolid a) => "solid";

        public string Two(ResolverBase a, ResolverBase b) => "base,base";
        public string Two(ResolverDerived a, ResolverBase b) => "derived,base";
        public string Two(ResolverDerived a, ResolverDerived b) => "derived,derived";

        public string Point(ResolverPoint a) => "point";
        public string Point(ResolverPoint a, ResolverPoint b) => "point,point";
        public string Point(double x, double y) => "double,double";

        public string Color(ResolverColor a) => "color";
        public string Color(string a) => "string";

        public string Convertible(double a) => "double:" + a.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public string NullRef(string a) => "string";
        public string NullRef(int a) => "int";

        public string NullOnlyBase(ResolverBase a) => a is null ? "null" : "base";
    }

    /// <summary>Optional parameters and keyword arguments.</summary>
    public class ResolverOptional
    {
        public string Opt(int a) => "int";
        public string Opt(int a, int b = 5) => "int,int=" + b;

        public string Named(int a, int b = 2, string c = "c") => $"a={a},b={b},c={c}";

        public string Kw(int count) => "count";
        public string Kw(string name) => "name";
        public string Kw(int count, string name) => "count,name";

        public string Attr(int a, [Optional] int b) => "int,[opt]int=" + b;

        public string Defaults(int a = 1, double b = 2.5, string c = "x", bool d = true, object e = null)
            => $"{a},{b.ToString(System.Globalization.CultureInfo.InvariantCulture)},{c},{d},{e ?? "null"}";

        public string FloatOrDoubleOpt(double a) => "double";
        public string FloatOrDoubleOpt(float a, float b = 0) => "float,float=";
    }

    /// <summary>params arrays.</summary>
    public class ResolverParams
    {
        public string Ints(params int[] values) => "params int:" + values.Length;

        public string Lead(string lead, params object[] values) => "string,params object:" + values.Length;

        public string Prefer(int a) => "int";
        public string Prefer(int a, int b) => "int,int";
        public string Prefer(params int[] values) => "params int:" + values.Length;

        public string Typed(params int[] values) => "params int:" + values.Length;
        public string Typed(params string[] values) => "params string:" + values.Length;

        public int Sum(params int[] values) => values.Sum();
    }

    /// <summary>out and ref parameters.</summary>
    public class ResolverOutRef
    {
        public bool TryGet(int key, out string value)
        {
            value = "v" + key;
            return key >= 0;
        }

        public void OutOnly(out int value)
        {
            value = 7;
        }

        public void TwoOuts(int a, out int doubled, out int tripled)
        {
            doubled = a * 2;
            tripled = a * 3;
        }

        public int Ref(ref int value)
        {
            value += 1;
            return value * 10;
        }

        public string Over(int a) => "int";

        public string Over(int a, out int b)
        {
            b = a;
            return "int,out int";
        }

        public string Over(int a, int b, int c) => "int,int,int";

        public string InArray([In] byte[] data, long offset) => "[in]byte[],long:" + (data?.Length.ToString() ?? "null");
    }

    /// <summary>Generic methods next to plain ones.</summary>
    public class ResolverGeneric
    {
        public string Echo(int a) => "int";
        public string Echo<T>(T a) => "T:" + typeof(T).Name;

        public string OnlyT<T>(T a) => "T:" + typeof(T).Name;

        public string Second<T>(int a, T b) => "int,T:" + typeof(T).Name;

        public string Two<T, U>(T a, U b) => $"T:{typeof(T).Name},U:{typeof(U).Name}";

        public string Explicit<T>() => "T:" + typeof(T).Name;

        public string Arr(double[] a) => "double[]";
        public string Arr<T>(T[] a) => "T[]:" + typeof(T).Name;

        public string Constrained<T>(T a) where T : class => "T:" + typeof(T).Name;
        public string Constrained(long a) => "long";

        public string ValueOnly<T>(T a) where T : struct => "T:" + typeof(T).Name;
        public string ValueOnly(object a) => "object";
    }

    /// <summary>Static and instance overloads sharing a name.</summary>
    public class ResolverStatic
    {
        public string Mix(int a) => "instance:int";
        public static string Mix(string a) => "static:string";
        public static string Mix(int a, int b) => "static:int,int";

        public static string OnlyStatic(int a) => "static:int";
        public static string OnlyStatic(double a) => "static:double";
        public static string OnlyStatic(string a, int b = 0) => "static:string,int=";
    }

    /// <summary>Derived type adding overloads to an inherited method group.</summary>
    public class ResolverInheritBase
    {
        public string Pick(int a) => "base:int";
        public string Pick(string a) => "base:string";
        public virtual string Virt(int a) => "base:int";
    }

    public class ResolverInheritDerived : ResolverInheritBase
    {
        public string Pick(double a) => "derived:double";
        public override string Virt(int a) => "derived:int";
        public string Virt(string a) => "derived:string";
    }

    /// <summary>Arrays and sequences.</summary>
    public class ResolverSequences
    {
        public string Arr(int[] a) => "int[]:" + (a?.Length.ToString() ?? "null");
        public string Arr(double[] a) => "double[]:" + (a?.Length.ToString() ?? "null");
        public string Arr(string[] a) => "string[]:" + a.Length;

        public string OnlyIntArr(int[] a) => "int[]:" + (a?.Length.ToString() ?? "null");
        public string OnlyEnumerable(IEnumerable<int> a) => "IEnumerable<int>:" + a.Count();
        public string OnlyList(IList<int> a) => "IList<int>:" + (a?.Count.ToString() ?? "null");

        public string ListOrEnumerable(IList<int> a) => "IList<int>";
        public string ListOrEnumerable(IEnumerable<int> a) => "IEnumerable<int>";

        public string ArrOrScalar(double a) => "double";
        public string ArrOrScalar(double[] a) => "double[]:" + (a?.Length.ToString() ?? "null");
    }

    /// <summary>Constructor overloads.</summary>
    public class ResolverCtor
    {
        public string Tag { get; }

        public ResolverCtor() { Tag = ""; }
        public ResolverCtor(int a) { Tag = "int"; }
        public ResolverCtor(double a) { Tag = "double"; }
        public ResolverCtor(string a) { Tag = "string"; }
        public ResolverCtor(int a, string b) { Tag = "int,string"; }
        public ResolverCtor(ResolverBase a) { Tag = "base"; }
        public ResolverCtor(int a, int b, int c = 3) { Tag = "int,int,int=" + c; }
    }

    /// <summary>Operator overloads.</summary>
    public class ResolverVec
    {
        public double X { get; }

        public ResolverVec(double x) { X = x; }

        // The sweep passes None here; reading .X of null would crash the whole host on Linux.
        static double Of(ResolverVec v) => v?.X ?? double.NaN;

        public static ResolverVec operator +(ResolverVec a, ResolverVec b) => new ResolverVec(Of(a) + Of(b));
        public static ResolverVec operator +(ResolverVec a, double b) => new ResolverVec(Of(a) + b + 1000);
        public static ResolverVec operator +(double a, ResolverVec b) => new ResolverVec(a + Of(b) + 2000);
        public static ResolverVec operator *(ResolverVec a, double b) => new ResolverVec(Of(a) * b);
        public static ResolverVec operator *(ResolverVec a, ResolverVec b) => new ResolverVec(Of(a) * Of(b) + 0.5);
        public static ResolverVec operator -(ResolverVec a) => new ResolverVec(-Of(a));
        public static ResolverVec operator -(ResolverVec a, ResolverVec b) => new ResolverVec(Of(a) - Of(b));
        public static ResolverVec operator /(ResolverVec a, double b) => new ResolverVec(Of(a) / b);
        public static bool operator ==(ResolverVec a, ResolverVec b) => Of(a) == Of(b);
        public static bool operator !=(ResolverVec a, ResolverVec b) => Of(a) != Of(b);
        public override bool Equals(object obj) => obj is ResolverVec other && other.X == X;
        public override int GetHashCode() => X.GetHashCode();
        public static bool operator <(ResolverVec a, ResolverVec b) => Of(a) < Of(b);
        public static bool operator >(ResolverVec a, ResolverVec b) => Of(a) > Of(b);
    }

    /// <summary>Calls that a C# compiler would report as ambiguous.</summary>
    public class ResolverAmbiguous
    {
        public string Iface(IResolverShape a) => "shape";
        public string Iface(IDisposable a) => "disposable";

        public string OptTie(int a = 0) => "int=";
        public string OptTie(string a = "") => "string=";
    }

    public class ResolverShapeDisposable : IResolverShape, IDisposable
    {
        public void Dispose() { }
    }

    /// <summary>A large overload set, to measure how cost grows with overload count.</summary>
    public class ResolverWide
    {
        public string Wide() => "";
        public string Wide(bool a) => "bool";
        public string Wide(int a) => "int";
        public string Wide(double a) => "double";
        public string Wide(string a) => "string";
        public string Wide(ResolverBase a) => "base";
        public string Wide(ResolverDerived a) => "derived";
        public string Wide(ResolverPoint a) => "point";
        public string Wide(int[] a) => "int[]";
        public string Wide(int a, int b) => "int,int";
        public string Wide(int a, double b) => "int,double";
        public string Wide(double a, double b) => "double,double";
        public string Wide(string a, int b) => "string,int";
        public string Wide(string a, string b) => "string,string";
        public string Wide(ResolverBase a, int b) => "base,int";
        public string Wide(ResolverPoint a, ResolverPoint b) => "point,point";
        public string Wide(int a, int b, int c) => "int,int,int";
        public string Wide(double a, double b, double c) => "double,double,double";
        public string Wide(string a, int b, double c) => "string,int,double";
        public string Wide(ResolverPoint a, ResolverPoint b, ResolverPoint c) => "point,point,point";
        public string Wide(ResolverBase a, ResolverBase b, ResolverBase c) => "base,base,base";
        public string Wide(int a, int b, int c, int d) => "int,int,int,int";
        public string Wide(double a, double b, double c, double d) => "double,double,double,double";
        public string Wide(string a, string b, string c, string d) => "string,string,string,string";
    }

    /// <summary>Delegate-typed parameters, filled from python callables.</summary>
    public class ResolverDelegates
    {
        public delegate int IntTransform(int value);

        public string OnlyAction(Action a) { a?.Invoke(); return a == null ? "Action:null" : "Action"; }
        public string OnlyFunc(Func<int, int> f) => "Func<int,int>:" + (f == null ? "null" : f(2).ToString());
        public string OnlyCustom(IntTransform f) => "IntTransform:" + (f == null ? "null" : f(3).ToString());

        public string ActionOrInt(Action a) => "Action";
        public string ActionOrInt(int a) => "int";

        public string FuncOrString(Func<int> f) => "Func<int>:" + f();
        public string FuncOrString(string a) => "string";
    }

    /// <summary>Nullable value type parameters.</summary>
    public class ResolverNullable
    {
        public string Only(int? a) => "int?:" + (a.HasValue ? a.Value.ToString() : "<null>");

        public string IntOrNullable(int a) => "int";
        public string IntOrNullable(int? a) => "int?";

        public string NullableOrString(int? a) => "int?";
        public string NullableOrString(string a) => "string";
    }

    /// <summary>Collections and python objects as parameters.</summary>
    public class ResolverCollections
    {
        public string OnlyBytes(byte[] a) => "byte[]:" + (a?.Length.ToString() ?? "null");
        public string OnlyPyObject(Python.Runtime.PyObject a) => "PyObject";
        public string PyObjectOrInt(Python.Runtime.PyObject a) => "PyObject";
        public string PyObjectOrInt(int a) => "int";
        public string OnlyBig(System.Numerics.BigInteger a) => "BigInteger:" + a;
    }

    /// <summary>A method hidden with `new`, and an explicit interface member.</summary>
    public interface IResolverExplicit
    {
        string Hidden(int a);
        string Explicit(int a);
    }

    public class ResolverHideBase
    {
        public string Hidden(int a) => "base:int";
        public static string StaticHidden(int a) => "base:static";
    }

    public class ResolverHideDerived : ResolverHideBase, IResolverExplicit
    {
        public new string Hidden(int a) => "derived:int";
        public static new string StaticHidden(int a) => "derived:static";
        string IResolverExplicit.Explicit(int a) => "explicit:int";
        public string Explicit(string a) => "public:string";
    }

    /// <summary>Overloaded indexers on one type.</summary>
    public class ResolverIndexer
    {
        readonly Dictionary<string, string> _store = new();

        public string this[int key]
        {
            get => "get:int:" + key;
            set => _store["int"] = value;
        }

        public string this[string key]
        {
            get => "get:string:" + key;
            set => _store["string"] = value;
        }

        public string this[int a, int b]
        {
            get => "get:int,int";
            set => _store["int,int"] = value;
        }

        public string Stored(string key) => _store.TryGetValue(key, out var v) ? v : "<none>";
    }

    /// <summary>Indexer whose setter value type picks the overload.</summary>
    public class ResolverIndexerValue
    {
        public string Last = "";

        public object this[int key]
        {
            get => Last;
            set => Last = "set:object:" + value;
        }

        public double this[double key]
        {
            get => key;
            set => Last = "set:double:" + value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Events wired through their add_/remove_ accessors.</summary>
    public class ResolverEvents
    {
        public event EventHandler Changed;
        public static event EventHandler StaticChanged;

        public int Fire()
        {
            int count = Changed?.GetInvocationList().Length ?? 0;
            Changed?.Invoke(this, EventArgs.Empty);
            return count;
        }

        public static int FireStatic()
        {
            int count = StaticChanged?.GetInvocationList().Length ?? 0;
            StaticChanged?.Invoke(null, EventArgs.Empty);
            return count;
        }
    }

    /// <summary>Delegate types invoked directly from python.</summary>
    public delegate string ResolverDoubleDelegate(double a, int b);
    public delegate bool ResolverOutDelegate(int a, out int b);

    public static class ResolverDelegateFactory
    {
        public static ResolverDoubleDelegate Double() => (a, b) => $"double,int:{a.ToString(System.Globalization.CultureInfo.InvariantCulture)},{b}";
        public static ResolverOutDelegate Out() => (int a, out int b) => { b = a * 2; return true; };
    }

    /// <summary>Operators beyond the basics: unary, comparison, inherited, mixed operands.</summary>
    public class ResolverOpBase
    {
        public int V { get; }

        public ResolverOpBase(int v) { V = v; }

        // The sweep passes None here; reading .V of null would crash the whole host on Linux.
        static int Of(ResolverOpBase o) => o?.V ?? -1;

        public static ResolverOpBase operator +(ResolverOpBase a, int b) => new ResolverOpBase(Of(a) + b);
        public static ResolverOpBase operator +(ResolverOpBase a, string b) => new ResolverOpBase(Of(a) + (b?.Length ?? -1) * 100);
        public static ResolverOpBase operator +(int a, ResolverOpBase b) => new ResolverOpBase(a + Of(b) + 1000);
        public static ResolverOpBase operator -(ResolverOpBase a) => new ResolverOpBase(-Of(a));
        public static ResolverOpBase operator +(ResolverOpBase a) => new ResolverOpBase(Of(a) + 1);
        public static ResolverOpBase operator ~(ResolverOpBase a) => new ResolverOpBase(~Of(a));
        public static ResolverOpBase operator %(ResolverOpBase a, int b) => new ResolverOpBase(Of(a) % b);
        public static ResolverOpBase operator &(ResolverOpBase a, ResolverOpBase b) => new ResolverOpBase(Of(a) & Of(b));
        public static ResolverOpBase operator |(ResolverOpBase a, ResolverOpBase b) => new ResolverOpBase(Of(a) | Of(b));
        public static ResolverOpBase operator ^(ResolverOpBase a, ResolverOpBase b) => new ResolverOpBase(Of(a) ^ Of(b));
        public static ResolverOpBase operator <<(ResolverOpBase a, int b) => new ResolverOpBase(Of(a) << b);
        public static ResolverOpBase operator >>(ResolverOpBase a, int b) => new ResolverOpBase(Of(a) >> b);
        public static bool operator <(ResolverOpBase a, ResolverOpBase b) => Of(a) < Of(b);
        public static bool operator >(ResolverOpBase a, ResolverOpBase b) => Of(a) > Of(b);
        public static bool operator <=(ResolverOpBase a, ResolverOpBase b) => Of(a) <= Of(b);
        public static bool operator >=(ResolverOpBase a, ResolverOpBase b) => Of(a) >= Of(b);
    }

    public class ResolverOpDerived : ResolverOpBase
    {
        public ResolverOpDerived(int v) : base(v) { }
    }

    /// <summary>Constructor shapes beyond plain overloads.</summary>
    public class ResolverCtorShapes
    {
        public string Tag { get; }

        public ResolverCtorShapes(params int[] values) { Tag = "params int:" + (values?.Length.ToString() ?? "null"); }
        public ResolverCtorShapes(string name, double scale = 1.0) { Tag = $"string,double={scale.ToString(System.Globalization.CultureInfo.InvariantCulture)}"; }
        public ResolverCtorShapes(Func<int> f) { Tag = "Func<int>:" + (f == null ? "null" : f().ToString()); }
    }

    public struct ResolverCtorStruct
    {
        public string Tag;

        public ResolverCtorStruct(int a) { Tag = "int"; }
    }

    public abstract class ResolverCtorAbstract
    {
        protected ResolverCtorAbstract() { }
    }

    public class ResolverCtorGeneric<T>
    {
        public string Tag { get; }

        public ResolverCtorGeneric(T value) { Tag = "T:" + typeof(T).Name; }
        public ResolverCtorGeneric(int count, T value) { Tag = "int,T:" + typeof(T).Name; }
    }
}
