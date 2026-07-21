using System.Diagnostics;
using Typhon.Engine;
using Typhon.Samples.Swg;
using Typhon.Schema.Definition;

namespace Typhon.MonitoringDemo.Scenarios;

/// <summary>
/// Simple baseline scenario adapted from unit tests.
/// Single-threaded, synchronous, no async - mirrors exactly what unit tests do.
/// Used to verify the DI/container setup works correctly.
/// </summary>
public class BaselineScenario : IScenario
{
    public string Name => "Baseline (Unit Test Style)";
    public string Description => "Single-threaded, synchronous CRUD. Mirrors unit test patterns to verify engine works.";

    public Task RunAsync(TyphonContext context, ScenarioConfig config, ScenarioStats stats, CancellationToken ct)
    {
        var engine = context.Engine;
        var rand = new Random(42);

        int loopCount = 0;

        // Run synchronously on the current thread - exactly like unit tests
        while (!ct.IsCancellationRequested)
        {
            var sw = Stopwatch.GetTimestamp();
            try
            {
                // Pattern 1: Create a Player entity in one transaction, read in another (unit test pattern)
                EntityId entityId;
                {
                    using var t = engine.CreateQuickTransaction();

                    var player = new Player
                    {
                        Name = SwgWorkload.S64($"Player-{loopCount}"), AccountId = loopCount,
                        Level = rand.Next(1, 91), ProfessionId = rand.Next(0, 16), CreatedAt = loopCount,
                    };
                    var membership = new Membership { Guild = EntityLink<GuildArch>.Null, GuildRank = rand.Next(0, 6) };
                    var wallet = new Wallet { Credits = rand.Next(0, 1_000_000), BankCredits = rand.Next(0, 100_000_000) };
                    var pos = new PlayerPosition { Bounds = SwgWorkload.RandomBounds(rand) };
                    var session = new Session { ConnectionId = loopCount, LatencyMs = rand.Next(5, 300) };
                    entityId = t.Spawn<PlayerArch>(
                        PlayerArch.Player.Set(in player),
                        PlayerArch.Membership.Set(in membership),
                        PlayerArch.Wallet.Set(in wallet),
                        PlayerArch.Position.Set(in pos),
                        PlayerArch.Session.Set(in session));

                    var committed = t.Commit();
                    if (committed)
                    {
                        stats.RecordCommit();
                        stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
                    }
                    else
                    {
                        stats.RecordRollback();
                        stats.RecordFailure(new Exception("Commit returned false"));
                        continue;
                    }
                }

                // Pattern 2: Read the entity back in a new transaction
                sw = Stopwatch.GetTimestamp();
                {
                    using var t = engine.CreateQuickTransaction();

                    if (t.TryOpen(entityId, out var entity))
                    {
                        var readWallet = entity.Read(PlayerArch.Wallet);
                        stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);

                        // Pattern 3: Update the entity (credit the player's wallet)
                        sw = Stopwatch.GetTimestamp();
                        ref var ww = ref t.OpenMut(entityId).Write(PlayerArch.Wallet);
                        ww.Credits = readWallet.Credits + rand.Next(1, 1000);

                        var committed = t.Commit();
                        if (committed)
                        {
                            stats.RecordCommit();
                            stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
                        }
                        else
                        {
                            stats.RecordRollback();
                            stats.RecordFailure(new Exception("Update commit returned false"));
                        }
                    }
                    else
                    {
                        stats.RecordFailure(new Exception($"Failed to read entity {entityId}"));
                        continue;
                    }
                }
            }
            catch (Exception ex)
            {
                stats.RecordFailure(ex);
            }

            ++loopCount;
        }

        return Task.CompletedTask;
    }
}
