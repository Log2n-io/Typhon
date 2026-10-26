using SwgTatooine.Server.Accounts;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SwgTatooine.Server;

/// <summary>
/// An account: the name somebody types, and when they were last seen. The credential lives next door in <see cref="AccountSecret"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>StorageMode.Versioned</c>, and not negotiable.</b> Everything else in this world is reconstructible from a seed —
/// creatures, lairs, towns, even a player's position, which is why they are all <see cref="ClusterDurability.Checkpoint"/>. An
/// account is the one thing a person created and expects to find again, so losing a checkpoint interval of it is losing
/// somebody's account. It rides the WAL like <see cref="Inventory"/> does.
/// </para>
/// <para>
/// <b>Versioned on a non-spatial archetype is also the one combination that may be written from outside the tick</b>
/// (<c>EW-02</c> in <c>rules/runtime-scheduling.md</c>), which is what lets <c>/auth/signup</c> and <c>/auth/login</c> commit
/// on a Kestrel thread instead of queueing work for a system. MVCC makes the write concurrent; being out of the spatial index
/// keeps it clear of the tick fence's unlatched fast paths.
/// </para>
/// <para>
/// <b><see cref="Name"/> carries the demo's first <c>[Index]</c> anywhere, and that is a deliberate engine-coverage win rather
/// than a convenience.</b> Every signup does a unique-key insert, and a duplicate name is a <b>unique-constraint violation
/// surfaced as a user-visible refusal</b> — "that name is taken" — which nothing in this demo has ever exercised. A pre-check
/// followed by an insert would be a race; the index is what makes exactly one of two simultaneous signups win.
/// </para>
/// <para>
/// <b>The one asymmetry of a <see cref="String64"/> key</b>, recorded so nobody rediscovers it as a bug: since #658 such a
/// field gets its own index segment with a wider node stride, because a 64-byte key's B-tree nodes do not fit the default
/// 256-byte node stride. What it does not get is a zone map — a 64-byte key has no min/max summary — so a B-tree lookup on a
/// name is as fast as any other, while a <i>scan</i> filtered on the name cannot prune clusters. Irrelevant for a unique-key
/// login, which is the only thing that reads it.
/// </para>
/// <para>
/// <b>No e-mail, no recovery, no profile.</b> A name and a password, deliberately
/// ([11 § 1.1](../../../../claude/design/SwgTatooine/design/accounts.md)): there is no PII here, so there is no GDPR data store,
/// and an operator can delete an account but never read a password.
/// </para>
/// </remarks>
[Component("Swg.Account", 2, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct Account
{
    /// <summary>The account name, unique and already lower-cased by <see cref="AccountName.TryNormalize"/>. What a person types to log in.</summary>
    [Field]
    [Index]
    public String64 Name;

    /// <summary>When the account was created, UTC ticks.</summary>
    [Field]
    public long CreatedUtcTicks;

    /// <summary>When the account last logged in, UTC ticks; 0 if never.</summary>
    /// <remarks>
    /// The one field the back office's account list is actually sorted by, and the only reason a write happens on a login rather
    /// than a read — which is worth knowing, because it means every login appends a WAL record.
    /// </remarks>
    [Field]
    public long LastSeenUtcTicks;

    /// <summary>
    /// The outfit chosen at sign-up, as one plus an index into <see cref="TatooineData.PlayerOutfits"/>; 0 when none was chosen.
    /// </summary>
    /// <remarks>
    /// <b>On the account and not on the character, because the character does not exist yet.</b> Sign-up is an HTTP call off the tick;
    /// the character is created in the tick, at the first login (<c>BindOneAvatar</c>), and takes its <see cref="Character.Outfit"/> from
    /// here — or <see cref="TatooineData.OutfitFor"/> when nothing was chosen: a bot's account, an older one, a client that did not ask.
    /// Changing it afterwards is the economy's to sell (Loïc, 2026-10-25: "a comestic/economy driven thing, you buy it from the bazaar").
    /// </remarks>
    [Field]
    public byte Outfit;
}

/// <summary>
/// One account's password credential: the PBKDF2-SHA256 digest, its salt, and the work factor both were derived at.
/// </summary>
/// <remarks>
/// <para>
/// <b>A component of its own, for two reasons, and the second one is the one that forced it.</b> First, separation: nothing
/// that lists accounts, sorts them by last-seen or resolves a name needs the digest, and keeping it apart means a read of
/// <see cref="Account"/> does not carry 48 bytes of secret into a cluster slot, a replication frame or a Workbench grid.
/// Second, the engine's limit: <c>ComponentValue</c> carries at most 112 bytes inline, and one combined component came to 132 —
/// which, before that check was ungated, corrupted memory instead of saying so.
/// </para>
/// <para>
/// <b><see cref="StorageMode.Versioned"/>, like its <see cref="Account"/>, and for the same reason</b>: a credential that
/// survives a checkpoint interval less well than the name it belongs to is an account that exists and cannot be logged into.
/// </para>
/// <para>
/// <b>The digest and salt are <see cref="ulong"/> words, not an <c>[InlineArray]</c>.</b> An inline array is the natural way to
/// spell a fixed byte buffer and the schema layer refuses it outright — <c>DatabaseDefinitions.ThrowUnmappableField</c>, which
/// is right to: a field it cannot map would be invisible to the Workbench, un-indexable and absent from replication. Words of a
/// mappable primitive, read through <see cref="DigestSpan"/> and <see cref="SaltSpan"/>, give the same blittable fixed block
/// and stay describable. Consequence worth stating: the digest IS visible in the Workbench, as four numbers. That is what a
/// password hash is for.
/// </para>
/// </remarks>
[Component("Swg.AccountSecret", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct AccountSecret
{
    /// <summary>How many <see cref="ulong"/> words the digest occupies. <see cref="PasswordHash.HashBytes"/> / 8.</summary>
    public const int DigestWords = PasswordHash.HashBytes / sizeof(ulong);

    /// <summary>How many <see cref="ulong"/> words the salt occupies. <see cref="PasswordHash.SaltBytes"/> / 8.</summary>
    public const int SaltWords = PasswordHash.SaltBytes / sizeof(ulong);

    /// <summary>First word of the PBKDF2-SHA256 derived key. Reached as bytes through <see cref="DigestSpan"/>, never field by field.</summary>
    [Field]
    public ulong Digest0;

    /// <summary>Second word of the derived key.</summary>
    [Field]
    public ulong Digest1;

    /// <summary>Third word of the derived key.</summary>
    [Field]
    public ulong Digest2;

    /// <summary>Fourth word of the derived key.</summary>
    [Field]
    public ulong Digest3;

    /// <summary>First word of the per-account salt. Reached as bytes through <see cref="SaltSpan"/>.</summary>
    [Field]
    public ulong Salt0;

    /// <summary>Second word of the salt.</summary>
    [Field]
    public ulong Salt1;

    /// <summary>
    /// The work factor the digest was derived with.
    /// </summary>
    /// <remarks>
    /// Stored per account rather than read from <see cref="PasswordHash.DefaultIterations"/> at verification time, which is the
    /// whole reason the cost can ever be raised: a hash derived at today's factor must keep verifying after the constant moves,
    /// or raising it would lock every existing account out.
    /// </remarks>
    [Field]
    public int Iterations;

    /// <summary>
    /// The derived key as <see cref="PasswordHash.HashBytes"/> contiguous bytes.
    /// </summary>
    /// <returns>A span over this instance's own storage; writing through it writes the component.</returns>
    /// <remarks>
    /// <b>A BYTE span, deliberately, and not a <see cref="ulong"/> one.</b> <c>Pack = 4</c> means these words are only
    /// 4-byte-aligned, and a <c>Span&lt;ulong&gt;</c> over a 4-aligned address is a span the runtime is entitled to assume is
    /// aligned. Bytes carry no alignment requirement, and the KDF and the constant-time comparison both want bytes anyway.
    /// <para>
    /// <c>[UnscopedRef]</c> because the span borrows <c>this</c>: the caller's <see cref="AccountSecret"/> variable must outlive
    /// it, which is what the attribute tells the compiler to enforce at every call site.
    /// </para>
    /// </remarks>
    [UnscopedRef]
    public Span<byte> DigestSpan()
        => MemoryMarshal.CreateSpan(ref Unsafe.As<ulong, byte>(ref Digest0), DigestWords * sizeof(ulong));

    /// <summary>The salt as <see cref="PasswordHash.SaltBytes"/> contiguous bytes.</summary>
    /// <returns>A span over this instance's own storage; writing through it writes the component.</returns>
    /// <remarks>See <see cref="DigestSpan"/> for why this is a byte span and why it is <c>[UnscopedRef]</c>.</remarks>
    [UnscopedRef]
    public Span<byte> SaltSpan()
        => MemoryMarshal.CreateSpan(ref Unsafe.As<ulong, byte>(ref Salt0), SaltWords * sizeof(ulong));
}
