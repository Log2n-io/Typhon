// #1034: the netstandard2.1 build declares its own IsExternalInit (Compat/LanguageSupport.cs), and every `init` setter's signature names it — so a library
// compiled against that build names [Typhon.Client]IsExternalInit (or [Typhon.Protocol]IsExternalInit). When it runs against this net10.0 build instead (a netstandard2.1 library in a
// .NET app), the forward resolves that name to the runtime's type, which this build's setters name: the signatures match, and no MissingMethodException.
#if NET
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(System.Runtime.CompilerServices.IsExternalInit))]
#endif
