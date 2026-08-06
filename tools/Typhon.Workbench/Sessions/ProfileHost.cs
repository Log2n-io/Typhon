using System.Collections.Concurrent;

namespace Typhon.Workbench.Sessions;

/// <summary>
/// The captures attached to a session, and which one is in focus (#617 D-10, generalised for #621).
/// </summary>
/// <remarks>
/// <para>
/// Extracted from <see cref="OpenSession"/> when <c>TraceSession</c> was removed. With a standalone trace session gone,
/// a capture is always attached <i>to</i> something — its database for a recorded capture, or the live session it was
/// captured from for a replay. Both hosts need identical semantics (attach, detach-and-dispose, focus fallback), and
/// the second copy of that logic is where the two would drift.
/// </para>
/// <para>
/// Composition rather than a base class: <see cref="OpenSession"/> and <see cref="AttachSession"/> share nothing else —
/// one owns an engine and a bundle path, the other a socket and an endpoint — and inheriting from a common session base
/// purely to share a dictionary would put the engine-shaped and stream-shaped lifecycles under one type.
/// </para>
/// </remarks>
public sealed class ProfileHost
{
    private readonly ConcurrentDictionary<Guid, TraceSessionRuntime> _profiles = new();

    /// <summary>
    /// Guards <see cref="_activeProfileId"/>. The dictionary is concurrent, but the active id is a <see cref="Nullable{Guid}"/> — 20 bytes, so its reads and
    /// writes are not atomic and a concurrent attach could hand a reader a torn value. Attach and detach are user gestures and reads are once per request, so a
    /// plain lock costs nothing measurable and removes the question entirely.
    /// </summary>
    private readonly Lock _activeLock = new();
    private Guid? _activeProfileId;

    /// <summary>Captures attached to this session, keyed by profile id.</summary>
    /// <remarks>
    /// Plural from day one even though the UI opens one at a time. It costs nothing now and it is what lets two captures of the same database be compared side
    /// by side later without peer sessions — the overwhelmingly common diff case, and the one thing the sub-resource model would otherwise have given up.
    /// </remarks>
    public IReadOnlyDictionary<Guid, TraceSessionRuntime> Profiles => _profiles;

    /// <summary>True when no capture is attached.</summary>
    public bool IsEmpty => _profiles.IsEmpty;

    /// <summary>Which attached capture is driving the profiler panels, or <c>null</c> when there is none.</summary>
    public Guid? ActiveProfileId
    {
        get { lock (_activeLock) { return _activeProfileId; } }
    }

    /// <summary>The runtime backing <see cref="ActiveProfileId"/>, or <c>null</c> when no profile is attached.</summary>
    public TraceSessionRuntime ActiveProfile
    {
        get
        {
            lock (_activeLock)
            {
                return _activeProfileId is { } id && _profiles.TryGetValue(id, out var runtime) ? runtime : null;
            }
        }
    }

    /// <summary>Attaches a capture and makes it the active profile. Returns its new profile id.</summary>
    public Guid Attach(TraceSessionRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var id = Guid.NewGuid();
        _profiles[id] = runtime;
        lock (_activeLock)
        {
            _activeProfileId = id;
        }
        return id;
    }

    /// <summary>
    /// Detaches and disposes one profile. When it was the active one, focus falls back to any remaining profile rather than leaving the session advertising the
    /// profiler capability with nothing behind it.
    /// </summary>
    public bool Detach(Guid profileId)
    {
        if (!_profiles.TryRemove(profileId, out var runtime))
        {
            return false;
        }

        try { runtime.Dispose(); }
        catch { /* a profile that failed to close must not take the session down with it */ }

        lock (_activeLock)
        {
            if (_activeProfileId == profileId)
            {
                _activeProfileId = _profiles.IsEmpty ? null : _profiles.Keys.First();
            }
        }
        return true;
    }

    /// <summary>Detaches every profile. Called on session dispose, before the engine goes, because captures hold file handles inside the bundle.</summary>
    public void DetachAll()
    {
        foreach (var id in _profiles.Keys.ToArray())
        {
            Detach(id);
        }
    }
}
