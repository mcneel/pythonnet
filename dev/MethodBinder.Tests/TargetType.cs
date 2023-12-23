using System;
using System.Runtime.InteropServices;

namespace MethodBinderTests
{
    public class TargetType
    {
        // 0
        public Guid
        Foo()
        {
            return new Guid("57557368-a11d-11ee-8c90-0242ac120002");
        }

        // 1
        public Guid
        Foo(int _1)
        {
            return new Guid("5755762e-a11d-11ee-8c90-0242ac120002");
        }

        // 1
        public Guid
        Foo(ref int _1)
        {
            return new Guid("57557be2-a11d-11ee-8c90-0242ac120002");
        }

        // 1
        public Guid
        Foo(int _1, out int _2)
        {
            _2 = _1;
            return new Guid("57557d40-a11d-11ee-8c90-0242ac120002");
        }

        // 1
        public Guid
        Foo(double _1)
        {
            return new Guid("575577c8-a11d-11ee-8c90-0242ac120002");
        }

        // 1
        static public Guid
        Foo<T>(T[] _1)
        {
            return new Guid("57558e98-a11d-11ee-8c90-0242ac120002");
        }

        // 1
        public Guid
        Foo(double[] _1)
        {
            return new Guid("57558bdc-a11d-11ee-8c90-0242ac120002");
        }

        // 2
        public Guid
        Foo(int _1, int _2)
        {
            return new Guid("57557e6c-a11d-11ee-8c90-0242ac120002");
        }

        // 2
        public Guid
        Foo(int _1, out int _2, int _3)
        {
            _2 = _1 + _3;
            return new Guid("57557f7a-a11d-11ee-8c90-0242ac120002");
        }

        // 2
        public Guid
        Foo(float _1, float _2)
        {
            return new Guid("57558402-a11d-11ee-8c90-0242ac120002");
        }

        // 1, 2
        static public Guid
        Foo(double[] _1, bool _2 = false)
        {
            return new Guid("57558fd8-a11d-11ee-8c90-0242ac120002");
        }

        // 1, 2
        public Guid
        Foo<T>(int _1, [Optional] T _2)
        {
            return new Guid("575590fa-a11d-11ee-8c90-0242ac120002");
        }

        // 1, 2
        public Guid
        Foo(int _1, bool _2 = false)
        {
            return new Guid("575580a6-a11d-11ee-8c90-0242ac120002");
        }

        // 1, 2
        public Guid
        Foo([Optional] int _1, uint _2)
        {
            return new Guid("57558786-a11d-11ee-8c90-0242ac120002");
        }

        // 1, 2
        public Guid
        Foo([DefaultParameterValue((uint)12)] uint _1, uint _2)
        {
            return new Guid("5755889e-a11d-11ee-8c90-0242ac120002");
        }

        // 1, 2
        public Guid
        Foo([Optional, DefaultParameterValue(12)] nint _1, uint _2)
        {
            return new Guid("57558a1a-a11d-11ee-8c90-0242ac120002");
        }

        // 0, 1, 2
        public Guid
        Foo(bool _1 = false, bool _2 = false)
        {
            return new Guid("575582e0-a11d-11ee-8c90-0242ac120002");
        }

        // 0, 1, 2
        public Guid
        Foo(params int[] _1)
        {
            return new Guid("575581be-a11d-11ee-8c90-0242ac120002");
        }

        // 1, 2, 3
        public Guid
        Foo(long _1, long _2, float _3 = 0.0f)
        {
            return new Guid("5755921c-a11d-11ee-8c90-0242ac120002");
        }

        // 1, 2, 3
        public Guid
        Foo<T>(long _1, long _2, [Optional] T _3)
        {
            return new Guid("575595be-a11d-11ee-8c90-0242ac120002");
        }
    }
}

/*
 * UNUSED GUIDS
return new Guid("575596e0-a11d-11ee-8c90-0242ac120002");
return new Guid("57559802-a11d-11ee-8c90-0242ac120002");
return new Guid("57559924-a11d-11ee-8c90-0242ac120002");
return new Guid("57559a96-a11d-11ee-8c90-0242ac120002");
return new Guid("57559bc2-a11d-11ee-8c90-0242ac120002");
return new Guid("57559ea6-a11d-11ee-8c90-0242ac120002");
return new Guid("57559fdc-a11d-11ee-8c90-0242ac120002");
return new Guid("5755a0f4-a11d-11ee-8c90-0242ac120002");
return new Guid("5755a216-a11d-11ee-8c90-0242ac120002");
return new Guid("5755a32e-a11d-11ee-8c90-0242ac120002");
return new Guid("5755a464-a11d-11ee-8c90-0242ac120002");
return new Guid("5755a572-a11d-11ee-8c90-0242ac120002");
return new Guid("5755a680-a11d-11ee-8c90-0242ac120002");
return new Guid("5755a950-a11d-11ee-8c90-0242ac120002");
return new Guid("5755aa86-a11d-11ee-8c90-0242ac120002");
return new Guid("5755ab9e-a11d-11ee-8c90-0242ac120002");
return new Guid("5755acac-a11d-11ee-8c90-0242ac120002");
return new Guid("5755adc4-a11d-11ee-8c90-0242ac120002");
return new Guid("5755aedc-a11d-11ee-8c90-0242ac120002");
return new Guid("5755afea-a11d-11ee-8c90-0242ac120002");
return new Guid("5755b2b0-a11d-11ee-8c90-0242ac120002");
return new Guid("5755b3c8-a11d-11ee-8c90-0242ac120002");
return new Guid("5755b4d6-a11d-11ee-8c90-0242ac120002");
return new Guid("5755b5e4-a11d-11ee-8c90-0242ac120002");
return new Guid("5755b6f2-a11d-11ee-8c90-0242ac120002");
return new Guid("5755b800-a11d-11ee-8c90-0242ac120002");
return new Guid("5755b90e-a11d-11ee-8c90-0242ac120002");
return new Guid("5755ba1c-a11d-11ee-8c90-0242ac120002");
return new Guid("5755bed6-a11d-11ee-8c90-0242ac120002");
return new Guid("5755c016-a11d-11ee-8c90-0242ac120002");
*/
