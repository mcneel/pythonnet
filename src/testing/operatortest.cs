using System;
using System.Collections.Generic;
using System.Text;

namespace Python.Test
{
    public class OperatorTest
    {

    }

    public struct OperatorValue
    {
        public static bool operator ==(OperatorValue l, OperatorValue r) => true;
        public static bool operator !=(OperatorValue l, OperatorValue r) => false;
    }
}
