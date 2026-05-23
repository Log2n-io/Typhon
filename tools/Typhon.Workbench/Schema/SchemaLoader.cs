using System.Reflection;
using Typhon.Engine;
using Typhon.Schema.Definition;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Schema;

/// <summary>
/// Loads user-supplied schema DLLs into a <see cref="WorkbenchAssemblyLoadContext"/> and extracts
/// component types (structs tagged with <c>[Component]</c>). Does not touch the engine — that
/// happens in <see cref="EngineLifecycle"/> after compatibility classification.
/// </summary>
public static class SchemaLoader
{
    /// <exception cref="WorkbenchException">
    /// 404 schema_missing — a path doesn't exist on disk.
    /// 400 schema_load_failed — DLL is not a valid managed assembly or fails to load.
    /// 400 schema_missing_dependency — a referenced assembly isn't resolvable.
    /// </exception>
    public static LoadedSchema LoadSchemaDlls(WorkbenchAssemblyLoadContext alc, string[] paths)
    {
        ArgumentNullException.ThrowIfNull(alc);
        ArgumentNullException.ThrowIfNull(paths);

        var assemblies = new List<Assembly>(paths.Length);
        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                throw new WorkbenchException(404, "schema_missing", $"Schema DLL not found: {path}");
            }
            try
            {
                assemblies.Add(alc.LoadSchema(path));
            }
            catch (FileLoadException ex)
            {
                throw new WorkbenchException(400, "schema_missing_dependency",
                    $"Failed to resolve a dependency for {path}: {ex.Message}", ex);
            }
            catch (BadImageFormatException ex)
            {
                throw new WorkbenchException(400, "schema_load_failed",
                    $"Not a valid .NET assembly: {path}", ex);
            }
            catch (Exception ex)
            {
                throw new WorkbenchException(400, "schema_load_failed",
                    $"Failed to load {path}: {ex.Message}", ex);
            }
        }

        var types = new List<Type>();
        var names = new List<string>();
        var archetypeTypes = new List<Type>();
        foreach (var asm in assemblies)
        {
            // GetTypes (not GetExportedTypes): user components/archetypes are frequently declared `internal` (e.g. AntHill's Ant/Food/Nest/Rock archetypes).
            // Public-only discovery silently skips them, so their cluster / entity-map segments never register and the File Map renders them Unknown.
            Type[] allTypes;
            try
            {
                allTypes = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // Best-effort: keep the types that did load — one unresolvable type must not hide the rest from inspection.
                allTypes = ex.Types.Where(t => t != null).ToArray()!;
            }

            foreach (var type in allTypes)
            {
                // Components — value types with [Component].
                if (type.IsValueType)
                {
                    var attr = type.GetCustomAttribute<ComponentAttribute>();
                    if (attr != null)
                    {
                        types.Add(type);
                        names.Add(attr.Name ?? type.Name);
                    }
                    continue;
                }
                // Archetypes — concrete (non-abstract) classes whose inheritance chain includes
                // Typhon.Engine.Archetype<> or Archetype<,>.
                if (type.IsClass && !type.IsAbstract && InheritsFromArchetype(type))
                {
                    archetypeTypes.Add(type);
                }
            }
        }

        return new LoadedSchema(
            assemblies.ToArray(),
            types.ToArray(),
            names.ToArray(),
            archetypeTypes.ToArray());
    }

    /// <summary>True if <paramref name="type"/>'s base-type chain contains a closed generic over
    /// <c>Typhon.Engine.Archetype&lt;&gt;</c> or <c>Archetype&lt;,&gt;</c>. Walks up the chain because deeper
    /// hierarchies (e.g., <c>Child : Archetype&lt;Child, Parent&gt;</c>) still trace back to the generic
    /// base definitions.</summary>
    private static bool InheritsFromArchetype(Type type)
    {
        var archetypeBase1 = typeof(Archetype<>);
        var archetypeBase2 = typeof(Archetype<,>);
        for (var cursor = type.BaseType; cursor != null && cursor != typeof(object); cursor = cursor.BaseType)
        {
            if (!cursor.IsGenericType) continue;
            var def = cursor.GetGenericTypeDefinition();
            if (def == archetypeBase1 || def == archetypeBase2)
            {
                return true;
            }
        }
        return false;
    }
}
