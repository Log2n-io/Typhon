using System;

namespace SwgTatooine.Server.Accounts;

/// <summary>
/// Reads, creates and saves a <see cref="Character"/> (A4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Off the tick, like <see cref="AccountStore"/>, and legal for the same one reason</b>: <see cref="Character"/> is
/// <c>StorageMode.Versioned</c> on an archetype with no placement field, which <c>EW-02</c> permits a thread outside the DAG to
/// write with no coordination.
/// </para>
/// <para>
/// <b>The index key is a <see cref="long"/>, which is worth stating because nothing in the declaration says so.</b>
/// <c>Character.Account</c> is an <c>EntityLink&lt;AccountArch&gt;</c>, and <c>DatabaseSchemaExtensions</c> maps both
/// <c>EntityLink&lt;T&gt;</c> and a bare <c>EntityId</c> to <c>FieldType.Long</c> — the same eight bytes. So the index behind it is
/// a <c>LongSingleBTree</c> and <c>EnumerateIndex</c> must be asked for <c>long</c>; asking for <c>EntityLink&lt;AccountArch&gt;</c>
/// or <c>ulong</c> fails the tree cast at run time rather than at compile time.
/// </para>
/// </remarks>
public static class CharacterStore
{
    /// <summary>
    /// Finds the character belonging to an account.
    /// </summary>
    /// <param name="dbe">The engine.</param>
    /// <param name="account">The account entity.</param>
    /// <param name="character">The character's entity on success.</param>
    /// <param name="profile">A copy of its <see cref="Character"/> component on success.</param>
    /// <returns><see langword="true"/> when the account has a character.</returns>
    public static bool TryFind(DatabaseEngine dbe, EntityId account, out EntityId character, out Character profile)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        character = default;
        profile = default;

        var key = IndexKey(account);
        var indexRef = dbe.GetIndexRef<Character, EntityLink<AccountArch>>(c => c.Account);

        using var tx = dbe.CreateQuickTransaction();
        using var found = tx.EnumerateIndex<Character, long>(indexRef, key, key);
        if (!found.MoveNext())
        {
            return false;
        }

