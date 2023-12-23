using System;

namespace MethodBinderTests
{
    using O = System.Runtime.InteropServices.OptionalAttribute;
    using D = System.Runtime.InteropServices.DefaultParameterValueAttribute;

    public class TargetTypeB
    {
        // 2
        public Guid
        Foo(float _1, float _2)
        {
            return new Guid("57557368-a11d-11ee-8c90-0242ac120002");
        }
    }
}
