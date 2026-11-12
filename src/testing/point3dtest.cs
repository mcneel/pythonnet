using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;

// Mirrors the shape of RhinoCommon's Point3d (attributes, interfaces, constructors,
// operators, indexer) without its native dependencies, for tests/test_perf.py.
namespace Python.Test.RhinoLike
{
    public interface IEpsilonComparable<T> { bool EpsilonEquals(T other, double epsilon); }
    public interface IValidable { bool IsValid { get; } }

    public struct Vector3d { public double X, Y, Z; public Vector3d(double x, double y, double z) { X = x; Y = y; Z = z; } }
    public struct Vector3f { public float X, Y, Z; }
    public struct Point3f { public float X, Y, Z; }
    public struct Point4d { public double X, Y, Z, W; }
    public struct ControlPoint { public Point3d Location; }

    [StructLayout(LayoutKind.Sequential, Pack = 8, Size = 24)]
    [DebuggerDisplay("({m_x}, {m_y}, {m_z})")]
    [Serializable]
    public struct Point3d :
        ISerializable, IEquatable<Point3d>, IComparable<Point3d>, IComparable,
        IEpsilonComparable<Point3d>, ICloneable, IValidable, IFormattable
    {
        internal double m_x;
        internal double m_y;
        internal double m_z;

        public Point3d(double x, double y, double z) { m_x = x; m_y = y; m_z = z; }
        public Point3d(Vector3d vector) { m_x = vector.X; m_y = vector.Y; m_z = vector.Z; }
        public Point3d(Point3f point) { m_x = point.X; m_y = point.Y; m_z = point.Z; }
        public Point3d(Point3d point) { m_x = point.X; m_y = point.Y; m_z = point.Z; }
        public Point3d(Point4d point)
        {
            double w = (Math.Abs(point.W - 1.0) > double.Epsilon && point.W != 0.0) ? 1.0 / point.W : 1.0;
            m_x = point.X * w; m_y = point.Y * w; m_z = point.Z * w;
        }

        public static Point3d Origin => new Point3d(0, 0, 0);
        public static Point3d Unset => new Point3d(-1.23432101234321e+308, -1.23432101234321e+308, -1.23432101234321e+308);

        private Point3d(SerializationInfo info, StreamingContext context)
        {
            m_x = info.GetDouble("X"); m_y = info.GetDouble("Y"); m_z = info.GetDouble("Z");
        }

        void ISerializable.GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("X", m_x); info.AddValue("Y", m_y); info.AddValue("Z", m_z);
        }

