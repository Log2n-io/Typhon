namespace SwgTatooine.Dashboard;

/// <summary>
/// Where the bots page's "Watch" opens the 3D client (bot-telemetry.md § 7): <see cref="ClientUrl"/> when <c>--client-url</c> names one, otherwise the
/// host the browser reached this page on, at the game server's port — the server serves the client, and the browser cannot reach the 127.0.0.1 the
/// dashboard reads it on from another machine.
/// </summary>
/// <param name="ClientUrl">The client's URL from <c>--client-url</c>, or null.</param>
/// <param name="ServerPort">The game server's port, from <c>--server</c>.</param>
public sealed record WatchLink(string ClientUrl, int ServerPort);
