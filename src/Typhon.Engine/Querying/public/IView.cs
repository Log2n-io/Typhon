namespace Typhon.Engine;

/// <summary>
/// Consumer-facing handle to a registered view. Concrete views derive from <see cref="ViewBase"/>; consumers usually hold the derived
/// type, but the registry / framework code that doesn't need to know the specific generics holds an <see cref="IView"/> reference instead.
/// </summary>
/// <remarks>
/// The engine-internal delta buffer that drives change capture isn't exposed on this interface — it's handed to the
/// registry as a separate parameter at registration time (see <c>ViewRegistry.RegisterView</c>), keeping the consumer
/// surface free of internal types like <c>ViewDeltaRingBuffer</c>.
/// </remarks>
public interface IView
{
    /// <summary>Process-unique identifier for this view, assigned at construction.</summary>
    int ViewId { get; }

    /// <summary>Indices of the indexed fields this view depends on; drives which component mutations notify the view.</summary>
    int[] FieldDependencies { get; }

    /// <summary>True once the view has been disposed and its buffers released.</summary>
    bool IsDisposed { get; }
}
