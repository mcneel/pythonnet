using System;

namespace Python.Runtime.Codecs
{
    public class ListDecoder : CollectionDecoder
    {
        public ListDecoder()
            : base(typeof(CollectionWrappers.ListWrapper<>))
        {
        }

        private static bool IsList(PyType objectType)
        {
            //TODO accept any python object that implements the sequence and list protocols
            //must implement sequence protocol to fully implement list protocol
            //if (!SequenceDecoder.IsSequence(objectType)) return false;

            //returns wheter the type is a list.
            return PythonReferenceComparer.Instance.Equals(objectType, Runtime.PyListType);
        }

        public override bool CanDecode(PyType objectType, Type targetType)
        {
            return IsList(objectType) && ThisIsAssignableTo(targetType);
        }

        public static ListDecoder Instance { get; } = new ListDecoder();

        public static void Register()
        {
            PyObjectConversions.RegisterDecoder(Instance);
        }
    }
}
