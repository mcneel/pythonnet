using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Python.Runtime.Codecs
{
    public abstract class CollectionDecoder : IPyObjectDecoder
    {
        protected Type m_collectionType;

        protected CollectionDecoder(Type collectionType)
        {
            m_collectionType = collectionType;
        }

        protected bool ThisIsAssignableTo(Type to)
        {
            // NOTE:
            // Can not use these
            // to.IsAssignableFrom(typeof(IEnumerable<>))
            // to.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            // to.GetGenericTypeDefinition().IsAssignableFrom(typeof(IEnumerable<>))
            // lets lookup and see if target type matches any of the interfaces
            // implemented by the collection type, and therefore is assignable
            // from generic collection type created in TryDecode<>
            bool isGeneric = to.IsGenericType;
            to = isGeneric ? to.GetGenericTypeDefinition() : to;
            foreach (Type iface in m_collectionType.GetInterfaces())
            {
                if (iface == to
                        || (iface.IsGenericType
                                && iface.GetGenericTypeDefinition() == to))
                {
                    return true;
                }
            }

            return false;
        }

        protected static bool IsSequence(PyType pyType)
        {
            //must implement iterable protocol to fully implement sequence protocol
            if (!IsIterable(pyType)) return false;

            //returns wheter it implements the sequence protocol
            //according to python doc this needs to exclude dict subclasses
            //but I don't know how to look for that given the objectType
            //rather than the instance.
            return pyType.HasAttr("__getitem__");
        }

        protected static bool IsIterable(PyType pyType)
        {
            return pyType.HasAttr("__iter__");
        }

        public abstract bool CanDecode(PyType pyType, Type type);

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

            Type collectionType = m_collectionType.MakeGenericType(elementType);

            object instance = Activator.CreateInstance(collectionType, new[] { pyObj });
            value = (T)instance;
            return true;
        }
    }
}
