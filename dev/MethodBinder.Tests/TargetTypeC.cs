using System;

namespace MethodBinder.Tests
{
    using O = System.Runtime.InteropServices.OptionalAttribute;
    using D = System.Runtime.InteropServices.DefaultParameterValueAttribute;

    public class TargetTypeC
    {
        // 1
        public Guid
        Foo(double _1)
        {
            return new Guid("57559fdc-a11d-11ee-8c90-0242ac120002");
        }

        public Guid
        Foo(int _1, double _2 = 0)
        {
            return new Guid("5755a0f4-a11d-11ee-8c90-0242ac120002");
        }

        public Guid
        Foo<T>(int _1, T? _2 = default) where T : class
        {
            return new Guid("5755a216-a11d-11ee-8c90-0242ac120002");
        }

        // any
        public Guid
        Foo(object _1, params int[] _2)
        {
            return new Guid("5755a680-a11d-11ee-8c90-0242ac120002");
        }
    }
}