        public static Point3d operator *(Point3d point, double t) => new Point3d(point.m_x * t, point.m_y * t, point.m_z * t);
        public static Point3d Multiply(Point3d point, double t) => point * t;
        public static Point3d operator *(double t, Point3d point) => point * t;
        public static Point3d Multiply(double t, Point3d point) => point * t;
        public static Point3d operator /(Point3d point, double t) => point * (1.0 / t);
        public static Point3d Divide(Point3d point, double t) => point / t;
        public static Point3d operator +(Point3d a, Point3d b) => new Point3d(a.m_x + b.m_x, a.m_y + b.m_y, a.m_z + b.m_z);
        public static Point3d Add(Point3d a, Point3d b) => a + b;
        public static Point3d operator +(Point3d p, Vector3d v) => new Point3d(p.m_x + v.X, p.m_y + v.Y, p.m_z + v.Z);
        public static Point3d Add(Point3d p, Vector3d v) => p + v;
        public static Point3d operator +(Point3d p, Vector3f v) => new Point3d(p.m_x + v.X, p.m_y + v.Y, p.m_z + v.Z);
        public static Point3d Add(Point3d p, Vector3f v) => p + v;
        public static Point3d operator +(Vector3d v, Point3d p) => p + v;
        public static Point3d Add(Vector3d v, Point3d p) => p + v;
        public static Point3d operator -(Point3d p, Vector3d v) => new Point3d(p.m_x - v.X, p.m_y - v.Y, p.m_z - v.Z);
        public static Point3d Subtract(Point3d p, Vector3d v) => p - v;
        public static Vector3d operator -(Point3d a, Point3d b) => new Vector3d(a.m_x - b.m_x, a.m_y - b.m_y, a.m_z - b.m_z);
        public static Vector3d Subtract(Point3d a, Point3d b) => a - b;
        public static Point3d operator -(Point3d p) => new Point3d(-p.m_x, -p.m_y, -p.m_z);
        public static Point3d Negate(Point3d p) => -p;
        public static bool operator ==(Point3d a, Point3d b) => a.m_x == b.m_x && a.m_y == b.m_y && a.m_z == b.m_z;
        public static bool operator !=(Point3d a, Point3d b) => !(a == b);
        public static implicit operator ControlPoint(Point3d pt) => new ControlPoint { Location = pt };
        public static explicit operator Vector3d(Point3d p) => new Vector3d(p.m_x, p.m_y, p.m_z);
        public static explicit operator Point3d(Vector3d v) => new Point3d(v);
        public static implicit operator Point3d(Point3f p) => new Point3d(p);
        public static Point3d FromPoint3f(Point3f p) => new Point3d(p);
        public static bool operator <(Point3d a, Point3d b) => a.CompareTo(b) < 0;
        public static bool operator <=(Point3d a, Point3d b) => a.CompareTo(b) <= 0;
        public static bool operator >(Point3d a, Point3d b) => a.CompareTo(b) > 0;
        public static bool operator >=(Point3d a, Point3d b) => a.CompareTo(b) >= 0;

        public double X { get => m_x; set => m_x = value; }
        public double Y { get => m_y; set => m_y = value; }
        public double Z { get => m_z; set => m_z = value; }

        public double this[int index]
        {
            get => index switch { 0 => m_x, 1 => m_y, 2 => m_z, _ => throw new IndexOutOfRangeException() };
            set { switch (index) { case 0: m_x = value; break; case 1: m_y = value; break; case 2: m_z = value; break; default: throw new IndexOutOfRangeException(); } }
        }

        public bool IsValid => !double.IsNaN(m_x) && !double.IsNaN(m_y) && !double.IsNaN(m_z);
        public double MinimumCoordinate => Math.Min(m_x, Math.Min(m_y, m_z));
        public double MaximumCoordinate => Math.Max(m_x, Math.Max(m_y, m_z));

        public override bool Equals(object obj) => obj is Point3d p && this == p;
        public bool EpsilonEquals(Point3d other, double epsilon)
            => Math.Abs(m_x - other.m_x) <= epsilon && Math.Abs(m_y - other.m_y) <= epsilon && Math.Abs(m_z - other.m_z) <= epsilon;
        public int CompareTo(Point3d other)
            => m_x != other.m_x ? m_x.CompareTo(other.m_x) : m_y != other.m_y ? m_y.CompareTo(other.m_y) : m_z.CompareTo(other.m_z);
        int IComparable.CompareTo(object obj) => obj is Point3d p ? CompareTo(p) : throw new ArgumentException();
        public bool Equals(Point3d point) => this == point;
        public override int GetHashCode() => m_x.GetHashCode() ^ m_y.GetHashCode() ^ m_z.GetHashCode();
        public object Clone() => this;
        public void Interpolate(Point3d pA, Point3d pB, double t)
        {
            m_x = pA.m_x + t * (pB.m_x - pA.m_x); m_y = pA.m_y + t * (pB.m_y - pA.m_y); m_z = pA.m_z + t * (pB.m_z - pA.m_z);
        }
        public override string ToString() => $"{m_x},{m_y},{m_z}";
        public string ToString(string format, IFormatProvider formatProvider) => ToString();
        public double DistanceTo(Point3d other) => Math.Sqrt(DistanceToSquared(other));
        public double DistanceToSquared(Point3d other)
        {
            double dx = other.m_x - m_x, dy = other.m_y - m_y, dz = other.m_z - m_z;
            return dx * dx + dy * dy + dz * dz;
        }
    }
}
