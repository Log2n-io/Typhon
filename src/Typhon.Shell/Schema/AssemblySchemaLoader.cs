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
        var fields = new List<ComponentSchema.FieldInfo>();
        var structSize = Unsafe.SizeOf<byte>(); // fallback

        // Use Marshal.SizeOf for blittable structs, or compute from field offsets
        try
        {
            structSize = Marshal.SizeOf(type);
        }
        catch
        {
            // If Marshal.SizeOf fails, use reflection-based calculation
        }

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
            var size = GetFieldSize(fieldInfo.FieldType);

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

    private static int GetFieldSize(Type dotNetType)
    {
        // For known types, return the exact size; for unknowns, use Marshal.SizeOf
        try
        {
            return Marshal.SizeOf(dotNetType);
        }
        catch
        {
            return 0;
        }
    }
}
