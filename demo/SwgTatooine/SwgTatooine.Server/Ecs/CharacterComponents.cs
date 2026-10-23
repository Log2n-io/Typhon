using System.Runtime.InteropServices;

namespace SwgTatooine.Server;

/// <summary>
/// The persistent you: a name, a wallet, and where you were when you logged out (A4).
/// </summary>
/// <remarks>
/// <para>
/// <b>The middle of the three levels</b> ([11 § 1.1](../../../../claude/design/SwgTatooine/design/accounts.md)): <see cref="Account"/>
/// is credentials, <c>Character</c> is everything that persists about a person's presence in the world, and the <c>Player</c>
/// cluster archetype is the body — spawned at login and destroyed at logout. The split is what lets an offline character exist
/// without sitting in the spatial index, which is the honest reason the body is separate rather than a flag on this.
/// </para>
/// <para>
/// <b><see cref="StorageMode.Versioned"/>, non-spatial, like <see cref="Account"/>, and for both of its reasons.</b> It is the
/// one thing besides the account that a person created and expects to find again, so it rides the WAL rather than a checkpoint;
/// and being Versioned on an archetype with no placement field is what lets a login read and write it from a Kestrel thread
/// (<c>EW-02</c>). Give <see cref="CharacterArch"/> a placement component and every login becomes a race.
/// </para>
/// <para>
/// <b>The position stored here is the AUTHORITY only while the character is offline.</b> While an avatar is alive its
/// <c>PlayerPlacement</c> is the truth and this is a stale copy; A6 refreshes it on a cadence so a <c>kill -9</c> costs seconds
/// of walking rather than a session. Reading it for anything other than deciding where to spawn is a bug waiting to happen.
/// </para>
/// </remarks>
// Pack = 4 on the analyzer's advice: 8 + 64 + 8 + 12 + 2 + 8 is 102 bytes of field, which the longs would otherwise round to 112
// — and 104 packed. Both fit the inline spawn payload (TYPHON013), with room for the fields A5 and G will add.
[Component("Swg.Character", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct Character
{
    /// <summary>
    /// The account this character belongs to. Unique: one character per account.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Unique rather than <c>AllowMultiple</c>, and the uniqueness is load-bearing today.</b> A login is read-or-create, and
    /// two logins for one account arriving together would both read nothing and both create — the same race the account name's
    /// index exists to settle. With a unique index the second insert is a <c>UniqueConstraintViolationException</c> and the
    /// caller re-reads, so exactly one character exists however many logins race. A pre-check could not give that.
    /// </para>
    /// <para>
    /// Several characters per account is a flip of this to <c>AllowMultiple = true</c> plus a selection screen, and the thing to
    /// know before making it is that the race above comes back and needs a different answer.
    /// </para>
    /// <para>
    /// <b><c>OnParentDelete = Delete</c> is what makes "an operator can delete an account" mean something.</b> Deleting the
    /// account takes the character with it rather than leaving a row pointing at nothing — the engine's cascade, not a sweep
    /// somebody has to remember to run.
    /// </para>
    /// </remarks>
    [Field]
    [Index(OnParentDelete = CascadeAction.Delete)]
    public EntityLink<AccountArch> Account;

    /// <summary>
    /// The name other players see. Distinct from the account name, which is a credential and is nobody else's business.
    /// </summary>
    /// <remarks>
    /// Not indexed and not unique, deliberately: display names are shown, not looked up, and A12 interns them to a <c>u32</c>
    /// for the wire anyway because the projection compiler cannot carry a <c>Str</c>. Making it unique would mean a second
    /// name-taken failure mode on a field nothing resolves by.
    /// </remarks>
    [Field]
    public String64 Name;

    /// <summary>The wallet, in credits.</summary>
    /// <remarks>
    /// A <c>long</c>, which is right for the data and currently <b>unprojectable above 2³²</b>: every projected field funnels
    /// through <c>double</c> → <c>uint</c>, so a balance past four billion is silently clamped on the wire (EN-A). Stored
    /// correctly here; the defect is the projection pipeline's and is filed.
    /// </remarks>
    [Field]
    public long Credits;

    /// <summary>Where the avatar was when it was last saved — the position a login spawns it at.</summary>
    [Field]
    public Point3F LastPosition;

    /// <summary>The realm the avatar was last in, as a <c>RealmId</c>'s underlying value.</summary>
    /// <remarks>
    /// <b>A position without its realm is a position in the wrong world.</b> Every realm has its own grid and the demo's
    /// interiors share one coordinate box, so spawning at a saved <c>(x, z)</c> in realm 0 when the character logged out inside
    /// a building puts them in the desert outside it — or worse, inside the terrain. The two are read and written together,
    /// always.
    /// </remarks>
    [Field]
    public ushort LastRealm;

    /// <summary>When <see cref="LastPosition"/> and <see cref="LastRealm"/> were written, UTC ticks.</summary>
    /// <remarks>
    /// Exists so A-5's acceptance criterion can be stated as a number rather than a hope: after a <c>kill -9</c> the position
    /// loss is bounded by now minus this, and a test can assert the bound instead of eyeballing it.
    /// </remarks>
    [Field]
    public long LastSavedUtcTicks;
}
