using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Protocol;

namespace SwgTatooine.Bots;

/// <summary>
/// Mail and /tell (mail.md § 7): every bot acts on the mail it is told of — a hopper full, an extractor stopped or its upkeep running out sends an owner to
/// tend it at once, with no wait for its next read — and deletes it, at the command's pace; mail that waited while it was offline is read and deleted once
/// it is back. One bot in <see cref="BotSwarmOptions.MailPercent"/> of those that trade also writes a mail to another bot of the fleet about once an hour,
/// and a /tell about once every ten minutes, so both paths carry load.
/// </summary>
public sealed partial class BotSwarm
{
    private const string DeleteMailCommand = "DeleteMail";
    private const string TellCommand = "TellPlayer";

    // The server's MailKind numbers the bot acts on.
    private const int MailHopperFull = 2;
    private const int MailUpkeepLow = 3;
    private const int MailStopped = 4;

    /// <summary>How often a writing bot mails another, and tells one, seconds; each drawn ±50 %.</summary>
    private const double MailEverySeconds = 3600;

    private const double TellEverySeconds = 600;

    /// <summary>Deletes sent a second at most: under the server's 4 a second, so a seller's burst of sale mail never spends the rate.</summary>
    private const int DeletesPerSecond = 2;

    /// <summary>The fleet's mail, summed: told, acted on, sent, refused; /tells said and heard.</summary>
    public (long Heard, long Acted, long Sent, long Refused, long Tells, long TellsHeard) Mailing()
    {
        var t = (Heard: 0L, Acted: 0L, Sent: 0L, Refused: 0L, Tells: 0L, TellsHeard: 0L);
        foreach (var bot in Bots)
        {
            if (bot == null)
            {
                continue;
            }

            t.Heard += Volatile.Read(ref bot.MailHeard);
            t.Acted += Volatile.Read(ref bot.MailActed);
            t.Sent += Volatile.Read(ref bot.MailSent);
            t.Refused += Volatile.Read(ref bot.MailRefused);
            t.Tells += Volatile.Read(ref bot.TellsSent);
            t.TellsHeard += Volatile.Read(ref bot.TellsHeard);
        }

        return t;
    }

    private sealed partial class Bot
    {
        public long MailHeard;
        public long MailActed;
        public long MailSent;
        public long MailRefused;
        public long TellsSent;
        public long TellsHeard;

        private bool _writesMail;
        private Uri _mailUri;
        private Func<int, string> _nameOf;
        private int _fleet;
        private int _self;
        private uint _mailRandom;
        private long _nextMailTick;
        private long _nextTellTick;
        private long _nextDeleteTick;
        private int _mailSending;
        private int _inboxRead;
        private readonly Queue<ulong> _mailToDelete = new();

        // Mail ids the inbox read found, handed over from its task to the bot's tick.
        private readonly System.Collections.Concurrent.ConcurrentQueue<(ulong Mail, int Kind)> _inboxFound = new();

        /// <summary>Wires the mail: every bot with a token acts on its mail; the next <see cref="BotSwarmOptions.MailPercent"/> by index also writes.</summary>
        private void SetUpMail(int index, BotSwarmOptions options)
        {
            _mailUri = _missionsUri == null ? null : new Uri(_missionsUri, "mail");
            _nameOf = options.NameFor;
            _fleet = options.Count;
            _self = index;
            // Its own stream, not the brain's: the same multiplier would read the brain's sequence a second time (BotSwarm's seeds).
            _mailRandom = (uint)((index * 3432918353u) ^ 0x5BD1E995u) | 1u;
            _writesMail = _trades && _nameOf != null && _fleet > 1 && index % 100 >= 100 - options.MailPercent;
            _nextMailTick = Delay(MailEverySeconds);
            _nextTellTick = Delay(TellEverySeconds);
        }

        private long Delay(double seconds) => (long)(seconds * (0.5 + ((NextMail() >> 8) / 16_777_216d)) * Math.Max(1L, _ticksPerSecond));

        private uint NextMail() => MarketPlan.Next(ref _mailRandom);

        /// <summary>
        /// The mail and the /tells this session was told of, taken: what an extractor's mail is about makes the owner's installations due now, so the next
        /// errand is the tend; every mail is queued for deletion — the browser's read-then-delete — sent at <see cref="DeletesPerSecond"/>.
        /// </summary>
        private void TakeMail()
        {
            while (Heard.Mail.TryDequeue(out var mail))
            {
                Interlocked.Increment(ref MailHeard);
                Act(mail.Mail, mail.Kind);
            }

            while (_inboxFound.TryDequeue(out var found))
            {
                Act(found.Mail, found.Kind);
            }

            while (Heard.Whispers.TryDequeue(out var whisper))
            {
                if (!whisper.Outgoing)
                {
                    Interlocked.Increment(ref TellsHeard);
                }
            }
        }

