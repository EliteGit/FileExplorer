using System.Collections;

namespace FileExplorer.App.Controls;

/// <summary>Read-only list that creates row objects only when the list control asks for them (millions of rows, no cost up front).</summary>
public sealed class VirtualList<T> : IList, IReadOnlyList<T> where T : class
{
    private readonly Func<int, T> _create;
    private readonly Dictionary<int, T> _cache = new();

    public VirtualList(int count, Func<int, T> create) { Count = count; _create = create; }

    public int Count { get; }

    public T this[int index]
    {
        get
        {
            if (_cache.TryGetValue(index, out var v)) return v;
            if (_cache.Count > 2000) _cache.Clear();
            return _cache[index] = _create(index);
        }
    }

    object? IList.this[int index] { get => this[index]; set => throw new NotSupportedException(); }
    public bool IsReadOnly => true;
    public bool IsFixedSize => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public int IndexOf(object? value) => value is T t ? Enumerable.Range(0, Math.Min(Count, 5000)).FirstOrDefault(i => _cache.TryGetValue(i, out var c) && c == t, -1) : -1;
    public bool Contains(object? value) => IndexOf(value) >= 0;
    public IEnumerator<T> GetEnumerator() { for (int i = 0; i < Count; i++) yield return this[i]; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void CopyTo(Array array, int index) { foreach (var x in this) array.SetValue(x, index++); }
    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
}
