using System;
using System.Collections;
using System.Collections.Generic;

namespace Python.Runtime.Codecs
{
    public abstract class CollectionDecoder : IPyObjectDecoder
    {
        protected static Type s_enum = typeof(IEnumerable);
        protected static Type s_enumT = typeof(IEnumerable<>);

        protected Type GenericCollectionType;

        protected CollectionDecoder(Type genericCollectionType)
        {
            GenericCollectionType = genericCollectionType;
        }

        protected static bool IsSequence(Type targetType)
        {
            // NOTE:
            // a type is a sequence if it implements either
            // IEnumerable or IEnumerable<>
            // but typeof(IEnumerable<>).IsAssignableFrom() can not be used,
            // since the generic type is unknown.
            // .GetGenericTypeDefinition() equality check does not work either
            // in cases like targetType being of type IList<>:
            // targetType.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            // and
            // typeof(IEnumerable<>).IsAssignableFrom(
            //      targetType.GetGenericTypeDefinition()
            // )
            // both return false.
            // so lets manually check the list of interfaces.
            // .GetInterfaces() returns both direct or indirect interfaces. 
            Type[] interfaces = targetType.GetInterfaces();
            for (int i = 0; i < interfaces.Length; i++)
            {
                Type iface = interfaces[i];

                if (iface == s_enum
                        || (iface.IsGenericType
                                && iface.GetGenericTypeDefinition() == s_enumT))
                {
                    return true;
                }
            }

            return false;
        }

        protected static bool IsSequence(PyType objectType)
        {
            //must implement iterable protocol to fully implement sequence protocol
            if (!IsIterable(objectType)) return false;

            //returns wheter it implements the sequence protocol
            //according to python doc this needs to exclude dict subclasses
            //but I don't know how to look for that given the objectType
            //rather than the instance.
            return objectType.HasAttr("__getitem__");
        }

        protected static bool IsIterable(PyType objectType)
        {
            return objectType.HasAttr("__iter__");
        }

        public abstract bool CanDecode(PyType objectType, Type targetType);

        public virtual bool TryDecode<T>(PyObject pyObj, out T value)
        {
            if (pyObj == null) throw new ArgumentNullException(nameof(pyObj));

            Type elementType;
            Type tType = typeof(T);

            // first see if T is a plane IEnumerable
            if (tType.IsGenericType)
            {
                elementType = tType.GetGenericArguments()[0];
            }
            else
            {
                elementType = typeof(object);
            }

            Type collectionType = GenericCollectionType.MakeGenericType(elementType);

            object instance = Activator.CreateInstance(collectionType, new[] { pyObj });
            value = (T)instance;
            return true;
        }
    }
}
