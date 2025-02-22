﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace BetterJoyForCemu.Collections {
    /// A thread-safe IEnumerator implementation.
    /// https://www.codeproject.com/Articles/56575/Thread-safe-enumeration-in-C
    public class SafeEnumerator<T> : IEnumerator<T> {
        private readonly IEnumerator<T> _inner;
        private readonly object _lock;

        public SafeEnumerator(IEnumerator<T> inner, object @lock) {
            _inner = inner;
            _lock = @lock;

            Monitor.Enter(_lock);
        }

        public void Dispose() {
            // called when foreach loop finishes
            Monitor.Exit(_lock);
        }

        public bool MoveNext() {
            return _inner.MoveNext();
        }

        public void Reset() {
            _inner.Reset();
        }

        public T Current {
            get { return _inner.Current; }
        }

        object IEnumerator.Current {
            get { return Current; }
        }
    }

    // https://codereview.stackexchange.com/a/125341
    public class ConcurrentList<T> : IList<T> {
        #region Fields

        private IList<T> _internalList;
        private readonly object _lock = new object();

        #endregion

        #region ctor

        public ConcurrentList() {
            _internalList = new List<T>();
        }

        public ConcurrentList(int capacity) {
            _internalList = new List<T>(capacity);
        }

        public ConcurrentList(IEnumerable<T> list) {
            _internalList = new List<T>();
            foreach (T item in list) {
                _internalList.Add(item);
            }
        }

        #endregion

        public T this[int index] {
            get {
                return LockInternalListAndGet(l => l[index]);
            }

            set {
                LockInternalListAndCommand(l => l[index] = value);
            }
        }

        public int Count {
            get {
                return LockInternalListAndQuery(l => l.Count);
            }
        }

        public bool IsReadOnly => false;

        public void Add(T item) {
            LockInternalListAndCommand(l => l.Add(item));
        }

        public void Clear() {
            LockInternalListAndCommand(l => l.Clear());
        }

        public bool Contains(T item) {
            return LockInternalListAndQuery(l => l.Contains(item));
        }

        public void CopyTo(T[] array, int arrayIndex) {
            LockInternalListAndCommand(l => l.CopyTo(array, arrayIndex));
        }

        public int IndexOf(T item) {
            return LockInternalListAndQuery(l => l.IndexOf(item));
        }

        public void Insert(int index, T item) {
            LockInternalListAndCommand(l => l.Insert(index, item));
        }

        public bool Remove(T item) {
            return LockInternalListAndQuery(l => l.Remove(item));
        }

        public void RemoveAt(int index) {
            LockInternalListAndCommand(l => l.RemoveAt(index));
        }

        IEnumerator<T> IEnumerable<T>.GetEnumerator() {
            return LockInternalAndEnumerate();
        }

        IEnumerator IEnumerable.GetEnumerator() {
            return LockInternalAndEnumerate();
        }

        #region Utilities

        protected virtual void LockInternalListAndCommand(Action<IList<T>> action) {
            lock (_lock) {
                action(_internalList);
            }
        }

        protected virtual T LockInternalListAndGet(Func<IList<T>, T> func) {
            lock (_lock) {
                return func(_internalList);
            }
        }

        protected virtual TObject LockInternalListAndQuery<TObject>(Func<IList<T>, TObject> query) {
            lock (_lock) {
                return query(_internalList);
            }
        }

        protected virtual IEnumerator<T> LockInternalAndEnumerate() {
            return new SafeEnumerator<T>(_internalList.GetEnumerator(), _lock);
        }

        #endregion
    }
}
