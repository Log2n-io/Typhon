namespace Typhon.Shell.Extensibility;

/// <summary>
/// Base class for shell commands contributed by extension assemblies.
/// Subclasses are discovered via reflection when an assembly is loaded with <c>load-schema</c>.
/// </summary>
public abstract class ShellCommand
{
    /// <summary>Display name used to invoke the command (e.g., "generate-data"). Lowercase, hyphen-separated.</summary>
    public abstract string Name { get; }

    /// <summary>One-line description shown in <c>help</c> listing.</summary>
    public abstract string Description { get; }

    /// <summary>Detailed help text (multi-line). Shown by <c>help &lt;command&gt;</c>.</summary>
    public virtual string DetailedHelp => Description;

    /// <summary>If true, the shell verifies a database is open before invoking <see cref="Execute"/>.</summary>
    public virtual bool RequiresDatabase => false;

    /// <summary>
    /// Execute the command.
    /// </summary>
    /// <param name="context">Shell context providing engine, transaction, and session access.</param>
    /// <param name="args">Tokenized arguments. <c>args[0]</c> is the command name itself.</param>
    public abstract ShellCommandResult Execute(IShellCommandContext context, string[] args);
}
