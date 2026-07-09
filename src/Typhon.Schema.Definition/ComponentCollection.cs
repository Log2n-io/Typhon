using JetBrains.Annotations;
using System.Runtime.InteropServices;

namespace Typhon.Schema.Definition;

/// <summary>
/// A component field holding a variable-length collection of unmanaged <typeparamref name="T"/> values. The struct itself is a compact handle (a buffer id),
/// so the owning component stays fixed-size and blittable; the elements live in a separate engine-managed buffer.
/// </summary>
/// <typeparam name="T">Unmanaged element type stored in the collection.</typeparam>
[PublicAPI]
[StructLayout(LayoutKind.Sequential)]
public struct ComponentCollection<T> where T : unmanaged
{
    internal int _bufferId;
}