        character = EntityId.FromRawValue((ulong)found.CurrentEntityPK);
        profile = found.CurrentComponent;
        return true;
    }

    /// <summary>
    /// Returns the account's character, creating it on first login.
    /// </summary>
    /// <param name="dbe">The engine.</param>
    /// <param name="account">The account the character belongs to.</param>
    /// <param name="displayName">The name to give a NEW character. Ignored when one already exists.</param>
    /// <param name="spawn">Where a NEW character starts. Ignored when one already exists.</param>
    /// <param name="realm">The realm a NEW character starts in. Ignored when one already exists.</param>
    /// <param name="character">The character's entity.</param>
    /// <param name="profile">A copy of its component.</param>
    /// <returns><see langword="true"/> when the character was created by THIS call, <see langword="false"/> when it already existed.</returns>
    /// <remarks>
    /// <b>The race is settled by the unique index, not by the lookup.</b> Two logins for one account can both find nothing and both
    /// try to create; the second insert is a <c>UniqueConstraintViolationException</c>, and the handler re-reads rather than
    /// retrying the insert. So exactly one character exists however many logins arrive together, and neither caller has to hold a
    /// lock. This is the same shape as <see cref="AccountStore.TrySignup"/> and for the same reason — see
    /// <see cref="Character.Account"/> for why the index is unique.
    /// </remarks>
    /// <remarks>
    /// <b>The OFF-TICK read-or-create. The login path has its own, and that is not an oversight.</b>
    /// <c>TatooineReplication.BindOneAvatar</c> does the same thing on the TICK's transaction, because a login must read or create the
    /// character, spawn the avatar and hand over control in one commit — see the comment there, which also explains why the
    /// unique-index retry below cannot be reproduced in-tick and why nothing needs it to be. This overload is what a caller outside the
    /// tick uses: an administrative tool, or a future <c>/characters</c> endpoint.
    /// </remarks>
    public static bool ReadOrCreate(DatabaseEngine dbe, EntityId account, string displayName, in Point3F spawn, ushort realm,
        out EntityId character, out Character profile)
    {
        ArgumentNullException.ThrowIfNull(dbe);

        if (TryFind(dbe, account, out character, out profile))
        {
            return false;
        }

        var fresh = new Character
        {
            Account = account,
            Name = (String64)(displayName ?? string.Empty),
            Credits = StartingCredits,
            LastPosition = spawn,
            LastRealm = realm,
            LastSavedUtcTicks = DateTime.UtcNow.Ticks,
        };

        try
        {
            using var tx = dbe.CreateQuickTransaction();
            character = tx.Spawn<CharacterArch>(CharacterArch.Profile.Set(fresh));
            tx.Commit();
            profile = fresh;
            return true;
        }
        catch (UniqueConstraintViolationException)
        {
            // Somebody else created it between the lookup and the insert. Re-read rather than retry: the winner's character is the
            // one that exists, and this caller wants THAT one, not another attempt at its own.
            if (TryFind(dbe, account, out character, out profile))
            {
                return false;
            }

            // Only reachable if the winner's character was destroyed in the moment between its commit and this read — which means
            // the account is being deleted underneath this login. Refusing is correct; inventing a second character is not.
            throw;
        }
    }

    /// <summary>
    /// Writes where the avatar is, so a later login spawns it there.
    /// </summary>
    /// <param name="dbe">The engine.</param>
    /// <param name="character">The character to save.</param>
    /// <param name="position">The avatar's current position.</param>
    /// <param name="realm">The realm it is currently in.</param>
    /// <returns><see langword="true"/> when the character existed and was written.</returns>
    /// <remarks>
    /// <b>Position and realm are written together, always.</b> A saved position without its realm is a position in the wrong world:
    /// the demo's interiors share one coordinate box with each other, so a character who logged out inside a building and came back
    /// with realm 0 would appear at those coordinates in the open desert.
    /// <para>
    /// Called at logout and, from A6, on a cadence while the avatar is alive — which is what bounds the loss from a <c>kill -9</c>
    /// to seconds of walking instead of the whole session.
    /// </para>
    /// </remarks>
    public static bool SaveLocation(DatabaseEngine dbe, EntityId character, in Point3F position, ushort realm)
    {
        ArgumentNullException.ThrowIfNull(dbe);

        using var tx = dbe.CreateQuickTransaction();
        using var characters = tx.For<CharacterArch>();
        if (!characters.TryOpenMut(character, out var entity))
        {
            return false;
        }

        var updated = entity.Read(CharacterArch.Profile);
        updated.LastPosition = position;
        updated.LastRealm = realm;
        updated.LastSavedUtcTicks = DateTime.UtcNow.Ticks;

        // The whole component is rewritten, Account included, with the same value. The commit path's unchanged-field guard (#665)
        // compares the full key, so the account index is not touched by a position save — which matters, because A6 does this on a
        // cadence for every live avatar and a B+Tree write per avatar per cadence would be a cost nobody budgeted.
        entity.Set(CharacterArch.Profile, updated);
        tx.Commit();
        return true;
    }

    /// <summary>What a character starts with.</summary>
    /// <remarks>
    /// A number rather than zero so the crafting loop ([12](../../../../claude/design/SwgTatooine/design/economy-loop.md)) has something
    /// to work with on a fresh character, and small enough that it is not the economy.
    /// </remarks>
    public const long StartingCredits = 1_000;

    /// <summary>
    /// The account id as the account index's key.
    /// </summary>
    /// <param name="account">The account entity.</param>
    /// <returns>The key.</returns>
    /// <remarks>
    /// <c>unchecked</c> and explicit: <see cref="EntityId.RawValue"/> is a <see cref="ulong"/> and the index is keyed on
    /// <see cref="long"/>, because that is the <c>FieldType</c> an entity reference maps to. Every id this demo will ever mint
    /// leaves the high bit clear, so the two readings agree — but a silent conversion here would be a sign-flip waiting for a
    /// database old enough to reach it.
    /// </remarks>
    private static long IndexKey(EntityId account) => unchecked((long)account.RawValue);
}
