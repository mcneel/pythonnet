using System;
using System.Collections;
using System.Collections.Generic;

namespace Python.Runtime.CollectionWrappers
{
    internal class ListWrapper<T> : SequenceWrapper<T>, IList<T>, IList
    {
        public ListWrapper(PyObject pyObj) : base(pyObj)
        {

        }

        public T this[int index]
        {
            get
            {
                var item = Runtime.PyList_GetItem(pyObject, index);
                var pyItem = new PyObject(item);
                return pyItem.As<T>()!;
            }
            set
            {
                var pyItem = value.ToPython();
                var result = Runtime.PyList_SetItem(pyObject, index, new NewReference(pyItem).Steal());
                if (result == -1)
                    Runtime.CheckExceptionOccurred();
            }
        }

        public int IndexOf(T item)
        {
            return indexOf(item);
        }

        public void Insert(int index, T item)
        {
            if (IsReadOnly)
                throw new InvalidOperationException("Collection is read-only");

            var pyItem = item.ToPython();

            int result = Runtime.PyList_Insert(pyObject, index, pyItem);
            if (result == -1)
                Runtime.CheckExceptionOccurred();
        }

        public void RemoveAt(int index)
        {
            var result = removeAt(index);

            //PySequence_DelItem will set an error if it fails.  throw it here
            //since RemoveAt does not have a bool return value.
            if (result == false)
                Runtime.CheckExceptionOccurred();
        }

        public class InvalidTypeException : Exception
        {
            public InvalidTypeException() : base($"value is not of type {typeof(T)}") { }
        }

        #region IList
        object? IList.this[int index]
        {
            get
            {
                return this[index];
            }
            set
            {
                if (value is T tvalue)
                    this[index] = tvalue;
                else
                    throw new InvalidTypeException();
            }
        }

        bool IList.IsFixedSize => false;

        bool IList.IsReadOnly => false;

        int ICollection.Count => Count;

        bool ICollection.IsSynchronized => false;

        object? ICollection.SyncRoot => null;

        int IList.Add(object value)
        {
            if (value is T tvalue)
            {
                Add(tvalue);
                return IndexOf(tvalue);
            }

            throw new InvalidTypeException();
        }

        void IList.Clear() => Clear();

        bool IList.Contains(object value)
        {
            if (value is T tvalue)
                return Contains(tvalue);

            throw new InvalidTypeException();
        }

        int IList.IndexOf(object value)
        {
            if (value is T tvalue)
                return indexOf(tvalue);

            throw new InvalidTypeException();
        }

        void IList.Insert(int index, object value)
        {
            if (value is T tvalue)
            {
                Insert(index, tvalue);
                return;
            }

            throw new InvalidTypeException();
        }

        void IList.Remove(object value)
        {
            if (value is T tvalue)
            {
                Remove(tvalue);
                return;
            }

            throw new InvalidTypeException();
        }

        void IList.RemoveAt(int index) => RemoveAt(index);

        void ICollection.CopyTo(Array array, int index) => throw new InvalidTypeException();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        #endregion
    }
}
