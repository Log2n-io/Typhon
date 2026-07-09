// unset

using JetBrains.Annotations;
using System.Collections.Generic;

namespace Typhon.Engine;

/// <summary>
/// A named object grouping a set of component definitions that together describe one logical entity shape.
/// </summary>
[PublicAPI]
public class DBObjectDefinition
{
    /// <summary>The object's name.</summary>
    public string Name { get; }

    /// <summary>The component definitions that make up this object.</summary>
    public IReadOnlyList<DBComponentDefinition> Components { get; }

    internal DBObjectDefinition(string name)
    {
        Name = name;
        Components = new List<DBComponentDefinition>();
    }
}