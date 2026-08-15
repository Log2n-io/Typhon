using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;
using Typhon.Shell.Extensibility;

namespace Typhon.Shell.Schema;

/// <summary>
/// Loads component types from a compiled .NET assembly by scanning for [Component] attributes.
/// Builds ComponentSchema instances for text-to-binary conversion.
/// Also discovers <see cref="ShellCommand"/> subclasses contributed by extension assemblies.
/// </summary>
internal static class AssemblySchemaLoader
{
    public static (Assembly Assembly, List<(string Name, Type Type, ComponentSchema Schema)> Components) LoadAssembly(string path)
    {
        var assembly = Assembly.LoadFrom(path);
        var results = new List<(string, Type, ComponentSchema)>();

        foreach (var type in assembly.GetExportedTypes())
        {
            if (!type.IsValueType)
            {
                continue;
            }

            var componentAttr = type.GetCustomAttribute<ComponentAttribute>();
            if (componentAttr == null)
            {
                continue;
            }

            var schema = BuildSchema(type, componentAttr, path);
            if (schema != null)
            {
                results.Add((schema.Name, type, schema));
            }
        }

        return (assembly, results);
    }

    /// <summary>
    /// Scans an already-loaded assembly for non-abstract classes that inherit <see cref="ShellCommand"/>
    /// and instantiates them via parameterless constructor.
    /// </summary>
    public static List<ShellCommand> LoadCommands(Assembly assembly)
    {
        var commands = new List<ShellCommand>();
        ScanAssemblyForCommands(assembly, commands);
        return commands;
    }

    /// <summary>
    /// Discovers shell commands from sibling DLLs in the same directory as <paramref name="loadedAssemblyPath"/>.
    /// Scans all .dll files that haven't already been loaded, looking for <see cref="ShellCommand"/> subclasses.
    /// Returns the list of commands with their source assembly name for reporting.
    /// </summary>
    public static List<(ShellCommand Command, string AssemblyName)> DiscoverCommandsInDirectory(string loadedAssemblyPath, HashSet<string> alreadyScanned)
    {
        var results = new List<(ShellCommand, string)>();
        var directory = Path.GetDirectoryName(Path.GetFullPath(loadedAssemblyPath));
        if (directory == null)
        {
            return results;
        }

        var extensibilityName = typeof(ShellCommand).Assembly.GetName().Name;

        foreach (var dll in Directory.GetFiles(directory, "*.dll"))
        {
            var fullPath = Path.GetFullPath(dll);
            if (alreadyScanned.Contains(fullPath))
            {
                continue;
            }

            alreadyScanned.Add(fullPath);

            try
            {
                // Quick check: only load assemblies that reference the extensibility assembly
                var asm = Assembly.LoadFrom(fullPath);

                var referencesExtensibility = false;
                foreach (var refName in asm.GetReferencedAssemblies())
                {
                    if (string.Equals(refName.Name, extensibilityName, StringComparison.OrdinalIgnoreCase))
                    {
                        referencesExtensibility = true;
                        break;
                    }
                }

                if (!referencesExtensibility)
                {
                    continue;
                }

                var commands = new List<ShellCommand>();
                ScanAssemblyForCommands(asm, commands);

                foreach (var cmd in commands)
                {
                    results.Add((cmd, asm.GetName().Name));
                }
            }
            catch
            {
                // Skip assemblies that can't be loaded (not managed, wrong target, etc.)
            }
        }

        return results;
    }

    private static void ScanAssemblyForCommands(Assembly assembly, List<ShellCommand> commands)
    {
        foreach (var type in assembly.GetExportedTypes())
        {
            if (type.IsAbstract || type.IsInterface || !type.IsClass)
            {
                continue;
            }

            if (!typeof(ShellCommand).IsAssignableFrom(type))
            {
                continue;
            }

            if (type.GetConstructor(Type.EmptyTypes) == null)
            {
                continue;
            }

            var command = (ShellCommand)Activator.CreateInstance(type);
            commands.Add(command);
        }
    }

