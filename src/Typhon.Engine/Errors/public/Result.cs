using JetBrains.Annotations;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine;

/// <summary>
/// A zero-allocation result type for hot-path methods.
/// <typeparamref name="TValue"/> is the data; <typeparamref name="TStatus"/> is a per-subsystem byte enum.
/// Convention: status value 0 = Success in all enums.
/// </summary>
[PublicAPI]
[StructLayout(LayoutKind.Sequential)]
public readonly struct Result<TValue, TStatus>
    where TValue : unmanaged
    where TStatus : unmanaged, Enum
{
    /// <summary>The value returned by the operation. Only meaningful when <see cref="IsSuccess"/> is true.</summary>
    public readonly TValue Value;

    /// <summary>The status code. Zero means success by convention.</summary>
    public readonly TStatus Status;

    /// <summary>Creates a successful result with the specified value and default (zero) status.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Result(TValue value)
    {
        Value = value;
        Status = default; // 0 = Success
    }

    /// <summary>Creates a failure result with the specified status and default value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Result(TStatus status)
    {
        Value = default;
        Status = status;
    }

    /// <summary>Creates a result with both a value and an explicit status (e.g., Deleted with revision metadata).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Result(TValue value, TStatus status)
    {
        Value = value;
        Status = status;
    }

    /// <summary>True when Status == 0 (Success by convention). No boxing — single byte comparison.</summary>
    public bool IsSuccess
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Unsafe.As<TStatus, byte>(ref Unsafe.AsRef(in Status)) == 0;
    }

    /// <summary>True when Status != 0.</summary>
    public bool IsFailure
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => !IsSuccess;
    }
}
