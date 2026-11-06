using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Protocol;

namespace SwgTatooine.Bots;

/// <summary>
/// The horde's call (horde.md § 6): every bot hears the status the browser hears, and its brain decides whether to answer
/// (<c>BotBrain.ShouldAnswer</c>); the answer is the browser's command, <c>AnswerCall</c>, and the server's rally point steers the brain. A bot that
/// connects in the middle of a horde reads its status once (<c>GET /swg/horde</c>), since the broadcast is sent on change only.
/// </summary>
public sealed partial class BotSwarm
{
    private const string AnswerCallCommand = "AnswerCall";

    /// <summary>The fleet's horde: statuses heard, answers sent, rallies taken, arrivals at the point, commitments ended.</summary>
    public (long Heard, long Answered, long Rallies, long Arrived, long Ended) Hording()
    {
        var t = (Heard: 0L, Answered: 0L, Rallies: 0L, Arrived: 0L, Ended: 0L);
        foreach (var bot in Bots)
        {
            if (bot != null)
            {
                var h = bot.Horde();
                t = (t.Heard + h.Heard, t.Answered + h.Answered, t.Rallies + h.Rallies, t.Arrived + h.Arrived, t.Ended + h.Ended);
            }
        }

        return t;
    }

    private sealed partial class Bot
    {
        private int _hordeRead;

        // The status the session's GET found, handed from its task to the bot's tick.
        private readonly System.Collections.Concurrent.ConcurrentQueue<BotHordeStatus> _hordeFound = new();

        /// <summary>This bot's horde counts, as its brain keeps them.</summary>
        public (long Heard, long Answered, long Rallies, long Arrived, long Ended) Horde() =>
            _brain == null ? default : (_brain.HordeHeard, _brain.HordeAnswered, _brain.RalliesTaken, _brain.RalliesArrived, _brain.RalliesEnded);

        /// <summary>The rallies and statuses heard, into the brain; an answer sent when it says so; the running horde read once a session.</summary>
        private void PollHorde(long tick, float x, float z)
        {
            if (_brain == null)
            {
                return;
            }

            while (Heard.Rallies.TryDequeue(out var rally))
            {
                _brain.HearRally(rally.Event, rally.X, rally.Z, rally.State, tick);
            }

            if (_missionsUri != null && Interlocked.CompareExchange(ref _hordeRead, 1, 0) == 0)
            {
                _ = ReadHordeAsync();
            }

            while (_hordeFound.TryDequeue(out var found))
            {
                Consider(in found, tick, x, z);
            }

            while (Heard.Hordes.TryDequeue(out var status))
            {
                Consider(in status, tick, x, z);
            }

            while (Heard.Radars.TryDequeue(out var radar))
            {
                _brain.HearRadar(radar.X, radar.Z, radar.N);
            }
        }

        private void Consider(in BotHordeStatus s, long tick, float x, float z)
        {
            _brain.HearHorde(s.Event, s.Phase, s.City, s.HallX, s.HallZ, s.HallHp, s.FortHp, tick, s.FortX, s.FortZ);
            if (_brain.ShouldAnswer(s.Event, s.Phase, s.Help, s.X, s.Z, x, z, tick) && Client.Plan.CommandByName(AnswerCallCommand) != null)
            {
                _ = SendQuietly(AnswerCallCommand, new RecordValues { ["event"] = FieldValue.OfUInt64(s.Event) }, counter: false);
            }
        }

        /// <summary>The running horde's status, as <c>GET /swg/horde</c> gives it: what a session that connected after its last change has not heard.</summary>
        private async Task ReadHordeAsync()
        {
            try
            {
                using var response = await JournalHttp.GetAsync(new Uri(_missionsUri, "horde")).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return;
                }

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                var s = doc.RootElement;
                if (s.ValueKind != JsonValueKind.Object || !ulong.TryParse(s.GetProperty("event").GetString(), out var hordeEvent))
                {
                    return;
                }

                _hordeFound.Enqueue(new BotHordeStatus(
                    hordeEvent,
                    s.GetProperty("phase").GetInt32(),
                    s.GetProperty("city").GetInt32(),
                    s.GetProperty("help").GetInt32(),
                    s.GetProperty("startsIn").GetInt32(),
                    s.GetProperty("raiders").GetInt32(),
                    s.GetProperty("committed").GetInt32(),
                    s.GetProperty("present").GetInt32(),
                    s.GetProperty("x").GetSingle(),
                    s.GetProperty("z").GetSingle())
                {
                    FortX = s.TryGetProperty("fx", out var fx) ? fx.GetSingle() : 0f,
                    FortZ = s.TryGetProperty("fz", out var fz) ? fz.GetSingle() : 0f,
                    HallX = s.TryGetProperty("hx", out var hx) ? hx.GetSingle() : 0f,
                    HallZ = s.TryGetProperty("hz", out var hz) ? hz.GetSingle() : 0f,
                    HallHp = s.TryGetProperty("hall", out var hall) ? hall.GetInt32() : 0,
                    FortHp = s.TryGetProperty("fort", out var fort) ? fort.GetInt32() : 0,
                });
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException
                or FormatException)
            {
                // The next change's broadcast says it.
            }
        }
    }
}
