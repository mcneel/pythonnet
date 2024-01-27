#pragma warning disable CA1822
using System;
using System.Numerics;

namespace MethodBinder.Tests
{
    using O = System.Runtime.InteropServices.OptionalAttribute;
    using D = System.Runtime.InteropServices.DefaultParameterValueAttribute;

    public class TargetTypeF
    {
        public Guid
        Foo(out Complex _1)
        {
            _1 = default;
            return new Guid("5755b4d6-a11d-11ee-8c90-0242ac120002");
        }

        public Guid
        Foo(out Complex _1, double _2)
        {
            _1 = default;
            return new Guid("5755b5e4-a11d-11ee-8c90-0242ac120002");
        }

        public Guid
        Foo(DateTime _1, out Complex _2)
        {
            _2 = default;
            return new Guid("5755b6f2-a11d-11ee-8c90-0242ac120002");
        }

        public Guid
        Foo(DateTime _1, out Complex _2, double _3)
        {
            _2 = default;
            return new Guid("5755b800-a11d-11ee-8c90-0242ac120002");
        }
    }
}
