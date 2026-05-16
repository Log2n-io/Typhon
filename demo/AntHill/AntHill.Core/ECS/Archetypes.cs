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

[Archetype(103)]
partial class Rock : Archetype<Rock>
{
    public static readonly Comp<Obstacle> Info = Register<Obstacle>();
}

// Spider is intentionally NOT an ECS archetype — kept as plain arrays on TyphonBridge.
// At Phase 5 scale (8 spiders) the archetype overhead + cross-archetype access patterns from
// SpiderUpdateSystem aren't worth the complexity. Revisit if predator count grows in Phase 9.