    private static ComponentSchema BuildSchema(Type type, ComponentAttribute componentAttr, string assemblyPath)
    {
        // An assembly compiled with Typhon's source generator already carries a spec whose offsets were measured against the MANAGED layout — the one the
        // engine reads components through. Reflecting instead reads Marshal.OffsetOf, which describes the MARSHALLED layout, and those two disagree for
        // `bool` and `char` (#819). Ask for the generated spec first; reflection is the fallback for assemblies that do not have one.
        if (TryBuildSchemaFromGeneratedSpec(type, componentAttr, assemblyPath, out var generated))
        {
            return generated;
        }

        // Reached only when the assembly has no generated spec, so the offsets below can only be read as the marshalled layout. That is a DIFFERENT layout
        // from the managed one the engine addresses fields by wherever a bool or char is involved — and for char the totals can match, so nothing downstream
        // would notice a field decoded a byte off. Refuse rather than hand back offsets that address the wrong bytes (#819).
        //
        // Uses the same detector as the engine's DBComponentDefinition.Build: it walks the CLR type at any depth, including fields the schema does not model
        // and non-public ones, and excludes the declarations that reconcile the two layouts (CharSet.Unicode, [MarshalAs], explicit layout, fixed buffers).
        if (LayoutDivergence.Detect(type, out var divergentPath, out var divergentKind))
        {
            throw new InvalidOperationException(
                $"Component '{componentAttr.Name ?? type.Name}' in '{assemblyPath}' contains a {divergentKind} at '{divergentPath}', and the assembly carries "
              + "no Typhon-generated schema. Its field offsets can only be read as the marshalled layout, which disagrees with the managed layout the engine "
              + "addresses fields by. Rebuild the assembly with the Typhon source generator.");
        }

        var fields = new List<ComponentSchema.FieldInfo>();
        var structSize = GetManagedStructSize(type);

        foreach (var fieldInfo in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var fieldAttr = fieldInfo.GetCustomAttribute<FieldAttribute>();
            if (fieldAttr == null)
            {
                continue;
            }

            var indexAttr = fieldInfo.GetCustomAttribute<IndexAttribute>();
            var fieldType = MapDotNetTypeToFieldType(fieldInfo.FieldType);

            var offset = (int)Marshal.OffsetOf(type, fieldInfo.Name);
            var size = GetManagedFieldSize(fieldInfo.FieldType);

            fields.Add(new ComponentSchema.FieldInfo
            {
                Name = fieldAttr.Name ?? fieldInfo.Name,
                Type = fieldType,
                Offset = offset,
                Size = size,
                HasIndex = indexAttr != null,
                IndexAllowMultiple = indexAttr?.AllowMultiple ?? false
            });
        }

        return new ComponentSchema(
            componentAttr.Name,
            componentAttr.Revision,
            // Component-level multi-instance ("AllowMultiple") was removed — always false now (field retained on ComponentSchema for shape stability).
            false,
            structSize,
            assemblyPath,
            fields);
    }

    /// <summary>
    /// Builds the schema from the assembly's source-generated <see cref="ComponentSchemaSpec"/> when it has one. Those offsets came from
    /// <c>Unsafe.ByteOffset</c> against a stack probe, so they describe the managed layout the engine actually reads (#816, #819).
    /// </summary>
    private static bool TryBuildSchemaFromGeneratedSpec(Type type, ComponentAttribute componentAttr, string assemblyPath, out ComponentSchema schema)
    {
        schema = null;

        // The registry is populated by the assembly's own [ModuleInitializer], and loading an assembly does not by itself run one — the CLI reaches these
        // types through reflection, which need not touch the module. Force it, or a properly-generated assembly would silently take the reflection fallback.
        //
        // Deliberately NOT wrapped in a catch: the runtime caches a failed module initializer and rethrows the same TypeInitializationException for every
        // subsequent access, so swallowing it would demote one broken assembly to the marshalled path for every component it declares — silently, which is
        // the failure mode #819 exists to remove. A schema assembly whose initializer throws is broken, and saying so is the useful outcome.
        RuntimeHelpers.RunModuleConstructor(type.Module.ModuleHandle);

        if (!GeneratedSchemaRegistry.TryGetComponentSpec(type, out var spec) || spec.Fields == null)
        {
            return false;
        }

        // A spec is a claim, and the flag is the part of it that says the offsets were measured against the managed layout. A spec emitted by a pre-#819
        // generator, or registered by hand through the public GeneratedSchemaRegistry API, carries marshalled or unknown offsets — preferring it over
        // reflection would then hand the CLI the very bytes this change exists to stop it writing.
        if (!spec.ManagedOffsets)
        {
            return false;
        }

        var fields = new List<ComponentSchema.FieldInfo>();
        foreach (var f in spec.Fields)
        {
            if (f.IsStatic)
            {
                continue;
            }

            var fieldType = MapDotNetTypeToFieldType(f.DotNetType);
            if (fieldType == FieldType.None)
            {
                continue;
            }

            fields.Add(new ComponentSchema.FieldInfo
            {
                Name = f.Name,
                Type = fieldType,
                Offset = f.Offset,
                Size = GetManagedFieldSize(f.DotNetType),
                HasIndex = f.HasIndex,
                IndexAllowMultiple = f.IndexAllowMultiple
            });
        }

        schema = new ComponentSchema(
            spec.Name ?? componentAttr.Name,
            spec.Revision,
            // Component-level multi-instance ("AllowMultiple") was removed — always false now (field retained on ComponentSchema for shape stability).
            false,
            GetManagedStructSize(type),
            assemblyPath,
            fields);
        return true;
    }

