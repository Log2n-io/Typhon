using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SwgTatooine.Bots;

/// <summary>What a provisioning pass did.</summary>
/// <param name="Created">Accounts signed up for the first time.</param>
/// <param name="LoggedIn">Accounts that existed and were logged into.</param>
/// <param name="Reused">Accounts whose stored token was still good, so no HTTP call was made.</param>
/// <param name="Failed">Accounts that could not be provisioned.</param>
/// <param name="FirstFailure">The first failure's message, or <see langword="null"/>.</param>
/// <param name="Elapsed">How long the pass took.</param>
public readonly record struct ProvisionReport(int Created, int LoggedIn, int Reused, int Failed, string FirstFailure, TimeSpan Elapsed)
{
    /// <summary>Whether every account in the population now has a usable token.</summary>
    public bool Complete => Failed == 0;
}

/// <summary>
/// Signs up and logs in a bot population over the real <c>/auth</c> endpoints.
/// </summary>
/// <remarks>
/// <para>
/// <b>The real HTTP path, because the point of a bot is that the server cannot tell.</b> Minting tokens in-process would skip
/// <c>AccountStore</c>, PBKDF2, the unique-name index and the rate limiter — four things a login storm is supposed to exercise, and
/// the four most likely to be what breaks at a thousand.
/// </para>
/// <para>
/// <b>Bounded concurrency, and the bound is about the SERVER.</b> Each signup and each login costs one PBKDF2 at 600 000
/// iterations, roughly 51 ms of a core, and the server runs them through a semaphore of <c>ProcessorCount / 4</c>
/// (<c>PasswordHash.Gate</c>). Pushing a thousand at once would not go faster — it would queue inside Kestrel, time out, and make
/// the fleet look like it failed to provision when it was merely early. Eight in flight keeps that gate fed without a queue.
/// </para>
/// <para>
/// <b>The address rate limiter is the thing that actually bites</b>, and it is not solved here. One bot box is one <c>/24</c>, so
/// the server's default allowance of ten attempts refilling one per six seconds puts a thousand signups at about ninety-nine
/// minutes. The server's <c>--auth-burst</c> and <c>--auth-refill-ms</c> are what make a fleet possible; a 429 here is reported
/// rather than retried forever, because retrying through a limiter is how a load generator turns a configuration mistake into an
/// hour of mystery.
/// </para>
/// </remarks>
public sealed class BotProvisioner : IDisposable
{
    /// <summary>How many signups and logins run at once by default.</summary>
    public const int DefaultConcurrency = 8;

    /// <summary>A token with less than this left on it is replaced rather than used.</summary>
    /// <remarks>
    /// Ten minutes. The token lasts two hours, so refreshing anything inside this window at connect time means a run shorter than
    /// about 110 minutes can never meet an expiry mid-flight — which is why there is no mid-run re-login path: it would be code
    /// that cannot be reached by any run we currently make, and therefore code nothing tests. A longer soak needs it, and that is
    /// the moment to write it.
    /// </remarks>
    public const int RefreshWindowSeconds = 600;

    private readonly HttpClient _http;
    private readonly BotAccountStore _store;
    private readonly string _key;
    private readonly int _concurrency;

    /// <summary>Added to every local index before a name and password are derived, so several processes can share one population.</summary>
    private readonly int _accountBase;

    /// <summary>Builds a provisioner.</summary>
    /// <param name="authBase">The HTTP base the <c>/auth</c> endpoints hang off, from <see cref="AuthBaseFrom"/>.</param>
    /// <param name="store">The accounts file.</param>
    /// <param name="key">The fleet key from <see cref="BotCredentials.ReadKeyOrThrow"/>.</param>
    /// <param name="concurrency">How many calls run at once; <see cref="DefaultConcurrency"/> when not given.</param>
    public BotProvisioner(Uri authBase, BotAccountStore store, string key, int concurrency = DefaultConcurrency, int accountBase = 0)
    {
        ArgumentNullException.ThrowIfNull(authBase);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(concurrency);
        ArgumentOutOfRangeException.ThrowIfNegative(accountBase);

        _store = store;
        _key = key;
        _concurrency = concurrency;
        _accountBase = accountBase;

        // A generous timeout, because a login is a PBKDF2 behind a semaphore and a thousand of them queue: the default 100 s is
        // right and the point of saying so is that a shorter one would read as a server fault.
        _http = new HttpClient { BaseAddress = authBase, Timeout = TimeSpan.FromSeconds(100) };
    }

