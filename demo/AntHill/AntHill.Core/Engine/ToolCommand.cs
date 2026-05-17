namespace AntHill.Core;

public enum ToolCommandKind : byte
{
    PlaceFood,
    PlaceRock,
    Cull,
    Ignite,
}

/// <summary>
/// God-game tool action queued from Godot main thread and drained by <c>ToolCommandSystem</c>
/// at <c>Phase.Input</c>. Coordinates are in sim units (0..<see cref="TyphonBridge.WorldSize"/>).
/// </summary>
public readonly struct ToolCommand
{
    public readonly ToolCommandKind Kind;
    public readonly float X;
    public readonly float Y;
    public readonly float Radius;   // sim units; used by Cull
    public readonly float Amount;   // initial food, used by PlaceFood

    private ToolCommand(ToolCommandKind kind, float x, float y, float radius, float amount)
    {
        Kind = kind;
        X = x;
        Y = y;
        Radius = radius;
        Amount = amount;
    }

    public static ToolCommand PlaceFood(float x, float y, float amount) => new(ToolCommandKind.PlaceFood, x, y, 0f, amount);
    public static ToolCommand PlaceRock(float x, float y) => new(ToolCommandKind.PlaceRock, x, y, 0f, 0f);
    public static ToolCommand Cull(float x, float y, float radius) => new(ToolCommandKind.Cull, x, y, radius, 0f);
    public static ToolCommand Ignite(float x, float y) => new(ToolCommandKind.Ignite, x, y, 0f, 0f);
}