    private static FieldType MapDotNetTypeToFieldType(Type dotNetType)
    {
        // Primitives
        if (dotNetType == typeof(bool))   return FieldType.Boolean;
        if (dotNetType == typeof(sbyte))  return FieldType.Byte;
        if (dotNetType == typeof(byte))   return FieldType.UByte;
        if (dotNetType == typeof(char))   return FieldType.Char;
        if (dotNetType == typeof(short))  return FieldType.Short;
        if (dotNetType == typeof(ushort)) return FieldType.UShort;
        if (dotNetType == typeof(int))    return FieldType.Int;
        if (dotNetType == typeof(uint))   return FieldType.UInt;
        if (dotNetType == typeof(float))  return FieldType.Float;
        if (dotNetType == typeof(double)) return FieldType.Double;
        if (dotNetType == typeof(long))   return FieldType.Long;
        if (dotNetType == typeof(ulong))  return FieldType.ULong;

        // Composite types from Typhon.Schema.Definition
        if (dotNetType == typeof(String64))    return FieldType.String64;
        if (dotNetType == typeof(String1024))  return FieldType.String1024;
        if (dotNetType == typeof(Variant))     return FieldType.Variant;
        if (dotNetType == typeof(Point2F))     return FieldType.Point2F;
        if (dotNetType == typeof(Point3F))     return FieldType.Point3F;
        if (dotNetType == typeof(Point4F))     return FieldType.Point4F;
        if (dotNetType == typeof(Point2D))     return FieldType.Point2D;
        if (dotNetType == typeof(Point3D))     return FieldType.Point3D;
        if (dotNetType == typeof(Point4D))     return FieldType.Point4D;
        if (dotNetType == typeof(QuaternionF)) return FieldType.QuaternionF;
        if (dotNetType == typeof(QuaternionD)) return FieldType.QuaternionD;

        // Default fallback — use None to signal an unrecognized type
        return FieldType.None;
    }

    /// <summary>
    /// Width of a field in the MANAGED layout — the one the engine reads components through, and the one both branches' offsets describe.
    /// </summary>
    /// <remarks>
    /// Pairing a managed offset with a marshalled size would be #819 all over again on the other axis: <c>Marshal.SizeOf(typeof(char))</c> is 1 and
    /// <c>bool</c> is 4, so a <c>char</c> field would be described as occupying half a code unit at a correct offset. <c>Marshal.SizeOf</c> also throws
    /// outright on a generic struct, which would silently leave a size of zero.
    /// </remarks>
    private static int GetManagedFieldSize(Type dotNetType)
    {
        try
        {
            return RuntimeHelpers.SizeOf(dotNetType.TypeHandle);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Managed size of the component struct itself, for the same reason <see cref="GetManagedFieldSize"/> exists.</summary>
    private static int GetManagedStructSize(Type type)
    {
        try
        {
            return RuntimeHelpers.SizeOf(type.TypeHandle);
        }
        catch
        {
            return Unsafe.SizeOf<byte>();
        }
    }
}