    /// <summary>
    /// Turns a game endpoint into the HTTP base its <c>/auth</c> endpoints live on.
    /// </summary>
    /// <param name="endpoint">The game endpoint: <c>ws://</c>, <c>wss://</c>, <c>http://</c> or <c>https://</c>.</param>
    /// <returns>The base, or <see langword="null"/> when the scheme carries no HTTP path.</returns>
    /// <remarks>
    /// Same authority, HTTP scheme, no path — the browser client does the mirror image of this in <c>Viewport.tsx</c>, deriving
    /// <c>wss://{host}/ws</c> from the page it was served by. A <c>tcp://</c> endpoint has no HTTP side at all, so it answers null
    /// and the caller must be told to pass one explicitly rather than be given a guess.
    /// </remarks>
    public static Uri AuthBaseFrom(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var scheme = endpoint.Scheme switch
        {
            "ws" or "http" => "http",
            "wss" or "https" => "https",
            _ => null,
        };

        return scheme == null ? null : new UriBuilder(scheme, endpoint.Host, endpoint.Port).Uri;
    }

    /// <summary>
    /// Makes sure every bot in the population has an account and a usable token.
    /// </summary>
    /// <param name="count">How many bots.</param>
    /// <param name="progress">Called with the number finished, for a long pass; may be <see langword="null"/>.</param>
    /// <param name="ct">Cancels the pass.</param>
    /// <returns>What the pass did.</returns>
    public Task<ProvisionReport> EnsureAsync(int count, Action<int, int> progress, CancellationToken ct = default)
        => EnsureRangeAsync(0, count, progress, ct);

    /// <summary>
    /// Signs up or logs in a SLICE of the population: indices <paramref name="from"/> up to <paramref name="from"/> + <paramref name="count"/>.
    /// </summary>
    /// <param name="from">The first bot index in the slice.</param>
    /// <param name="count">How many bots.</param>
    /// <param name="progress">Called with the number finished, for a long pass; may be <see langword="null"/>.</param>
    /// <param name="ct">Cancels the pass.</param>
    /// <returns>What the pass did.</returns>
    /// <remarks>
    /// <b>A slice, because provisioning a whole population before anything happens is what made a ramp start slowly.</b> The fleet
    /// advances in chunks — provision these, connect these, let them walk, take the next — so one chunk signs up while the chunk
    /// before it is already playing. Ten thousand accounts took seventeen seconds during which nothing existed at all; a chunk of
    /// 250 takes about one, and every chunk after the first is overlapped with sessions that are already driving.
    /// <para>
    /// It also removes the reason the capacity had to be provisioned up front. Asking for headroom used to cost the full price
    /// immediately, so a run that only ever reached 500 sessions still signed up ten thousand accounts.
    /// </para>
    /// </remarks>
    public async Task<ProvisionReport> EnsureRangeAsync(int from, int count, Action<int, int> progress, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(from);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        var started = Stopwatch.StartNew();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var created = 0;
        var loggedIn = 0;
        var reused = 0;
        var failed = 0;
        var done = 0;
        string firstFailure = null;

        using var gate = new SemaphoreSlim(_concurrency);
        var work = new List<Task>(count);
        var results = new Outcome[count];

        for (var i = 0; i < count; i++)
        {
            // The ACCOUNT index is offset by the slice's start, while `results` stays slice-local. Conflating the two would write
            // every chunk's outcomes into the first chunk's slots and read the wrong account's token back.
            var index = from + i;
            var existing = _store[index];
            if (existing != null && !string.IsNullOrEmpty(existing.Token) && existing.TokenExpUnix - now > RefreshWindowSeconds)
            {
                results[i] = new Outcome(OutcomeKind.Reused, existing, null);
                continue;   // a token that is still good is the whole reason the file exists; a second run of the same fleet makes no HTTP calls at all
            }

            work.Add(ProvisionOneAsync(index, i, existing, gate, results, ct));
        }

        if (work.Count > 0)
        {
            // Progress on a separate cadence rather than inside each task: a thousand accounts behind a server-side PBKDF2 gate is
            // tens of seconds, and a run that prints nothing in that time reads as a hang.
            //
            // <b>Its own linked token, cancelled in a finally, and both halves of that matter.</b> Awaiting the reporter after the
            // work meant two defects at once: when the work FAULTED the await was skipped and the reporter ran for the lifetime of
            // the process — in the control panel, raising a change notification every five seconds for a run that had ended — and
            // when the work SUCCEEDED the pass still blocked until the reporter's next tick, so a six-account pass that took three
            // hundred milliseconds reported five seconds. Cancelling makes the reporter's lifetime the work's lifetime, which is
            // the only lifetime it should ever have had.
            using var reportingStopped = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var reporting = progress == null ? null : ReportProgressAsync(results, progress, count, reportingStopped.Token);
            try
            {
                await Task.WhenAll(work).ConfigureAwait(false);
            }
            finally
            {
                await reportingStopped.CancelAsync().ConfigureAwait(false);
                if (reporting != null)
                {
                    // The reporter swallows its own cancellation, so this observes it rather than re-throwing over whatever ended
                    // the work -- which is the exception the caller needs to see.
                    await reporting.ConfigureAwait(false);
                }
            }
        }

        foreach (var outcome in results)
        {
            switch (outcome.Kind)
            {
                case OutcomeKind.Created:
                    created++;
                    break;
                case OutcomeKind.LoggedIn:
                    loggedIn++;
                    break;
                case OutcomeKind.Reused:
                    reused++;
                    break;
                default:
                    failed++;
                    firstFailure ??= outcome.Failure;
                    break;
            }

            if (outcome.Record != null)
            {
                _store.Put(outcome.Record);
            }

            done++;
        }

        // One write for the whole pass. The records were gathered rather than stored as they arrived precisely so this is one
        // flush and not a thousand, and so the store is only ever touched from this thread.
        _store.Flush();
        progress?.Invoke(done, count);
        return new ProvisionReport(created, loggedIn, reused, failed, firstFailure, started.Elapsed);
    }

