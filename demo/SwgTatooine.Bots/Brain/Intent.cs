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
}

/// <summary>One decision.</summary>
/// <param name="Kind">What to do.</param>
/// <param name="Heading">Where to go, radians, for <see cref="IntentKind.Move"/>.</param>
/// <param name="SpeedClass">How fast, as a class the server grants, for <see cref="IntentKind.Move"/>.</param>
/// <param name="NetId">What to attack, for <see cref="IntentKind.Attack"/>.</param>
/// <param name="City">Where to fly, for <see cref="IntentKind.Board"/>.</param>
public readonly record struct Intent(IntentKind Kind, float Heading, byte SpeedClass, uint NetId, int City)
{
    /// <summary>Nothing this tick.</summary>
    public static Intent None => new(IntentKind.None, 0f, 0, 0, -1);

    /// <summary>Move on a heading at a speed class.</summary>
    /// <param name="heading">Radians.</param>
    /// <param name="speedClass">A class the server grants.</param>
    /// <returns>The intent.</returns>
    public static Intent Move(float heading, byte speedClass) => new(IntentKind.Move, heading, speedClass, 0, -1);

    /// <summary>Attack an entity, or 0 to stop attacking.</summary>
    /// <param name="netId">The target.</param>
    /// <returns>The intent.</returns>
    public static Intent Attack(uint netId) => new(IntentKind.Attack, 0f, 0, netId, -1);

    /// <summary>Queue for a shuttle to a city.</summary>
    /// <param name="city">The destination.</param>
    /// <returns>The intent.</returns>
    public static Intent Board(int city) => new(IntentKind.Board, 0f, 0, 0, city);
}
