// netstandard2.1 only (#1034): the compiler-support types the newer language features need, which .NET 5+ ships in its BCL. Internal, so they never
// conflict with a consumer's own copies; compiled for netstandard2.1 alone (Typhon.Protocol.csproj), so net10.0 uses the runtime's.

namespace System.Runtime.CompilerServices
{
    /// <summary>Lets <c>init</c> accessors and records compile against netstandard2.1.</summary>
    internal static class IsExternalInit
    {
    }

    /// <summary>The caller-argument expression the throw helpers name their parameter by.</summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    internal sealed class CallerArgumentExpressionAttribute : Attribute
    {
        public CallerArgumentExpressionAttribute(string parameterName) => ParameterName = parameterName;

        public string ParameterName { get; }
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    /// <summary>The ref-safety attribute (C# 11) a struct member returning a reference to its own state carries.</summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Parameter, Inherited = false)]
    internal sealed class UnscopedRefAttribute : Attribute
    {
    }
}