    /// <summary>Releases the HTTP client.</summary>
    public void Dispose() => _http.Dispose();

    /// <summary>Signs up or logs in one account.</summary>
    /// <param name="index">The bot index, which names the account.</param>
    /// <param name="slot">Where the outcome goes: the index WITHIN this slice, not the account index.</param>
    /// <param name="existing">What the file already knows, or <see langword="null"/>.</param>
    /// <param name="gate">The concurrency bound.</param>
    /// <param name="results">Where to put the outcome.</param>
    /// <param name="ct">Cancels the call.</param>
    private async Task ProvisionOneAsync(int index, int slot, BotAccountRecord existing, SemaphoreSlim gate, Outcome[] results, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // <b>Offset by the fleet's account base, which is what lets one population span several processes.</b> Names are derived
            // from the index alone (`bot000123`), so two processes both running indices 0..1999 would ask for the same accounts: the
            // second would log in to bodies the first is already driving, and the server's one-session-per-account guard would refuse
            // it. A base per process gives each one its own slice of the name space while the store stays keyed by local index.
            var name = BotCredentials.Name(_accountBase + index);
            var password = BotCredentials.Password(_key, _accountBase + index);

            // Signup first for an account the file has never seen, login first for one it has. The order matters only for the
            // number of round trips, because EITHER order recovers: signup answers 409 for a name that exists, and a login
            // refused with 401 tries a signup — the file outliving the server's accounts is as ordinary as the reverse, and on a
            // fresh box it is the normal case.
            var signupFirst = existing == null || !existing.SignedUp;
            var outcome = signupFirst
                ? await SignupThenLoginAsync(index, name, password, existing, ct).ConfigureAwait(false)
                : await LoginAsync(index, name, password, existing, signupOnUnauthorized: true, ct).ConfigureAwait(false);

            results[slot] = outcome;
        }
        catch (OperationCanceledException)
        {
            results[slot] = new Outcome(OutcomeKind.Failed, null, "cancelled");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException
            or System.Text.Json.JsonException or NotSupportedException)
        {
            // <b>JsonException and NotSupportedException are in here because of what is in front of this server.</b>
            // ReadFromJsonAsync throws the first for a body that is not the JSON we expect and the second for a content type that
            // is not JSON at all -- which is exactly what a captive portal, a proxy error page or a misrouted load balancer
            // answers 200 with. Without them one such response faulted the whole pass: EnsureAsync threw instead of answering
            // `Failed` with a message, contradicting this class's own rule that a refusal is reported rather than thrown, and
            // leaving the account's slot Pending forever.
            results[slot] = new Outcome(OutcomeKind.Failed, null, Describe(e));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Tries a signup, falling back to a login when the name already exists.</summary>
    /// <param name="index">The bot index.</param>
    /// <param name="name">The account name.</param>
    /// <param name="password">The derived password.</param>
    /// <param name="existing">What the file knows.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The outcome.</returns>
    private async Task<Outcome> SignupThenLoginAsync(int index, string name, string password, BotAccountRecord existing, CancellationToken ct)
    {
        using var response = await PostAsync("/auth/signup", name, password, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            // The account exists from an earlier run whose file we no longer have. Not an error. No signup retry on a 401 from
            // here (the signup that would be retried is the one that just answered 409), but a 401 AFTER that 409 is the one case
            // where the key is proven wrong rather than merely suspected, so this path carries the diagnosis.
            return await LoginAsync(index, name, password, existing, signupOnUnauthorized: false, ct, nameIsTaken: true)
                .ConfigureAwait(false);
        }

        if (!response.IsSuccessStatusCode)
        {
            return new Outcome(OutcomeKind.Failed, null, await RefusalAsync(response, "signup", ct).ConfigureAwait(false));
        }

        var minted = await response.Content.ReadFromJsonAsync(BotAccountJson.Default.BotAuthResponse, ct).ConfigureAwait(false);
        return minted == null
            ? new Outcome(OutcomeKind.Failed, null, "signup answered 200 with no body")
            : new Outcome(OutcomeKind.Created, Record(index, minted, existing), null);
    }

    /// <summary>Signs up after a login was refused, which is what tells the two causes of a 401 apart.</summary>
    /// <param name="index">The bot index.</param>
    /// <param name="name">The account name.</param>
    /// <param name="password">The derived password.</param>
    /// <param name="existing">What the file knows.</param>
    /// <param name="refusal">What the login answered, kept for the message a 409 then earns.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The outcome.</returns>
    /// <remarks>
    /// <b>The signup is the probe.</b> A 401 is the server's answer both for a wrong password and for a name it does not know, so
    /// only trying to create the name separates them: 201 means the account was simply gone and the fleet carries on, while 409
    /// means the name is taken by a password that is not ours — and that is the one case where the key really is to blame.
    /// </remarks>
    private async Task<Outcome> SignupAfterUnauthorizedAsync(int index, string name, string password, BotAccountRecord existing, string refusal,
        CancellationToken ct)
    {
        using var response = await PostAsync("/auth/signup", name, password, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return new Outcome(
                OutcomeKind.Failed,
                null,
                refusal + $" — the account exists but {BotCredentials.KeyVariable} does not match the one it was created with");
        }

        if (!response.IsSuccessStatusCode)
        {
            // BOTH reasons: the login's describes the ACCOUNT and the signup's only describes the probe. A server with signup
            // disabled answers 403 here, and reporting that alone would hide the 401 that sent us looking.
            var probe = await RefusalAsync(response, "signup", ct).ConfigureAwait(false);
            return new Outcome(OutcomeKind.Failed, null, $"{refusal}; the signup probe then answered {probe}");
        }

        var minted = await response.Content.ReadFromJsonAsync(BotAccountJson.Default.BotAuthResponse, ct).ConfigureAwait(false);
        return minted == null
            ? new Outcome(OutcomeKind.Failed, null, "signup answered 200 with no body")
            : new Outcome(OutcomeKind.Created, Record(index, minted, existing), null);
    }

    /// <summary>Logs in.</summary>
    /// <param name="index">The bot index.</param>
    /// <param name="name">The account name.</param>
    /// <param name="password">The derived password.</param>
    /// <param name="existing">What the file knows.</param>
    /// <param name="signupOnUnauthorized">
    /// Whether a 401 should try a signup before giving up. True when the login was chosen because the file claims the account
    /// exists, false when a signup has already answered 409 — see <see cref="SignupAfterUnauthorizedAsync"/>.
    /// </param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The outcome.</returns>
    private async Task<Outcome> LoginAsync(int index, string name, string password, BotAccountRecord existing, bool signupOnUnauthorized,
        CancellationToken ct, bool nameIsTaken = false)
    {
        using var response = await PostAsync("/auth/login", name, password, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var refusal = await RefusalAsync(response, "login", ct).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized || !signupOnUnauthorized)
            {
                // <b>The caller may already have PROVEN the name exists, and then a 401 has exactly one explanation.</b> A signup
                // answering 409 establishes it; this login then refusing the derived password leaves only the key. It is said here
                // because this is the STRONGER of the two paths - the other infers existence from a signup it has yet to try — and
                // it was reporting a bare "login refused: 401", the same dead end the fallback below exists to end.
                if (nameIsTaken && response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    refusal += $" — the account exists but {BotCredentials.KeyVariable} does not match the one it was created with";
                }

                return new Outcome(OutcomeKind.Failed, null, refusal);
            }

            // <b>A 401 does not mean the account exists.</b> The server answers the same "that name and password do not match an
            // account" for a name it has never heard of — deliberately, so that probing cannot enumerate accounts — and this code
            // used to read it as a key mismatch and stop. An accounts file outliving the server's database is not an error at
            // all: it is what a wiped database, a rebuilt instance or a fresh box looks like, and it cost a 10 000-bot fleet
            // every one of its sessions, reported as "no session opened; check the scheme and the port".
            return await SignupAfterUnauthorizedAsync(index, name, password, existing, refusal, ct).ConfigureAwait(false);
        }

        var minted = await response.Content.ReadFromJsonAsync(BotAccountJson.Default.BotAuthResponse, ct).ConfigureAwait(false);
        return minted == null
            ? new Outcome(OutcomeKind.Failed, null, "login answered 200 with no body")
            : new Outcome(OutcomeKind.LoggedIn, Record(index, minted, existing), null);
    }

