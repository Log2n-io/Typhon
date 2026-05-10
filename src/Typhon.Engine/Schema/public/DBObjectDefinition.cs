// unset

using JetBrains.Annotations;
using System.Collections.Generic;

namespace Typhon.Engine;

[PublicAPI]
public class DBObjectDefinition
{
    public string Name { get; }
    public IReadOnlyList<DBComponentDefinition> Components { get; }

    internal DBObjectDefinition(string name)
    {
        Name = name;
        Components = new List<DBComponentDefinition>();
    }
}