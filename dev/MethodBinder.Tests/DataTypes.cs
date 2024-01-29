using System;

namespace MethodBinder.Tests
{
    public class DataA
    {
        public static implicit operator float(DataA _) => 0.0F;
    }

    public class DataB : DataA { }

    public class DataC : DataB { }
}
