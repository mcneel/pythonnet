using System;
using System.Collections.Generic;

namespace Python.Runtime.Codecs
{
    public class IterableDecoder : CollectionDecoder
    {
        public IterableDecoder()
            : base(typeof(CollectionWrappers.IterableWrapper<>))
        {
        }

        public override bool CanDecode(PyType objectType, Type targetType)
        {
            return IsIterable(objectType) && IsEnumerable(targetType);
        }

        public static IterableDecoder Instance { get; } = new IterableDecoder();

        public static void Register()
        {
            PyObjectConversions.RegisterDecoder(Instance);
        }
    }
}
