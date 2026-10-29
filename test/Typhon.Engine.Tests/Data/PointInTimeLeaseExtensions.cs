using System;

namespace Typhon.Engine.Tests;

/// <summary>One-shot reads through a <see cref="PointInTimeAccessor"/> worker, each under a lease of its own (EP-04).</summary>
internal static class PointInTimeLeaseExtensions
{
    /// <summary>Leases <paramref name="workerId"/>'s accessor for the length of <paramref name="read"/>.</summary>
    public static T Leased<T>(this PointInTimeAccessor pta, int workerId, Func<EntityAccessor, T> read)
    {
        using var lease = pta.Enter(workerId);
        return read(lease.Accessor);
    }
}
