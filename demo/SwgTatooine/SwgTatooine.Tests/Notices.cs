using System.Collections.Generic;
using SwgTatooine.Bots;
using SwgTatooine.Server;
using Typhon.Client;
using Typhon.Engine;
using Typhon.Protocol;

namespace SwgTatooine.Tests;

/// <summary>The <c>PlayerNotice</c>s a session was told, decoded from its frames as a bot decodes them (<see cref="BotEvents"/>).</summary>
internal static class Notices
{
    /// <summary>Every notice in every <c>TICK</c> the link was sent, oldest first.</summary>
    public static List<BotNotice> Heard(FakeLink link)
    {
        var events = new BotEvents();
        var applier = new FrameApplier(new WorldStore(link.Plan), events);
        foreach (var message in link.Sent)
        {
            if (message.Length > 0 && message[0] == MessageTypes.Tick)
            {
                applier.Apply(message);
            }
        }

        var heard = new List<BotNotice>();
        while (events.Notices.TryDequeue(out var n))
        {
            heard.Add(n);
        }

        return heard;
    }

    /// <summary>A character's name, as a notice names the other side.</summary>
    public static string NameOf(TatooineSim sim, EntityId character)
    {
        using var tx = sim.Dbe.CreateQuickTransaction();
        return tx.Open(character).Read(CharacterArch.Profile).Name.AsString;
    }
}
