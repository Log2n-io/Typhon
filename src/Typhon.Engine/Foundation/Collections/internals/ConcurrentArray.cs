using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Fixed size array where multiple concurrent threads can process elements in an exclusive fashion.
/// </summary>
/// <typeparam name="T">The type being stored in the collection</typeparam>
/// <remarks>
/// The features:
///  - Exclusive processing of a given elements. Other thread won't be able to enumerate/pick it.
///  - Any thread can remove an element from the collection, if another thread is currently processing it the element the <see cref="Remove(int)"/>
/// method will spin wait until it gets ownership over the item.
///  - Elements can be added, removed as long as the capacity is not reached.
/// Typical usage is producer add several objects to the collection calling <see cref="Add"/>,
/// then multiple users iterate the content with a for-loop, calling <see cref="Pick"/> method to "reserve" the item (this is an atomic option,
/// only one thread can reserve a given element at a given position), process it and either put it back with <see cref="PutBack"/> or remove it
/// from the collection with <see cref="Remove(int)"/>
/// If calling <see cref="Pick"/> return false, then the item is already being processed by another thread (or the entry is free), you just have
/// to move on to the next one.
/// </remarks>
[ExcludeFromCodeCoverage]
internal class ConcurrentArray<T> where T : class
{
    private readonly Memory<T> _data;
    private readonly int _capacity;
    private int _count;
    private readonly ConcurrentQueue<int> _freeList;

    public ConcurrentArray(int capacity)
    {
        _capacity = capacity;
        _count = 0;
        _data = new T[capacity];
        _freeList = new ConcurrentQueue<int>();
    }

    public int Count => _count - _freeList.Count;
    public int Capacity => _capacity;

    public void Clear()
    {
        _data.Span.Clear();
        _count = 0;
        _freeList.Clear();
    }

    public T[] ToArray() => _data.ToArray();

    public int Add(T obj)
    {
        var span = _data.Span;
        if (_freeList.TryDequeue(out var index))
        {
            span[index] = obj;
        }
        else
        {
            if (_count >= _capacity)
            {
                ThrowCapacityReached();
            }
            index = _count;
            span[_count++] = obj;
        }

        return index;
    }

    /// <summary>
    /// Array accessor, NOT THREAD-SAFE, access on existing element only!
    /// </summary>
    /// <param name="index"></param>
    /// <returns></returns>
    public ref T this[int index]
    {
        get
        {
            if ((uint)index >= Count)
            {
                ThrowIndexOutOfRangeException();
            }
            return ref _data.Span[index];
        }
    }

    internal static int PickCounter = 0;

    /// <summary>
    /// Pick and item from the collection for processing.
    /// Any call to this method must be followed by either <see cref="PutBack"/> or <see cref="Remove"/>.
    /// </summary>
    /// <param name="index">The index at which the item should be retrieved</param>
    /// <param name="result">The object or <c>null</c> if the entry is free or the object currently being processed by another thread</param>
    /// <returns><c>true</c> if the object was successfully retrieved, <c>false</c> otherwise</returns>
    public bool Pick(int index, out T result)
    {
        if ((uint)index >= _capacity)
        {
            ThrowIndexOutOfRangeException();
        }

        Interlocked.Increment(ref PickCounter);
        result = Interlocked.Exchange(ref _data.Span[index], null);
        return result != null;
    }

    /// <summary>
    /// Put a previously picked object back in the collection.
    /// </summary>
    /// <param name="index">Index of the object to put back</param>
    /// <param name="obj">The object</param>
    /// <remarks>
    /// You must call this method after a corresponding <see cref="Pick"/> to put the object back in the collection and allow other thread to <see cref="Pick"/> it or <see cref="Release"/> it.
    /// </remarks>
    public void PutBack(int index, T obj)
    {
        if ((uint)index >= _capacity)
        {
            ThrowIndexOutOfRangeException();
        }
        var prev = Interlocked.CompareExchange(ref _data.Span[index], obj, null);
        if (prev != null)
        {
            ThrowInvalidPutBack(index);
        }
    }

    /// <summary>
    /// Release an object previously picked to free its entry.
    /// </summary>
    /// <param name="index">Index of the object to release</param>
    /// <remarks>
    /// This call must be preceding by a corresponding <see cref="Pick"/> call, the object won't be part of the collection anymore, freeing one entry.
    /// </remarks>
    public void Release(int index)
    {
        if ((uint)index >= _capacity)
        {
            ThrowIndexOutOfRangeException();
        }
        _freeList.Enqueue(index);

    }

    /// <summary>
    /// Remove a non-pick object from the collection.
    /// </summary>
    /// <param name="index">Index of the object to remove</param>
    /// <remarks>
    /// If the object is currently being picked by another thread, this call will spin-wait until it get ownership.
    /// If you call this method on an entry that doesn't hold an object, you'll end-up with an infinite wait.
    /// </remarks>
    public void Remove(int index) => Remove(index, TimeSpan.MaxValue);

    /// <summary>
    /// Remove a non-picked object from the collection.
    /// </summary>
    /// <param name="index">Index of the object to remove</param>
    /// <param name="timeOut"></param>
    /// <returns><c>true</c> if the item was successfully removed, <c>false</c> if we couldn't get ownership in before it timed-out.</returns>
    /// <remarks>
    /// If the object is currently being picked by another thread, this call will spin-wait until it get ownership.
    /// If you call this method on an entry that doesn't hold an object, you'll end-up with an infinite wait.
    /// </remarks>
    public bool Remove(int index, TimeSpan timeOut)
    {
        if ((uint)index >= _capacity)
        {
            ThrowIndexOutOfRangeException();
        }

        var succeed = true;
        var obj = Interlocked.Exchange(ref _data.Span[index], null);
        if (obj == null)
        {
            succeed = false;
            var to = DateTime.UtcNow + timeOut;
            var sw = new SpinWait();
            while (DateTime.UtcNow < to)
            {
                sw.SpinOnce();
                obj = Interlocked.Exchange(ref _data.Span[index], null);
                if (obj == null)
                {
                    succeed = true;
                    break;
                }
            }
        }

        if (succeed)
        {
            _freeList.Enqueue(index);
        }
        return succeed;
    }

    private static void ThrowIndexOutOfRangeException() => throw new IndexOutOfRangeException();
    private static void ThrowInvalidPutBack(int index) => throw new Exception($"Invalid put back at location {index}");
    private static void ThrowNonFreeElement(int index) => throw new Exception($"Element at {index} is not free, call Pick() first");
    private static void ThrowCapacityReached() => throw new Exception("Can add a new element, the array capacity is reached");
}