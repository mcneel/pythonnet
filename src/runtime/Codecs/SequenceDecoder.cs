using System;
using System.Collections;
using System.Collections.Generic;

namespace Python.Runtime.Codecs
{
    public class SequenceDecoder : CollectionDecoder
    {
        public SequenceDecoder()
            : base(typeof(CollectionWrappers.SequenceWrapper<>))
        {
        }

        public override bool CanDecode(PyType objectType, Type targetType)
        {
            return IsSequence(objectType) && IsEnumerable(targetType);
        }

        public static SequenceDecoder Instance { get; } = new SequenceDecoder();

        public static void Register()
        {
            PyObjectConversions.RegisterDecoder(Instance);
        }
    }
}
