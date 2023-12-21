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

            // NOTE:
            // if the consumer of this iterator, does not iterate
            // over all the items in pyObject, the iterObject will not
            // be disposed while holding the GIL.
            // lets ensure iterObject is disposed while holding the GIL
            using var disposer = new DisposeOnGIL(iterObject);
            while (true)
            {
                using var GIL = Py.GIL();

                if (!iterObject.MoveNext())
                {
                    break;
                }

                yield return iterObject.Current.As<T>()!;
            }
        }

        private sealed class DisposeOnGIL : IDisposable
        {
            readonly PyObject pyObject;

            public DisposeOnGIL(PyObject pyobj) => pyObject = pyobj;

            public void Dispose()
            {
                GC.SuppressFinalize(this);

                using (Py.GIL())
                {
                    pyObject?.Dispose();
                }
            }
        }
    }
}
