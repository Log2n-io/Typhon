using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SwgTatooine.Server;

/// <summary>
/// The names a client reads a player's replicated <c>nameId</c> through (A12): an id on the wire, the text over HTTP, once.
/// </summary>
/// <remarks>
/// <para>
/// <b>An id rather than the text on the body, and the reason is the cluster.</b> A <c>String64</c> name on every <c>Player</c> took the
/// archetype from 315 to 380 bytes an entity and its clusters from 25 players to 10 — two and a half times the clusters for every
/// per-tick walk over players, scripted crowd included, to carry text only the few humans have. Four bytes beside the outfit, in one
/// 8-byte <see cref="PlayerLook"/>, keep it at 24 (2026-10-25, measured with the engine's own <c>SelectClusterSize</c>).
/// </para>
/// <para>
/// <b>Ids are this process's, not the database's.</b> A name is registered when its character's body is spawned, the only way a client
/// can ever see it, and an id is never reused or reassigned while the process lives. A restart renumbers; a client's cache dies with its
/// session, so it never meets an id from another run.
/// </para>
/// <para>
/// Written by the tick (a login spawns the body), read by Kestrel threads: one lock, taken once per login and once per lookup request,
/// neither of them on a per-frame path.
/// </para>
/// </remarks>
public static class PlayerNames
{
    /// <summary>The most ids one lookup answers; the rest of a longer request are ignored, not refused.</summary>
    public const int MaxIdsPerRequest = 256;

    private static readonly object Gate = new();
    private static readonly Dictionary<uint, string> ById = [];
    private static readonly Dictionary<string, uint> ByName = new(StringComparer.Ordinal);
    private static uint _next;

    /// <summary>
    /// The id a name travels as, registering it on first use; 0 for no name, which a client reads as "none".
    /// </summary>
    /// <param name="name">A character's name. Account names are unique, so one name is one character.</param>
    /// <returns>The id, never reused for another name in this process.</returns>
    public static uint IdFor(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return 0;
        }

        lock (Gate)
        {
            if (!ByName.TryGetValue(name, out var id))
            {
                id = ++_next;
                ByName[name] = id;
                ById[id] = name;
            }

            return id;
        }
    }

    /// <summary>The name an id stands for.</summary>
    /// <param name="id">A replicated <c>nameId</c>.</param>
    /// <param name="name">The name, when the id is known.</param>
    /// <returns>Whether it is.</returns>
    public static bool TryGet(uint id, out string name)
    {
        lock (Gate)
        {
            return ById.TryGetValue(id, out name);
        }
    }

    /// <summary>
    /// The <c>/typhon/names?ids=…</c> answer: a JSON object from each known id to its name. Unknown ids, malformed entries and any past
    /// <see cref="MaxIdsPerRequest"/> are left out, so a client asks once per id and treats an absent one as "no name".
    /// </summary>
    /// <param name="ids">Comma-separated decimal ids, as the query carries them.</param>
    /// <returns>The JSON document.</returns>
    public static string Json(string ids)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            var seen = 0;
            foreach (var part in (ids ?? string.Empty).Split(',', MaxIdsPerRequest + 1, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (++seen > MaxIdsPerRequest)
                {
                    break;
                }

                if (uint.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)
                    && TryGet(id, out var name))
                {
                    json.WriteString(part, name);
                }
            }

            json.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }
}
