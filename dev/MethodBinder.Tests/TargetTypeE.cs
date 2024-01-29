#pragma warning disable CA1822
using System;
using System.Numerics;

namespace MethodBinder.Tests
{
    using O = System.Runtime.InteropServices.OptionalAttribute;
    using D = System.Runtime.InteropServices.DefaultParameterValueAttribute;

    public class TargetTypeE
    {
        // 2
        public Guid
        Foo(short _1 = 0)
        {
            return new Guid("5755aedc-a11d-11ee-8c90-0242ac120002");
        }

        public Guid
        Foo(int _1 = 0, int _2 = 0)
        {
            return new Guid("5755afea-a11d-11ee-8c90-0242ac120002");
        }

        public Guid
        Foo(long _1 = 0, long _2 = 0)
        {
            return new Guid("5755b2b0-a11d-11ee-8c90-0242ac120002");
        }
    }
}
