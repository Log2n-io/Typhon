namespace SwgTatooine.Bots.Brain;

/// <summary>What a bot decided to do this tick, as one of the commands a browser has and nothing else.</summary>
/// <remarks>
/// <b>A value rather than a send, which is what makes the brain testable at all.</b> The decision is a pure function of
/// what the bot was sent, what it remembers and its own seed; turning the answer into a frame is the swarm's job. A
/// brain that sent its own commands could only be tested against a live server, which is how a decision loop ends up
/// with no tests.
/// <para>
/// <b>The vocabulary is deliberately the browser's.</b> Movement is <c>MoveDir</c> with a speed class — never a speed,
/// because a speed from a client is the speed hack — attacking is <c>SetTarget</c>, and flying is <c>BoardShuttle</c>
/// naming a city. A bot that could say anything else would be distinguishable from a person, which is the one property
/// this whole phase exists to protect.
/// </para>
/// </remarks>
public enum IntentKind
{
    /// <summary>Nothing to send this tick.</summary>
    None = 0,

    /// <summary>Walk, run or ride on a heading (<c>MoveDir</c>).</summary>
    Move,

    /// <summary>Attack something, or 0 to stop (<c>SetTarget</c>).</summary>
    Attack,

    /// <summary>Queue for the next shuttle to a city (<c>BoardShuttle</c>).</summary>
    Board,

    /// <summary>Walk to a point and stop on it (<c>MoveTo</c>): the click a browser sends, at the run the server grants.</summary>
    MoveTo,

    /// <summary>Get on the swoop, or off it (<c>SetMount</c>): the browser's 1 key.</summary>
    Mount,

    /// <summary>Take a mission listed at the terminal the bot stands at (<c>TakeMission</c>): the swarm names the offer, the one whose lair is nearest.</summary>
    TakeMission,

    /// <summary>Cancel the mission held (<c>AbandonMission</c>): the swarm names it, from the journal.</summary>
    AbandonMission,

    /// <summary>
    /// Trade at the bazaar terminal it stands at (bazaar.md § 8): the swarm reads the market and sends the plan — collect, list, vendor, buy — as the
    /// commands a browser's bazaar panel sends. <see cref="Intent.Region"/> is the bazaar's city, as a region.
    /// </summary>
    Trade,
}

/// <summary>One decision.</summary>
/// <param name="Kind">What to do.</param>
/// <param name="Heading">Where to go, radians, for <see cref="IntentKind.Move"/>.</param>
/// <param name="SpeedClass">How fast, as a class the server grants, for <see cref="IntentKind.Move"/>.</param>
/// <param name="NetId">What to attack, for <see cref="IntentKind.Attack"/>.</param>
/// <param name="Region">Where to fly, as a city plus one, for <see cref="IntentKind.Board"/>; 0 otherwise.</param>
/// <param name="X">Where to walk, east, for <see cref="IntentKind.MoveTo"/>.</param>
/// <param name="Z">Where to walk, north.</param>
/// <param name="On">For <see cref="IntentKind.Mount"/>: get on (true) or off.</param>
/// <remarks>
/// <b>The destination is a region, which is a city plus one, and 0 means none.</b> <c>BoardShuttle</c> itself carries a
/// bare city index on the wire, so the subtraction happens at the send — once, in the swarm — rather than here. The
/// alternative was a -1 on every intent that is not a boarding, and an optional value that spells absence as -1 is the
/// sentinel this project does not keep: <c>Intent.None.Region</c> would then differ from <c>default</c>, and the one
/// place a city index of 0 is legitimate is exactly where the mistake would be invisible.
/// </remarks>
public readonly record struct Intent(IntentKind Kind, float Heading, byte SpeedClass, uint NetId, int Region, float X = 0f, float Z = 0f, bool On = false)
{
    /// <summary>Nothing this tick.</summary>
    public static Intent None => new(IntentKind.None, 0f, 0, 0, 0);

    /// <summary>Walk to a point and stop there.</summary>
    /// <param name="x">East.</param>
    /// <param name="z">North.</param>
    /// <returns>The intent.</returns>
    public static Intent MoveTo(float x, float z) => new(IntentKind.MoveTo, 0f, 0, 0, 0, x, z);

    /// <summary>Get on the swoop, or off it.</summary>
    /// <param name="on">On (true) or off.</param>
    /// <returns>The intent.</returns>
    public static Intent Mount(bool on) => new(IntentKind.Mount, 0f, 0, 0, 0, On: on);

    /// <summary>Move on a heading at a speed class.</summary>
    /// <param name="heading">Radians.</param>
    /// <param name="speedClass">A class the server grants.</param>
    /// <returns>The intent.</returns>
    public static Intent Move(float heading, byte speedClass) => new(IntentKind.Move, heading, speedClass, 0, 0);

    /// <summary>Attack an entity, or 0 to stop attacking, and move while doing it: on a heading at a speed class, or stand (class 0).</summary>
    /// <remarks>
    /// <b>The motion travels with the target.</b> Naming a target moves nothing, and the body keeps the last <c>MoveDir</c> it was given: an attack that
    /// carried no motion left a bot running on through its target and back, all fight long (Loïc, 2026-10-26). So every attack says how to move too.
    /// </remarks>
    /// <param name="netId">The target.</param>
    /// <param name="heading">Radians, when <paramref name="speedClass"/> is not 0.</param>
    /// <param name="speedClass">A class the server grants, or 0 to stand.</param>
    /// <returns>The intent.</returns>
    public static Intent Attack(uint netId, float heading = 0f, byte speedClass = 0) => new(IntentKind.Attack, heading, speedClass, netId, 0);

    /// <summary>Take a mission at the terminal it stands at.</summary>
    public static Intent TakeMission => new(IntentKind.TakeMission, 0f, 0, 0, 0);

    /// <summary>Cancel the mission held.</summary>
    public static Intent AbandonMission => new(IntentKind.AbandonMission, 0f, 0, 0, 0);

    /// <summary>Trade at the bazaar it stands at.</summary>
    /// <param name="region">The bazaar's city, as a city plus one: the city a browse reads and a buy is collected in.</param>
    /// <returns>The intent.</returns>
    public static Intent Trade(int region) => new(IntentKind.Trade, 0f, 0, 0, region);

    /// <summary>Queue for a shuttle to a region's city.</summary>
    /// <param name="region">The destination, as a city plus one.</param>
    /// <returns>The intent.</returns>
    public static Intent Board(int region) => new(IntentKind.Board, 0f, 0, 0, region);
}
