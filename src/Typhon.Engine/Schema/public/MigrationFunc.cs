using System;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Strongly-typed migration function that transforms a component from one revision to another.
/// Both types must be unmanaged structs with [Component] attributes sharing the same Name but different Revisions.
/// </summary>
[PublicAPI]
public delegate void MigrationFunc<TOld, TNew>(ref TOld oldComponent, out TNew newComponent) where TOld : unmanaged where TNew : unmanaged;

/// <summary>
/// Byte-level migration function for scenarios where the old struct type is no longer available in code.
/// Receives raw bytes of the old component and must produce raw bytes of the new component.
/// </summary>
[PublicAPI]
public delegate void ByteMigrationFunc(ReadOnlySpan<byte> oldBytes, Span<byte> newBytes);
