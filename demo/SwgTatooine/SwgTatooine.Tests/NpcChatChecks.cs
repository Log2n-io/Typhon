using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SwgTatooine.Server;

namespace SwgTatooine.Tests;

/// <summary>A city NPC speaks every 1 to 3 minutes, at random, never under one (Loïc, 2026-11-04).</summary>
[TestFixture]
public sealed class NpcChatChecks
{
    [TestCase(1)]
    [TestCase(4)]
    public void ANpc_SpeaksEveryOneToThreeMinutes_AtRandom(int k)
    {
        const int Hz = 20;
        var gaps = new List<long>();
        for (var key = 1L; key <= 40; key++)
        {
            var last = -1L;
            for (long tick = 0; tick < 3_600L * Hz; tick += k)
            {
                if (!SimBridge.NpcSpeaksNow(tick, key, Hz, k, 0xC0FFEEu))
                {
                    continue;
                }

                if (last >= 0)
                {
                    gaps.Add(tick - last);
                }

                last = tick;
            }
        }

        var seconds = gaps.Select(g => g / (double)Hz).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(seconds.Min(), Is.GreaterThanOrEqualTo(SimBridge.NpcChatMinSeconds), "a line under a minute after the last");
            Assert.That(seconds.Max(), Is.LessThanOrEqualTo(SimBridge.NpcChatMaxSeconds), "a silence past three minutes");
            Assert.That(seconds.Average(), Is.InRange(110d, 130d));
            Assert.That(seconds.Count(s => s < 90), Is.GreaterThan(0), "never a short delay: not random");
            Assert.That(seconds.Count(s => s > 150), Is.GreaterThan(0), "never a long delay: not random");
        });
    }
}
