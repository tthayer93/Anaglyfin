using System.Collections;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;

namespace Anaglyfin.Tests.Stubs;

/// <summary>
/// A minimal <see cref="IServiceCollection"/> the tests can inspect.
/// </summary>
/// <remarks>
/// The real <c>ServiceCollection</c> type lives in the container implementation, which a
/// plugin does not reference: it compiles against the Jellyfin assemblies only. Keeping a
/// list behind the interface gives the tests the same view the server hands the plugin.
/// </remarks>
internal sealed class FakeServiceCollection : IServiceCollection
{
    private readonly List<ServiceDescriptor> _descriptors = new();

    public ServiceDescriptor this[int index]
    {
        get => _descriptors[index];
        set => _descriptors[index] = value;
    }

    public int Count => _descriptors.Count;

    public bool IsReadOnly => false;

    public void Add(ServiceDescriptor descriptor)
        => _descriptors.Add(descriptor);

    public void Clear()
        => _descriptors.Clear();

    public bool Contains(ServiceDescriptor item)
        => _descriptors.Contains(item);

    public void CopyTo(ServiceDescriptor[] array, int arrayIndex)
        => _descriptors.CopyTo(array, arrayIndex);

    public bool Remove(ServiceDescriptor item)
        => _descriptors.Remove(item);

    public int IndexOf(ServiceDescriptor item)
        => _descriptors.IndexOf(item);

    public void Insert(int index, ServiceDescriptor item)
        => _descriptors.Insert(index, item);

    public void RemoveAt(int index)
        => _descriptors.RemoveAt(index);

    public IEnumerator<ServiceDescriptor> GetEnumerator()
        => _descriptors.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator()
        => GetEnumerator();
}