    /// <summary>Posts a credential.</summary>
    /// <param name="route">The endpoint path.</param>
    /// <param name="name">The account name.</param>
    /// <param name="password">The derived password.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The response, for the caller to read and dispose.</returns>
    /// <remarks>
    /// A POST body, never a query string: a password in a URL is a password in the access log, the proxy's history and the metrics
    /// label. The server's own <c>AuthRequest</c> carries the same note for the same reason.
    /// </remarks>
    private Task<HttpResponseMessage> PostAsync(string route, string name, string password, CancellationToken ct)
        => _http.PostAsJsonAsync(route, new BotAuthRequest(name, password), BotAccountJson.Default.BotAuthRequest, ct);

    /// <summary>Builds the record a mint produces, keeping the resume token the file already held.</summary>
    /// <param name="index">The bot index.</param>
    /// <param name="minted">The server's answer.</param>
    /// <param name="existing">What the file knows, or <see langword="null"/>.</param>
    /// <returns>The record.</returns>
    private static BotAccountRecord Record(int index, BotAuthResponse minted, BotAccountRecord existing)
        => new()
        {
            Index = index,
            Name = minted.Name,
            SignedUp = true,
            Token = minted.Token,
            TokenExpUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + minted.ExpiresInSeconds,

            // Carried over, not dropped: a fresh token does not invalidate the continuity of the session the last run had, and
            // losing it here would silently turn every restart into a cold login — the exact thing the resume path exists to avoid.
            Resume = existing?.Resume,
        };

