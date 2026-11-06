using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using SwgTatooine.Server;

namespace SwgTatooine.Tests;

/// <summary>
/// The web client's copies of the server's numbers (client.md C38-C39) — the armour slots' shares and the mitigation cap the Equipment tab totals with,
/// a waypoint name's longest, the most waypoints a character keeps, the waypoint refusals' codes — read from its source and held to the server's.
/// </summary>
[TestFixture]
internal sealed class ClientCopyChecks
{
    private static string ClientSource(string relative)
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "SwgTatooine.Client", "src", relative);
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }
        }

        Assert.Ignore($"no client source beside this checkout ({relative})");
        return null;
    }

    private static int Constant(string source, string name)
    {
        var m = Regex.Match(source, $@"export const {name}\s*=\s*([\d_]+);");
        Assert.That(m.Success, Is.True, $"{name} is not a number constant in the client");
        return int.Parse(m.Groups[1].Value.Replace("_", ""));
    }

    [Test]
    public void TheArmourTotals_AreTheServers()
    {
        var items = ClientSource(Path.Combine("data", "items.ts"));
        var shares = Regex.Match(items, @"ARMOR_SHARE_PER_MILLE: readonly number\[\] = \[([\d,\s]+)\];");
        Assert.That(shares.Success, Is.True, "ARMOR_SHARE_PER_MILLE");
        Assert.Multiple(() =>
        {
            Assert.That(shares.Groups[1].Value.Split(',').Select(s => int.Parse(s.Trim())), Is.EqualTo(EquipSlot.SharePerMille.Skip(1)));
            Assert.That(Constant(items, "MITIGATION_CAP_PER_MILLE"), Is.EqualTo(Equipment.MitigationCapPerMille));
        });
    }

    [Test]
    public void TheWaypointRules_AreTheServers()
    {
        var waypoints = ClientSource(Path.Combine("data", "waypoints.ts"));
        var market = ClientSource(Path.Combine("data", "market.ts"));
        Assert.Multiple(() =>
        {
            Assert.That(Constant(waypoints, "WAYPOINT_NAME_MAX_BYTES"), Is.EqualTo(Waypoints.NameMaxBytes));
            Assert.That(Constant(waypoints, "WAYPOINTS_MAX"), Is.EqualTo(Waypoints.MaxPerCharacter));
            Assert.That(Constant(waypoints, "KIND_CUSTOM"), Is.EqualTo(WaypointKind.Custom));
            Assert.That(Constant(waypoints, "KIND_INSTALLATION"), Is.EqualTo(WaypointKind.Installation));
            foreach (var reason in new[]
                     {
                         TatooineReplication.WaypointBad, TatooineReplication.WaypointsFull, TatooineReplication.WaypointUnknown,
                         TatooineReplication.InstallationNotHere, TatooineReplication.InstallationInTown,
                     })
            {
                Assert.That(market, Does.Contain($"  {reason}: '"), $"the client names no refusal {reason}");
            }
        });
    }
}
