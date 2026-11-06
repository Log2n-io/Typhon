using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using SwgTatooine.Bots;
using SwgTatooine.Server;
using SwgTatooine.Server.Accounts;
using Typhon.Client;
using Typhon.Engine;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace SwgTatooine.Tests;

/// <summary>
/// Mail and /tell (mail.md): a player writes to another by name — stored, told online, listed newest first, read, deleted; refused with its reason when the
/// name, the subject or the recipient is wrong; the inbox kept to 100 and 30 days; a listing sold mails its seller; a /tell reaches the named player and
/// echoes to its sender, and is refused for a player offline.
/// </summary>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
internal sealed class MailChecks : ServedChecks
{
    private TatooineSim Start()
    {
        var config = Worlds.Served(Dir);
        config.CommandRateScale = Worlds.FastCommands;
        config.WorkerCount = 1;
        var sim = new TatooineSim(config);
        sim.Initialize();
        Harness = new SessionHarness(sim);
        return sim;
    }

    private sealed record Player(FakeLink Link, EntityId Character, string Name)
    {
        public long Key => unchecked((long)Character.RawValue);
    }

    private Player LogIn(TatooineSim sim, ulong account, long bindings) => LogIn(sim, TestAccounts.For(sim.Dbe, account), bindings);

    private Player LogIn(TatooineSim sim, EntityId account, long bindings)
    {
        var link = Harness.Connect(TatooineReplication.PlayerKind, account);
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= bindings, "the avatar to be spawned");
        var characterAccount = account;
        var character = EntityId.Null;
        Character profile = default;
        SessionHarness.Until(() => CharacterStore.TryFind(sim.Dbe, characterAccount, out character, out profile), "the account's character");
        SessionHarness.Until(() => Ledgers.TryWallet(sim, character, out _), "the wallet to be opened");
        Harness.Ticks(2);
        return new Player(link, character, profile.Name.AsString);
    }

    private void Breathe() => Harness.Ticks(Worlds.FastCommandGapTicks);

    private static MailOutcome Send(TatooineSim sim, Player from, string to, string subject, string text)
    {
        var done = sim.Bridge.SendPlayerMail(new MailRequest { From = from.Key, To = to, Subject = subject, Text = text });
        SessionHarness.Until(() => done.IsCompleted, "the mail's answer");
        return done.Result;
    }

    private static JsonElement Inbox(TatooineSim sim, Player p) => JsonDocument.Parse(MailEndpoints.InboxJson(sim.Dbe, p.Key)).RootElement;

    private static List<BotMailNotice> MailHeard(FakeLink link)
    {
        var events = Heard(link);
        var heard = new List<BotMailNotice>();
        while (events.Mail.TryDequeue(out var n))
        {
            heard.Add(n);
        }

        return heard;
    }

    private static List<BotWhisper> WhispersHeard(FakeLink link)
    {
        var events = Heard(link);
        var heard = new List<BotWhisper>();
        while (events.Whispers.TryDequeue(out var w))
        {
            heard.Add(w);
        }

        return heard;
    }

    private static BotEvents Heard(FakeLink link)
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

        return events;
    }

    private static int Refused(FakeLink link, byte reason) => Ledgers.Refusals(link).Count(r => r == reason);

    private static void ReadMail(FakeLink link, string mail)
        => link.SendCommand(nameof(Server.ReadMail), new RecordValues { ["mail"] = FieldValue.OfUInt64(ulong.Parse(mail)) });

    private static void DeleteMail(FakeLink link, ulong mail)
        => link.SendCommand(nameof(Server.DeleteMail), new RecordValues { ["mail"] = FieldValue.OfUInt64(mail) });

    private static void Tell(FakeLink link, string to, string text)
        => link.SendCommand(nameof(TellPlayer), new RecordValues { ["to"] = FieldValue.Of(to), ["text"] = FieldValue.Of(text) });

    /// <summary>
    /// ML-1, ML-2: a mail from A to B by name — stored with its text, told to B's session at once, listed in B's inbox newest first and unread; read, it is
    /// marked read; deleted, it is gone. A's own inbox has nothing.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void APlayerMail_IsToldListedReadAndDeleted()
    {
        using var sim = Start();
        var a = LogIn(sim, 0xA101, 1);
        var b = LogIn(sim, 0xA102, 2);

        var first = Send(sim, a, b.Name, "Hides wanted", "I buy Hide Mk II, 30 cr each.\nMeet me at the Port Sable bazaar.");
        var second = Send(sim, a, b.Name.ToUpperInvariant(), "Still?", "");
        Breathe();
        var inbox = Inbox(sim, b);
        var mails = inbox.GetProperty("mails").EnumerateArray().ToList();
        Assert.Multiple(() =>
        {
            Assert.That((first.Ok, second.Ok), Is.EqualTo((true, true)), first.Message + " / " + second.Message);
            Assert.That(inbox.GetProperty("unread").GetInt32(), Is.EqualTo(2));
            Assert.That(mails.Select(m => m.GetProperty("subject").GetString()), Is.EqualTo(new[] { "Still?", "Hides wanted" }), "newest first");
            Assert.That(mails.All(m => m.GetProperty("from").GetString() == a.Name && m.GetProperty("kind").GetString() == "player"), Is.True);
            Assert.That(MailEndpoints.MailJson(sim.Dbe, b.Key, first.Mail), Does.Contain("Meet me at the Port Sable bazaar."), "the text, with its line");
            Assert.That(MailEndpoints.MailJson(sim.Dbe, a.Key, first.Mail), Is.Null, "a mail is its owner's alone");
            Assert.That(Inbox(sim, a).GetProperty("mails").GetArrayLength(), Is.Zero);
            Assert.That(MailHeard(b.Link).Select(n => (n.Mail, n.From, n.Unread)),
                Is.EqualTo(new[] { ((ulong)first.Mail, a.Name, 1), ((ulong)second.Mail, a.Name, 2) }),
                "told to the recipient's session, with the unread count");
            Assert.That(MailHeard(a.Link), Is.Empty);
        });

        ReadMail(b.Link, first.Mail.ToString(System.Globalization.CultureInfo.InvariantCulture));
        SessionHarness.Until(() => Inbox(sim, b).GetProperty("unread").GetInt32() == 1, "the mail marked read");
        DeleteMail(b.Link, 0);
        SessionHarness.Until(() => Inbox(sim, b).GetProperty("mails").GetArrayLength() == 1, "the read one deleted");
        DeleteMail(b.Link, (ulong)second.Mail);
        SessionHarness.Until(() => Inbox(sim, b).GetProperty("mails").GetArrayLength() == 0, "the last one deleted");
        Assert.That(sim.Bridge.MailCount(3), Is.EqualTo(2), "two deleted");
    }

    /// <summary>
    /// ML-1 through the routes the host maps: <c>POST /swg/mail</c> answers its JSON — 401 without a token, the mail's id when sent, the reason when refused
    /// — and the recipient's <c>GET /swg/mail</c> and <c>GET /swg/mail/{id}</c> list it and give its text. A lambda bound as a plain request delegate once
    /// answered every one of them with an empty 200.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public async Task TheMailRoutes_AnswerTheirJson()
    {
        using var sim = Start();
        Assert.That(AccountStore.TrySignup(sim.Dbe, "mailwriter", "mail-writer-pass-1", out var writer), Is.EqualTo(AccountStore.SignupOutcome.Created));
        Assert.That(AccountStore.TrySignup(sim.Dbe, "mailreader", "mail-reader-pass-1", out var reader), Is.EqualTo(AccountStore.SignupOutcome.Created));
        var a = LogIn(sim, writer, 1);
        var b = LogIn(sim, reader, 2);
        MailEndpoints.Send = sim.Bridge.SendPlayerMail;
        MailEndpoints.Clock = () => (sim.Bridge.MailWorldTick, Worlds.ServedHz);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Error);
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.MapMail(sim.Dbe);
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses.First();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            HttpRequestMessage Post(EntityId account, string json)
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "/swg/mail") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                if (account != EntityId.Null)
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AuthToken.Mint(account));
                }

                return request;
            }

            using var huge = await client.SendAsync(Post(writer, "{\"to\":\"mailreader\",\"subject\":\"hi\",\"text\":\"" + new string('x', 9_000) + "\"}"));
            using var anonymous = await client.SendAsync(Post(EntityId.Null, "{\"to\":\"mailreader\",\"subject\":\"hi\"}"));
            using var sent = await client.SendAsync(Post(writer, "{\"to\":\"MailReader\",\"subject\":\"Hides\",\"text\":\"Thirty Hide Mk II.\"}"));
            using var nobody = await client.SendAsync(Post(writer, "{\"to\":\"nobody_here\",\"subject\":\"hi\"}"));
            var sentJson = await sent.Content.ReadAsStringAsync();
            var nobodyJson = await nobody.Content.ReadAsStringAsync();
            using var list = new HttpRequestMessage(HttpMethod.Get, "/swg/mail");
            list.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AuthToken.Mint(reader));
            var inbox = await (await client.SendAsync(list)).Content.ReadAsStringAsync();
            var id = JsonDocument.Parse(sentJson).RootElement.GetProperty("mail").GetString();
            using var one = new HttpRequestMessage(HttpMethod.Get, $"/swg/mail/{id}");
            one.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AuthToken.Mint(reader));
            var mail = await (await client.SendAsync(one)).Content.ReadAsStringAsync();
            using var third = await client.SendAsync(Post(writer, "{\"to\":\"mailreader\",\"subject\":\"three\"}"));
            using var fast = await client.SendAsync(Post(writer, "{\"to\":\"mailreader\",\"subject\":\"four\"}"));
            Assert.Multiple(() =>
            {
                Assert.That(huge.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge), "a body past the cap is not read");
                Assert.That(third.StatusCode, Is.EqualTo(HttpStatusCode.OK), "three at once: the burst");
                Assert.That(fast.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests), "a fourth at once: past one a second");
                Assert.That(anonymous.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                Assert.That((sent.StatusCode, sentJson), Is.EqualTo((HttpStatusCode.OK, $"{{\"ok\":true,\"mail\":\"{id}\"}}")));
                Assert.That(nobody.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
                Assert.That(nobodyJson, Does.Contain($"\"reason\":{TatooineReplication.MailNoSuchPlayer}"));
                Assert.That(inbox, Does.Contain("\"subject\":\"Hides\"").And.Contain("\"from\":\"mailwriter\"").And.Contain("\"unread\":1"));
                Assert.That(mail, Does.Contain("\"text\":\"Thirty Hide Mk II.\""));
                Assert.That(b.Name, Is.EqualTo("mailreader"));
            });
        }
        finally
        {
            await app.StopAsync();
            MailEndpoints.Send = null;
            MailEndpoints.Clock = null;
        }
    }

    /// <summary>
    /// A Workshop pass that does not commit writes no mail: the player's is refused and sent again by hand, the game's queued again and written once by
    /// the next pass — never twice — and the recipient told only of what committed.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void AFailedPass_WritesNoMail_AndTheGamesIsWrittenOnceAfter()
    {
        using var sim = Start();
        var a = LogIn(sim, 0xA601, 1);
        var b = LogIn(sim, 0xA602, 2);
        Breathe();
        sim.Bridge.FailNextWorkshopWriteForChecks();
        sim.Bridge.QueueSystemMail(b.Key, MailKind.Sold, 7, "Sold: 5 Hide Mk II", "bought");
        var refused = Send(sim, a, b.Name, "Lost", "");
        Assert.That((refused.Ok, refused.Reason), Is.EqualTo((false, TatooineReplication.CraftFailed)), refused.Message);

        SessionHarness.Until(() => Inbox(sim, b).GetProperty("mails").GetArrayLength() == 1, "the game's mail, written by the next pass");
        Harness.Ticks(10);
        var mails = Inbox(sim, b).GetProperty("mails").EnumerateArray().ToList();
        Assert.Multiple(() =>
        {
            Assert.That(
                mails.Select(m => m.GetProperty("subject").GetString()),
                Is.EqualTo(new[] { "Sold: 5 Hide Mk II" }),
                "written once, and the refused one not at all");
            Assert.That(MailHeard(b.Link).Select(n => n.Subject), Is.EqualTo(new[] { "Sold: 5 Hide Mk II" }), "told once, of what committed");
        });
    }

    /// <summary>ML-1: an unknown name, a mail to oneself, and a read or delete of a mail that is not the player's are refused with their reasons.</summary>
    [Test]
    [CancelAfter(120_000)]
    public void BadMail_IsRefusedWithItsReason()
    {
        using var sim = Start();
        var a = LogIn(sim, 0xA201, 1);
        var b = LogIn(sim, 0xA202, 2);
        var nobody = Send(sim, a, "nobody_by_this_name", "Hello", "");
        var self = Send(sim, a, a.Name, "Hello", "");
        var mine = Send(sim, a, b.Name, "Hello", "");
        Assert.Multiple(() =>
        {
            Assert.That((nobody.Ok, nobody.Reason), Is.EqualTo((false, TatooineReplication.MailNoSuchPlayer)));
            Assert.That((self.Ok, self.Reason), Is.EqualTo((false, TatooineReplication.MailBad)));
            Assert.That(mine.Ok, Is.True);
        });

        Breathe();
        ReadMail(a.Link, mine.Mail.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Breathe();
        DeleteMail(a.Link, (ulong)mine.Mail);
        SessionHarness.Until(() => Refused(a.Link, TatooineReplication.MailUnknown) == 2, "B's mail is not A's to read or delete");
        Assert.That(Inbox(sim, b).GetProperty("mails").GetArrayLength(), Is.EqualTo(1), "nothing of it was written");
    }

    /// <summary>
    /// ML-3: past 100 mails the oldest is dropped; a mail past 30 days — staged with an old tick, before the inbox is first read — is swept by the next
    /// send and never listed.
    /// </summary>
    [Test]
    [CancelAfter(180_000)]
    public void TheInbox_KeepsAHundred_AndThirtyDays()
    {
        using var sim = Start();
        var a = LogIn(sim, 0xA301, 1);
        var b = LogIn(sim, 0xA302, 2);
        sim.WriteInTick((tx, worldTick) =>
        {
            var old = new MailCore { Owner = b.Key, From = a.Key, Kind = MailKind.Player, SentTick = worldTick - Mail.LifeTicks(Worlds.ServedHz) - 10,
                Subject = (String64)"ancient" };
            tx.Spawn<MailArch>(MailArch.Core.Set(in old));
        });
        Assert.That(Inbox(sim, b).GetProperty("mails").GetArrayLength(), Is.Zero, "past its life it is never listed");

        string firstId = null;
        for (var i = 0; i < Mail.MaxPerCharacter + 1; i++)
        {
            var sent = Send(sim, a, b.Name, "n" + i, "");
            Assert.That(sent.Ok, Is.True, sent.Message);
            firstId ??= sent.Mail.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        Breathe();
        var mails = Inbox(sim, b).GetProperty("mails").EnumerateArray().ToList();
        Assert.Multiple(() =>
        {
            Assert.That(mails, Has.Count.EqualTo(Mail.MaxPerCharacter));
            Assert.That(mails.Any(m => m.GetProperty("id").GetString() == firstId), Is.False, "the oldest dropped first");
            Assert.That(mails[0].GetProperty("subject").GetString(), Is.EqualTo("n" + Mail.MaxPerCharacter));
            Assert.That(sim.Bridge.MailCount(5), Is.EqualTo(1), "the old one swept as expired");
            Assert.That(sim.Bridge.MailCount(4), Is.EqualTo(1), "one dropped past the cap");
        });
    }

    /// <summary>ML-4: a listing bought mails its seller — what, for how much, the tax — and a listing expired mails it where to collect it.</summary>
    [Test]
    [CancelAfter(180_000)]
    public void TheBazaar_MailsTheSeller()
    {
        using var sim = Start();
        var bazaar = sim.Index.Bazaars[0];
        var a = LogIn(sim, 0xA401, 1);
        var b = LogIn(sim, 0xA402, 2);
        Worlds.Park(sim, a.Link.Session, bazaar.X + 2.5f, bazaar.Z);
        Worlds.Park(sim, b.Link.Session, bazaar.X - 2.5f, bazaar.Z);
        sim.Bridge.StageTransfer(ItemLedger.World, a.Key, ItemCatalog.Hide, 2, 20, ItemMoveKind.Kill, 1);
        SessionHarness.Until(() => Market.Units(sim, a.Character, ItemCatalog.Hide, 2) == 20, "the staged hide");
        Harness.Ticks(2);

        Market.List(a.Link, ItemCatalog.Hide, 2, 5, 7);
        SessionHarness.Until(() => Market.Listings(sim).Values.Any(l => l.Quantity == 5), "the listing");
        var listing = Market.Listings(sim).Single(l => l.Value.Quantity == 5).Key;
        Breathe();
        Market.Buy(b.Link, listing, 7);
        SessionHarness.Until(() => Inbox(sim, a).GetProperty("mails").GetArrayLength() == 1, "the sale's mail");

        sim.Bridge.SetListingLifeTicksForChecks(Worlds.ServedHz);
        Breathe();
        Market.List(a.Link, ItemCatalog.Hide, 2, 3, 50);
        SessionHarness.Until(() => Inbox(sim, a).GetProperty("mails").GetArrayLength() == 2, "the expiry's mail");
        SessionHarness.Until(() => MailHeard(a.Link).Count == 2, "the expiry's mail told");
        var mails = Inbox(sim, a).GetProperty("mails").EnumerateArray().ToList();
        Assert.Multiple(() =>
        {
            Assert.That(mails[1].GetProperty("kind").GetString(), Is.EqualTo("sold"));
            Assert.That(mails[1].GetProperty("subject").GetString(), Is.EqualTo("Sold: 5 Hide Mk II"));
            Assert.That(mails[1].GetProperty("from").GetString(), Is.Empty, "from the game");
            Assert.That(
                MailEndpoints.MailJson(sim.Dbe, a.Key, long.Parse(mails[1].GetProperty("id").GetString())),
                Does.Contain($"{b.Name} bought your 5 Hide Mk II for 35 cr"));
            Assert.That(mails[0].GetProperty("kind").GetString(), Is.EqualTo("expired"));
            Assert.That(mails[0].GetProperty("subject").GetString(), Is.EqualTo("Listing expired: 3 Hide Mk II"));
            Assert.That(MailHeard(a.Link).Select(n => n.Kind), Is.EqualTo(new[] { (int)MailKind.Sold, (int)MailKind.Expired }));
        });
    }

    /// <summary>ML-5: a /tell reaches the named player's session and echoes to its sender; to a name nobody has, to oneself, or to a player offline,
    /// refused.</summary>
    [Test]
    [CancelAfter(120_000)]
    public void ATell_ReachesThePlayer_AndEchoes()
    {
        using var sim = Start();
        var a = LogIn(sim, 0xA501, 1);
        var b = LogIn(sim, 0xA502, 2);
        Tell(a.Link, b.Name, "Meet me at the cantina.");
        SessionHarness.Until(() => WhispersHeard(b.Link).Count == 1, "the tell heard");
        Breathe();
        Tell(a.Link, "nobody_by_this_name", "hello?");
        Breathe();
        Tell(a.Link, a.Name, "me?");
        SessionHarness.Until(() => Refused(a.Link, TatooineReplication.MailNoSuchPlayer) == 1 && Refused(a.Link, TatooineReplication.MailBad) == 1,
            "the bad tells refused");
        Assert.Multiple(() =>
        {
            Assert.That(WhispersHeard(b.Link), Is.EqualTo(new[] { new BotWhisper(a.Name, "Meet me at the cantina.", false) }));
            Assert.That(WhispersHeard(a.Link), Is.EqualTo(new[] { new BotWhisper(b.Name, "Meet me at the cantina.", true) }), "the sender's echo");
            Assert.That(sim.Bridge.MailCount(6), Is.EqualTo(1));
        });

        // B logs out: a tell to B now is refused as offline — mail reaches an offline player, a tell does not.
        b.Link.Close(CloseCodes.Normal, "logging out");
        SessionHarness.Until(() => !TatooineReplication.IsPlaying(TestAccounts.For(sim.Dbe, 0xA502)), "B logged out");
        Breathe();
        Tell(a.Link, b.Name, "still there?");
        SessionHarness.Until(() => Refused(a.Link, TatooineReplication.TellOffline) == 1, "a tell to a player offline refused");
    }
}
