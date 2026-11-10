using System;

namespace Python.Test
{
    // Types for tests that add python mixins by assigning __bases__ on CLR types.
    // Clone() is only reachable through ICloneable, so python sees it only after a mixin adds it.

    public struct BasesTestPoint : ICloneable
    {
        public double X;
        public double Y;
        public double Z;

        public BasesTestPoint(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        object ICloneable.Clone() => this;
    }

    public struct BasesTestVector : ICloneable
    {
        public double X;
        public double Y;
        public double Z;

        public BasesTestVector(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        object ICloneable.Clone() => this;
    }

    public class BasesTestClass : ICloneable
    {
        public int Value;

        public BasesTestClass(int value)
        {
            Value = value;
        }

        object ICloneable.Clone() => new BasesTestClass(Value);
    }

    public static class BasesTestMethods
    {
        public static BasesTestVector EchoVector(BasesTestVector v) => v;

        public static double SumVector(BasesTestVector v) => v.X + v.Y + v.Z;
    }
}
