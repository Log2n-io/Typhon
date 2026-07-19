using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.ARPG.Schema;

/// <summary>
/// Spatial location, orientation, and movement.
/// Present on all entities that exist in the game world.
/// </summary>
[Component("ARPG.Position", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct Position
{
    [Field] public Point3F Location;
    [Field] public QuaternionF Rotation;
    [Field] public float MovementSpeed;
    [Field] public Point3F Velocity;

    [Field] [Index(AllowMultiple = true)] public int ZoneId;

    [Field] public bool IsGrounded;
}
