using System;
using System.Collections.Generic;
using System.Collections;

namespace Python.Runtime.CollectionWrappers
{
    internal class IterableWrapper<T> : IEnumerable<T>
    {
        protected readonly PyObject pyObject;

        public IterableWrapper(PyObject pyObj)
        {
            if (pyObj == null)
                throw new ArgumentNullException();
            pyObject = new PyObject(pyObj.Reference);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public IEnumerator<T> GetEnumerator()
        {
            PyIter iterObject;
            using (Py.GIL())
            {
                iterObject = PyIter.GetIter(pyObject);
            }
            try
            {
                while (true)
                {
                    using var _ = Py.GIL();
                    if (!iterObject.MoveNext())
                    {
                        break;
                    }
                    yield return iterObject.Current.As<T>()!;
                }
            }
            finally
            {
                // NOTE:
                // if the consumer of this iterator, does not iterate
                // over all the items in pyObject, the iterObject will not
                // be disposed while holding the GIL.
                // lets ensure iterObject is disposed while holding the GIL
                using var _ = Py.GIL();
                iterObject.Dispose();
            }
        }
    }
}
