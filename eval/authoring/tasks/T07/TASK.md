## Adding mana

`Model.cs` is the model of version 1 of the game, already shipped. Production databases created by version 1 exist and hold
many heroes.

For version 2, heroes gain mana: add a `public int Mana;` field to `Stats`. When a version-1 database is opened by version 2,
every existing hero must keep its `Hp` and `Armor` and start with `Mana = 50`. You may change `Model.cs` (this task is about
changing it); keep the component name `"Eval.Stats"` and the `Hero` archetype.

Contract:

```csharp
public static class Solution
{
    // Opens a database created by version 1 (or by version 2), ready to use.
    public static Typhon.Engine.DatabaseEngine Open(string path);

    public static System.Collections.Generic.List<Typhon.Engine.EntityId> AllHeroes(Typhon.Engine.DatabaseEngine db);

    public static (int Hp, int Armor, int Mana) ReadHero(Typhon.Engine.DatabaseEngine db, Typhon.Engine.EntityId hero);
}
```
