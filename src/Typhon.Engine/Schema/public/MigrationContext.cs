using System;
using JetBrains.Annotations;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>
/// What a migration function (<see cref="ContextMigrationFunc{TOld, TNew}"/>) can do beyond copying bytes: read the texts of the old value and set the
/// texts of the new one. A <see cref="String1024"/> is a handle the engine owns the text of (#1094), so a plain <see cref="MigrationFunc{TOld, TNew}"/> can
/// only carry an existing one over; giving a new value a text takes this context.
/// </summary>
/// <remarks>
/// <para>Copying an old value's <see cref="String1024"/> into the new value keeps its text: the handle moves with the row. Copy it into at most one field —
/// two fields naming one text would each free it.</para>
/// <para>The migration may also run a function only to read another field of the converted value (a realm key, say); there the context writes no text:
/// <see cref="SetText(ref String1024, ReadOnlySpan{char})"/> leaves the field empty and <see cref="ReadText"/> reads nothing.</para>
/// </remarks>
[PublicAPI]
public sealed class MigrationContext
{
    private readonly TextMigration _text;

    internal MigrationContext(TextMigration text) => _text = text;

    /// <summary>The text <paramref name="field"/> of the OLD value names; empty for no text.</summary>
    public string ReadText(in String1024 field) => _text?.Read(field._chunkId) ?? string.Empty;

    /// <summary>Gives <paramref name="field"/> of the NEW value the text <paramref name="text"/>, cut at <see cref="String1024.MaxUtf8Bytes"/> UTF-8
    /// bytes.</summary>
    public void SetText(ref String1024 field, ReadOnlySpan<char> text)
    {
        Span<byte> utf8 = stackalloc byte[String1024.MaxUtf8Bytes];
        var length = TextChunks.Encode(text, utf8);
        field._chunkId = _text?.Allocate(utf8[..length]) ?? 0;
    }

    /// <summary>The UTF-8 overload of <see cref="SetText(ref String1024, ReadOnlySpan{char})"/>.</summary>
    public void SetText(ref String1024 field, ReadOnlySpan<byte> utf8) => field._chunkId = _text?.Allocate(utf8[..TextChunks.FitUtf8(utf8)]) ?? 0;
}

/// <summary>
/// A migration function that can read and set texts through a <see cref="MigrationContext"/>; registered as a <see cref="MigrationFunc{TOld, TNew}"/> is.
/// </summary>
[PublicAPI]
public delegate void ContextMigrationFunc<TOld, TNew>(ref TOld oldComponent, out TNew newComponent, MigrationContext context)
    where TOld : unmanaged where TNew : unmanaged;
