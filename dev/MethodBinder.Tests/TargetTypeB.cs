using System;

namespace MethodBinder.Tests
{
    using O = System.Runtime.InteropServices.OptionalAttribute;
    using D = System.Runtime.InteropServices.DefaultParameterValueAttribute;

    public class TargetTypeB
    {
        // 1
        public Guid
        Foo(float[] _1)
        {
            return new Guid("57559924-a11d-11ee-8c90-0242ac120002");
        }

        public Guid
        Foo(float _1)
        {
            return new Guid("57559802-a11d-11ee-8c90-0242ac120002");
        }

        public Guid
        Foo(DataA _1)
        {
            return new Guid("57559bc2-a11d-11ee-8c90-0242ac120002");
        }

        public Guid
        Foo(DataB _1)
        {
            return new Guid("57559ea6-a11d-11ee-8c90-0242ac120002");
        }

        // 2
        public Guid
        Foo(float[] _1, float[] _2)
        {
            return new Guid("575596e0-a11d-11ee-8c90-0242ac120002");
        }

        public Guid
        Foo(float _1, float _2)
        {
            return new Guid("57559a96-a11d-11ee-8c90-0242ac120002");
        }
    }
}
