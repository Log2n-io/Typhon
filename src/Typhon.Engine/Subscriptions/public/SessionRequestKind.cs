using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>What a recorded <see cref="SessionRequest"/> asks for.</summary>
[PublicAPI]
public enum SessionRequestKind : byte
{
    /// <summary>Unset; never recorded.</summary>
    None = 0,

    /// <summary>Bind the session to a declared interest profile.</summary>
    Profile = 1,

    /// <summary>Put an observer in one of the session's slots.</summary>
    Observe = 2,

    /// <summary>Empty one of the session's observer slots.</summary>
    Unobserve = 3,

    /// <summary>Bind the session to the entity it controls.</summary>
    Control = 4,

    /// <summary>Replace the session's whole source subscription list.</summary>
    SetSources = 5,

    /// <summary>Set the session's outbound byte budget.</summary>
    SetBudget = 6,

    /// <summary>End the session.</summary>
    Kick = 7,

    /// <summary>Point the session's viewpoint at an entity it does not control.</summary>
    Follow = 8,

    /// <summary>Set the session's visibility tag mask.</summary>
    SetVisibility = 9,
}
