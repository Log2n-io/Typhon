namespace Typhon.Engine.Internals;

/// <summary>Where a Sphere profile's sessions are centred (09 § 6).</summary>
internal enum ViewpointSource : byte
{
    /// <summary>The viewpoint the application gives each session with <c>Place</c>, every tick.</summary>
    Placed = 0,

    /// <summary>A fixed world position, declared with <c>At</c>.</summary>
    Fixed = 1,

    /// <summary>One entity every session of the profile follows, declared with <c>Bind</c>: its position after the tick's fence.</summary>
    Bound = 2,

    /// <summary>The entity each session controls (<c>Control</c>), declared with <c>AroundControlled</c>: its position after the tick's fence.</summary>
    Controlled = 3,
}
