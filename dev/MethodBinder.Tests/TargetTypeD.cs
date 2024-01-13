#pragma warning disable CA1822
using System;

namespace MethodBinder.Tests
{
    using O = System.Runtime.InteropServices.OptionalAttribute;
    using D = System.Runtime.InteropServices.DefaultParameterValueAttribute;

    public class TargetTypeD
    {
        // 2
        public Guid
        Foo(short _1, short _2)
        {
            return new Guid("5755a950-a11d-11ee-8c90-0242ac120002");
        }

        public Guid
        Foo(int _1, int _2)
        {
            return new Guid("5755aa86-a11d-11ee-8c90-0242ac120002");
        }

        public Guid
        Foo(long _1, long _2)
        {
            return new Guid("5755adc4-a11d-11ee-8c90-0242ac120002");
        }
    }
}
