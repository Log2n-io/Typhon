using System;
using Typhon.Protocol;

namespace SwgTatooine;

/// <summary>
/// What the simulation's own players say, pre-encoded (SWG-09).
/// </summary>
/// <remarks>
/// <para>
/// <b>The demo needs chatter of its own or chat cannot be looked at.</b> A <c>Say</c> comes from a client, and the
/// browser client has no one typing into it — so without this the only way to see the 50 m fan-out working is to run two
/// bots and trust a counter. Simulated players talking makes the routing visible: fly the camera over a town and the
/// speech appears and stops as you move.
/// </para>
/// <para>
/// <b>Encoded once, at type initialisation.</b> <see cref="Utf8Text256.From"/> encodes a string, which allocates, and this
/// is reached from the tick. The lines are values in a static array, so saying one copies 258 bytes and allocates
/// nothing — the property the event path's allocation test asserts for everything else.
/// </para>
/// <para>
/// The lines are grouped by what the speaker is doing, so the chat reflects the simulation rather than decorating it: a
/// player in combat says something a fighter would, and one idling in a cantina does not.
/// </para>
/// </remarks>
public static class ChatLines
{
    private static readonly Utf8Text256[] Idle =
    [
        Utf8Text256.From("Anyone seen a decent moisture vaporator around here?"),
        Utf8Text256.From("I've got a bad feeling about this cantina."),
        Utf8Text256.From("Utinni!"),
        Utf8Text256.From("Two credits says that Rodian is a bounty hunter."),
        Utf8Text256.From("Watch the sand. It gets everywhere."),
        Utf8Text256.From("Anyone selling water? Fair price."),
    ];

    private static readonly Utf8Text256[] Travelling =
    [
        Utf8Text256.From("Heading out past the dune sea. Wish me luck."),
        Utf8Text256.From("Long walk. Should have bought a speeder."),
        Utf8Text256.From("Sand people territory ahead — staying off the ridge."),
        Utf8Text256.From("On my way. Don't start without me."),
    ];

    private static readonly Utf8Text256[] Fighting =
    [
        Utf8Text256.From("It's got me! Someone help!"),
        Utf8Text256.From("Aim for the legs!"),
        Utf8Text256.From("This one's bigger than the last."),
        Utf8Text256.From("I can't hold it much longer!"),
        Utf8Text256.From("Nearly down — keep at it!"),
    ];

    private static readonly Utf8Text256[] Waiting =
    [
        Utf8Text256.From("Shuttle's late. As usual."),
        Utf8Text256.From("How long does a shuttle take out here?"),
        Utf8Text256.From("I'll be on the next one."),
    ];

    /// <summary>
    /// What the people who live in a town say.
    /// </summary>
    /// <remarks>
    /// <b>City NPCs are why chat is watchable at all.</b> Speech carries 50 m, and players are spread across a 16 km
    /// planet — about one per 350 m² at the demo's populations, so a camera hears a player within earshot perhaps two
    /// times in a hundred. NPCs stand in the towns in dozens, which is exactly where someone watching points the camera,
    /// and it is also what a town is supposed to sound like.
    /// </remarks>
    private static readonly Utf8Text256[] Townsfolk =
    [
        Utf8Text256.From("Fresh power converters! Barely used!"),
        Utf8Text256.From("You'll not find better moisture farming gear on this rock."),
        Utf8Text256.From("Keep your hood up. Imperials about."),
        Utf8Text256.From("Water's gone up again. Robbery, that's what it is."),
        Utf8Text256.From("Mind the krayt dragon stories — they're mostly stories."),
        Utf8Text256.From("Spice runner came through last night. Saw nothing, said nothing."),
        Utf8Text256.From("Wanted to buy a landspeeder. Ended up with a bantha."),
        Utf8Text256.From("That Jawa gave me a fair price for once."),
    ];

    /// <summary>A line a townsperson would say, chosen by <paramref name="hash"/>.</summary>
    /// <param name="hash">Any value; its low bits pick the line.</param>
    /// <returns>The line, by reference.</returns>
    public static ref readonly Utf8Text256 Townsperson(uint hash) => ref Townsfolk[(int)(hash % (uint)Townsfolk.Length)];

    /// <summary>A line suited to what the speaker is doing, chosen by <paramref name="hash"/>.</summary>
    /// <param name="activity">The speaker's <c>PlayerActivity</c>.</param>
    /// <param name="hash">Any value; its low bits pick the line.</param>
    /// <returns>The line, by reference — it is 258 bytes and the caller only reads it.</returns>
    public static ref readonly Utf8Text256 For(int activity, uint hash)
    {
        var lines = activity switch
        {
            PlayerActivity.Combat => Fighting,
            PlayerActivity.Travelling or PlayerActivity.Roaming or PlayerActivity.ToPortal => Travelling,
            PlayerActivity.ToShuttle or PlayerActivity.AwaitingShuttle => Waiting,
            _ => Idle,
        };

        return ref lines[(int)(hash % (uint)lines.Length)];
    }

    /// <summary>Every line, for a check that wants to assert what the world can say.</summary>
    public static int Count => Idle.Length + Travelling.Length + Fighting.Length + Waiting.Length + Townsfolk.Length;
}
