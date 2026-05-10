using JetBrains.Annotations;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Typhon.Engine.Internals;

[PublicAPI]
[ExcludeFromCodeCoverage]
internal class ConcurrentCollection<T> : ICollection<T>
{
    private Lock _lock;
    private List<T> _items;

    public ConcurrentCollection()
    {
        _lock = new Lock();
        _items = new List<T>();
    }

    public void SafeAction(Action<List<T>> action)
    {
        lock (_lock)
        {
            action(_items);
        }
    }
    
    public void Add(T item)
    {
        lock (_lock)
        {
            _items.Add(item);
        }
    }

    public void CopyTo(T[] array, int arrayIndex)
    {
        lock (_lock)
        {
            _items.CopyTo(array, arrayIndex);
        }
    }

    public bool Remove(T item)
    {
        lock (_lock)
        {
            return _items.Remove(item);
        }
    }
    
    public void Clear()
    {
        lock (_lock)
        {
            _items.Clear();
        }
    }

    public bool Contains(T item)
    {
        lock (_lock)
        {
            return _items.Contains(item);
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _items.Count;
            }
        }
    }

    public bool IsReadOnly => false;

    public T[] ToArray()
    {
        lock (_lock)
        {
            return _items.ToArray();
        }
    }

    public IEnumerator<T> GetEnumerator() => new Enumerator(ToArray());

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<T>
    {
        public Enumerator(T[] elements)
        {
            _elements = elements;
            _index = -1;
        }
        private T[] _elements;
        private int _index;
        private T _current;

        public bool MoveNext()
        {
            if ((uint)_index < (uint)_elements.Length)
            {
                _current = _elements[_index];
                _index++;
                return true;
            }

            _current = default;
            _index = -1;
            return false;
        }

        public void Reset()
        {
            _index = 0;
            _current = default;
        }

        T IEnumerator<T>.Current => _current;

        object IEnumerator.Current => _current;

        public void Dispose() {}
    }
}