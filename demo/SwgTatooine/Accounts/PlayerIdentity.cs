using Typhon.Engine;

namespace SwgTatooine.Accounts;

/// <summary>
/// Who an admitted <c>player</c> session is: the account its token named (A3).
/// </summary>
/// <param name="Account">The account entity, proved by <see cref="AuthToken.TryValidate"/> at admission.</param>
/// <remarks>
/// <para>
/// <b>This is the <c>Admission.AppData</c> the engine carries from the hook to <c>SessionEventKind.Opened</c>, and it is the
/// whole point of validating the token in the hook rather than later.</b> The hook runs on the transport thread with no
/// transaction and no tick, so it cannot bind an avatar; the session system can, and it needs to know which account to bind. One
/// object, held by reference, read once in the tick.
/// </para>
/// <para>
/// <b>A record and not a bare <see cref="EntityId"/>, because <c>AppData</c> is an <see cref="object"/>.</b> A boxed
/// <see cref="EntityId"/> would be indistinguishable from any other boxed identifier a later feature decides to carry there, and
/// the cast would succeed on the wrong one. A named type makes the pattern match say what it means.
/// </para>
/// <para>
/// <b>Nothing here is trusted beyond the account id.</b> No role, no limits, no name: the role comes from the session kind the
/// engine checked, and the name is read from the account in the tick. A token says who you are and nothing about what you may do.
/// </para>
/// </remarks>
public sealed record PlayerIdentity(EntityId Account);
