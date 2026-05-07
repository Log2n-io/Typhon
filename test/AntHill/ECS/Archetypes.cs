using Typhon.Engine;
using Typhon.Schema.Definition;

namespace AntHill;

[Archetype(100)]
partial class Ant : Archetype<Ant>
{
    public static readonly Comp<WorldBounds> Bounds = Register<WorldBounds>();
    public static readonly Comp<Velocity> Velocity = Register<Velocity>();
    public static readonly Comp<Genetics> Genetics = Register<Genetics>();
    public static readonly Comp<AntState> State = Register<AntState>();
}

[Archetype(101)]
partial class Food : Archetype<Food>
{
    public static readonly Comp<FoodSource> Source = Register<FoodSource>();
}

[Archetype(102)]
partial class Nest : Archetype<Nest>
{
    public static readonly Comp<NestInfo> Info = Register<NestInfo>();
}
