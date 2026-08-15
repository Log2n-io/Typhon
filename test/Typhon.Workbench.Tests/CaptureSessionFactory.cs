using System.Net.Http.Json;
using System.Text.Json;
using Typhon.Workbench.Dtos.Profiler;
using Typhon.Workbench.Dtos.Sessions;

namespace Typhon.Workbench.Tests;

/// <summary>
/// Creates a session serving a capture, the way the Workbench now does it (#621).
/// </summary>
/// <remarks>
/// <para>
/// Every profiler fixture used to <c>POST /api/sessions/trace</c> with a capture path and get a session that <i>was</i>
/// that capture. With the two-entry-mode change, that route and the standalone trace session are gone: a capture is
/// reached through the database it was recorded against. The equivalent setup is therefore "make a database, put the
/// capture in its <c>profilings/</c>, open it, attach the capture" — which is also, not coincidentally, exactly what a
/// user now does.
/// </para>
/// <para>
/// Centralised rather than repeated in each fixture: ten copies of a four-step setup is ten places for the next change
/// to this flow to be missed, and the fixtures care about what the profiler endpoints return, not about how a session
/// comes to hold a capture.
/// </para>
/// </remarks>
internal static class CaptureSessionFactory
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Opens a database holding <paramref name="capturePath"/> and attaches it, returning the session.
    /// </summary>
    /// <remarks>
    /// The capture is <b>moved</b> into a fresh bundle rather than copied, so a fixture that builds a capture in the
    /// factory's demo directory does not leave a second copy behind that a later <c>profilings/</c> listing would count.
    /// </remarks>
    /// <param name="client">An authenticated client from <c>WorkbenchFactory.CreateAuthenticatedClient</c>.</param>
    /// <param name="rootDirectory">Where to create the database bundle — normally <c>WorkbenchFactory.DemoDirectory</c>.</param>
    /// <param name="capturePath">A capture built by <c>TraceFixtureBuilder</c> (or any valid <c>.typhon-trace</c>).</param>
    public static async Task<SessionDto> OpenWithCaptureAsync(HttpClient client, string rootDirectory, string capturePath)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);
        ArgumentException.ThrowIfNullOrEmpty(capturePath);

        // A distinct bundle per call keeps fixtures that create several sessions from sharing one database — and from
        // sharing its lock, which the second open would otherwise fail on (or, since #621, pause on).
        var bundle = Path.Combine(rootDirectory, "capture-" + Guid.NewGuid().ToString("N")[..8] + ".typhon");
        var profilings = TraceLocation.ProfilingsDirectoryOf(bundle);
        Directory.CreateDirectory(profilings);

        var fileName = Path.GetFileName(capturePath);
        var destination = Path.Combine(profilings, fileName);
        File.Move(capturePath, destination, overwrite: true);

        var openResponse = await client.PostAsJsonAsync("/api/sessions/file", new CreateFileSessionRequest(bundle));
        openResponse.EnsureSuccessStatusCode();
        var session = JsonSerializer.Deserialize<SessionDto>(await openResponse.Content.ReadAsStringAsync(), Json)!;

        var attach = new HttpRequestMessage(HttpMethod.Post, $"/api/sessions/{session.SessionId}/profile")
        {
            Content = JsonContent.Create(new AttachProfileRequest(fileName)),
        };
        attach.Headers.Add("X-Session-Token", session.SessionId.ToString());
        var attachResponse = await client.SendAsync(attach);
        attachResponse.EnsureSuccessStatusCode();

        // Re-read: attaching changes the session's capabilities and active profile, and callers assert on both.
        var refresh = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{session.SessionId}");
        refresh.Headers.Add("X-Session-Token", session.SessionId.ToString());
        var refreshed = await client.SendAsync(refresh);
        refreshed.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<SessionDto>(await refreshed.Content.ReadAsStringAsync(), Json)!;
    }

    /// <summary>
    /// The on-disk path of the capture attached to <paramref name="session"/>.
    /// </summary>
    /// <remarks>
    /// Needed because a session's <c>FilePath</c> is now the database bundle, not the capture — so a test that wants to
    /// touch the capture file itself (an overwrite-detection test, say) can no longer use it. Each bundle created by
    /// <see cref="OpenWithCaptureAsync"/> holds exactly one capture, so resolving it by enumeration is unambiguous.
    /// </remarks>
    public static string CapturePathOf(SessionDto session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var profilings = TraceLocation.ProfilingsDirectoryOf(session.FilePath);
        return Directory.EnumerateFiles(profilings, "*" + TraceLocation.TraceExtension).First();
    }
}