    /// <summary>Reads a refusal body into one line fit for a report.</summary>
    /// <param name="response">The failed response.</param>
    /// <param name="what">Which call it was.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The description.</returns>
    private static async Task<string> RefusalAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        var status = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
        string detail = null;
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            detail = body.Length > 200 ? body[..200] : body;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // The status alone is the finding; a body we could not read does not change it.
        }

        var hint = response.StatusCode == HttpStatusCode.TooManyRequests
            ? " — the server's /auth limiter is keyed on the caller's /24, so a whole bot box is one key: raise it with --auth-burst and --auth-refill-ms"
            : string.Empty;

        return what + " " + status + (string.IsNullOrWhiteSpace(detail) ? string.Empty : " " + detail) + hint;
    }

    /// <summary>One line describing a transport failure.</summary>
    /// <param name="e">The exception.</param>
    /// <returns>The description.</returns>
    private static string Describe(Exception e) => e.GetType().Name + ": " + e.Message;

    /// <summary>Prints progress while a pass runs.</summary>
    /// <param name="results">The outcome array, read as it fills.</param>
    /// <param name="progress">The reporter.</param>
    /// <param name="count">The population.</param>
    /// <param name="ct">Cancels the reporting.</param>
    private static async Task ReportProgressAsync(Outcome[] results, Action<int, int> progress, int count, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (true)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var done = 0;
            foreach (var outcome in results)
            {
                if (outcome.Kind != OutcomeKind.Pending)
                {
                    done++;
                }
            }

            progress(done, count);
            if (done >= count)
            {
                return;
            }
        }
    }

    /// <summary>What happened to one account.</summary>
    private enum OutcomeKind
    {
        /// <summary>Not finished.</summary>
        Pending = 0,

        /// <summary>Signed up.</summary>
        Created,

        /// <summary>Logged into an account that existed.</summary>
        LoggedIn,

        /// <summary>The stored token was still good.</summary>
        Reused,

        /// <summary>Could not be provisioned.</summary>
        Failed,
    }

    /// <summary>One account's outcome.</summary>
    /// <param name="Kind">What happened.</param>
    /// <param name="Record">The record to store, or <see langword="null"/>.</param>
    /// <param name="Failure">Why it failed, or <see langword="null"/>.</param>
    private readonly record struct Outcome(OutcomeKind Kind, BotAccountRecord Record, string Failure);
}