        private void Act(ulong mail, int kind)
        {
            if (Installs && kind is MailHopperFull or MailUpkeepLow or MailStopped)
            {
                _installsReadAt = long.MinValue / 2;
                Interlocked.Increment(ref MailActed);
            }

            // 0 would mean "every read mail": never sent for a mail of its own.
            if (mail != 0)
            {
                _mailToDelete.Enqueue(mail);
            }
        }

        /// <summary>
        /// Once a session can, its inbox read — mail that waited while it was offline acted on and deleted; then each tick its deletes, paced, and a
        /// writing bot's mail and /tell when due: to another bot of the fleet by name, never itself.
        /// </summary>
        private void PollMail(long tick)
        {
            TakeMail();
            if (string.IsNullOrEmpty(_token) || _mailUri == null)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _inboxRead, 1, 0) == 0)
            {
                _ = ReadInboxAsync();
            }

            if (_mailToDelete.Count > 0 && tick >= _nextDeleteTick && Client.Plan.CommandByName(DeleteMailCommand) != null)
            {
                _nextDeleteTick = tick + Math.Max(1L, _ticksPerSecond / DeletesPerSecond);
                _ = SendQuietly(DeleteMailCommand, new RecordValues { ["mail"] = FieldValue.OfUInt64(_mailToDelete.Dequeue()) }, counter: false);
            }

            if (!_writesMail)
            {
                return;
            }

            if (tick >= _nextTellTick)
            {
                _nextTellTick = tick + Delay(TellEverySeconds);
                if (Other() is { } name && Client.Plan.CommandByName(TellCommand) != null)
                {
                    var values = new RecordValues { ["to"] = FieldValue.Of(name), ["text"] = FieldValue.Of("Anyone selling Hide?") };
                    _ = SendQuietly(TellCommand, values, counter: true);
                }
            }

            if (tick >= _nextMailTick && Interlocked.CompareExchange(ref _mailSending, 1, 0) == 0)
            {
                _nextMailTick = tick + Delay(MailEverySeconds);
                if (Other() is { } name)
                {
                    _ = SendMailAsync(name);
                }
                else
                {
                    Volatile.Write(ref _mailSending, 0);
                }
            }
        }

        /// <summary>
        /// A command sent off the tick, its fault swallowed — the transport down is the session's to notice; a /tell counted only once sent.
        /// </summary>
        private async Task SendQuietly(string command, RecordValues values, bool counter)
        {
            try
            {
                var seq = await Client.SendCommandAsync(command, values).ConfigureAwait(false);
                if (counter && seq != 0)
                {
                    Interlocked.Increment(ref TellsSent);
                }
            }
            catch (Exception e) when (e is InvalidOperationException or OperationCanceledException or System.IO.IOException)
            {
                // Not sent: a /tell is the next one's chance, a delete the next login's inbox read.
            }
        }

        private string Other()
        {
            // Never itself: one draw over the others, the index past its own shifted by one.
            var other = (int)(NextMail() % (uint)(_fleet - 1));
            return _nameOf(other >= _self ? other + 1 : other);
        }

        /// <summary>The inbox as it stands at the first poll: each mail acted on and deleted, as one told live would be.</summary>
        private async Task ReadInboxAsync()
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, _mailUri);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
                using var response = await JournalHttp.SendAsync(request).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return;
                }

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                foreach (var m in doc.RootElement.GetProperty("mails").EnumerateArray())
                {
                    if (ulong.TryParse(m.GetProperty("id").GetString(), out var id))
                    {
                        var kind = m.GetProperty("kind").GetString() switch
                        {
                            "hopperfull" => MailHopperFull,
                            "upkeeplow" => MailUpkeepLow,
                            "stopped" => MailStopped,
                            _ => 0,
                        };
                        _inboxFound.Enqueue((id, kind));
                    }
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // The next session's first poll reads it again.
            }
        }

        private async Task SendMailAsync(string to)
        {
            try
            {
                var body = JsonSerializer.Serialize(new
                {
                    to,
                    subject = "Trade",
                    text = "Hello! I have materials to spare, and I buy cores of my band. Find me at the bazaar.",
                });
                using var request = new HttpRequestMessage(HttpMethod.Post, _mailUri) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
                using var response = await JournalHttp.SendAsync(request).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    Interlocked.Increment(ref MailSent);
                }
                else
                {
                    Interlocked.Increment(ref MailRefused);
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                Interlocked.Increment(ref MailRefused);
            }
            finally
            {
                Volatile.Write(ref _mailSending, 0);
            }
        }
    }
}
