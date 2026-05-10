namespace Typhon.Engine.Internals;

internal interface IView
{
    int ViewId { get; }
    int[] FieldDependencies { get; }
    bool IsDisposed { get; }
    ViewDeltaRingBuffer DeltaBuffer { get; }
}