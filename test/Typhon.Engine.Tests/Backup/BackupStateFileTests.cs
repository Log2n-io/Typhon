using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// The clean-close side file (BKP-06): written only by a trusted session, bound to its close by a nonce the next open clears, and read back only when
/// the nonce, both checksums, the database, the timeline and the file's length all match.
/// </summary>
class BackupStateFileTests : BackupTestBase<BackupStateFileTests>
{
    private const string Marker = "BKP-06 trusted";

    [Test]
    public void RoundTrip_AndEveryKindOfDamage_IsRejected()
    {
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"bs-{Guid.NewGuid():N}");
        var contents = new BackupStateContents
        {
            Nonce = 42,
            DatabaseId = Guid.NewGuid(),
            TimelineId = Guid.NewGuid(),
            StoreId = Guid.NewGuid(),
            ChainId = Guid.NewGuid(),
            LastBackupId = 7,
            DataFilePages = 130,
            LastPointLsn = 12345,
            Map = [0x5, 0, 0x3],
        };
        try
        {
            BackupStateFile.Write(path, contents, flushToDisk: false);
            var read = BackupStateFile.TryRead(path, 42, out var ok);
            Assert.Multiple(() =>
            {
                Assert.That(ok, Is.EqualTo(BackupStateReadResult.Loaded));
                Assert.That(read.DatabaseId, Is.EqualTo(contents.DatabaseId));
                Assert.That(read.TimelineId, Is.EqualTo(contents.TimelineId));
                Assert.That(read.StoreId, Is.EqualTo(contents.StoreId));
                Assert.That(read.ChainId, Is.EqualTo(contents.ChainId));
                Assert.That(read.LastBackupId, Is.EqualTo(7));
                Assert.That(read.DataFilePages, Is.EqualTo(130));
                Assert.That(read.LastPointLsn, Is.EqualTo(12345));
                Assert.That(read.Map, Is.EqualTo(contents.Map));
                Assert.That(File.Exists(path + ".tmp"), Is.False, "the temporary file is renamed over the target");
            });

            BackupStateFile.TryRead(path, 43, out var wrongNonce);
            BackupStateFile.TryRead(path, 0, out var zeroNonce);
            var bytes = File.ReadAllBytes(path);
            BackupStateFile.Parse(Flip(bytes, 30), 42, out var headerDamage);
            BackupStateFile.Parse(Flip(bytes, 115), 42, out var bodyDamage);
            BackupStateFile.Parse(bytes.AsSpan(0, bytes.Length - 8), 42, out var truncated);
            BackupStateFile.TryRead(path + ".absent", 42, out var missing);
            Assert.Multiple(() =>
            {
                Assert.That(wrongNonce, Is.EqualTo(BackupStateReadResult.NonceMismatch));
                Assert.That(zeroNonce, Is.EqualTo(BackupStateReadResult.NonceMismatch), "a data file recording 0 vouches for no file");
                Assert.That(headerDamage, Is.EqualTo(BackupStateReadResult.Invalid));
                Assert.That(bodyDamage, Is.EqualTo(BackupStateReadResult.Invalid));
                Assert.That(truncated, Is.EqualTo(BackupStateReadResult.Invalid));
                Assert.That(missing, Is.EqualTo(BackupStateReadResult.Missing));
            });
        }
        finally
        {
            BackupStateFile.Delete(path);
        }
    }

    /// <summary>A trusted session's clean close writes the file and its nonce; the next open trusts it; that open's flip clears the nonce.</summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("BKP-06")]
    public void TrustedClose_WritesIt_TheNextOpenTrustsIt_AndClearsItsNonce()
    {
        var store = new FakeBackupStore();
        string path;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            Spawn(dbe, 50);
            Capture(dbe, store);
            path = BackupStateFile.PathIn(dbe.MMF.BundleDirectory);
        }

        Assert.That(File.Exists(path), Is.True, "a trusted session's clean close writes the backup-state");
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            AssertTrusted(dbe, true);
            Assert.That(DurabilityWatermarks.ReadBackupStateNonce(dbe.MMF), Is.Zero, "the open cleared the nonce: a crash from here leaves no trusted map");
        }
    }

    /// <summary>The verifier must reject an open that trusts a file the data file does not vouch for.</summary>
    [Test]
    [CancelAfter(15_000)]
    [RuleMutant("BKP-06")]
    public void Mutant_AFileTrustedAfterACrash_IsReported()
    {
        var store = new FakeBackupStore();
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            Capture(dbe, store);
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            Open(scope).SimulateHardCrash();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            dbe.BackupCoordinator.Lineage.Trusted = true;   // the violation: trust that survived a crash
            RuleMutants.AssertDetects("BKP-06", Marker, () => AssertTrusted(dbe, false));
        }
    }

    /// <summary>A file left by an earlier clean close (copied back over the current one) carries another nonce than the data file: not trusted.</summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("BKP-06")]
    public void AStaleFile_FromAnEarlierClose_IsNotTrusted()
    {
        var store = new FakeBackupStore();
        string path;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            Capture(dbe, store);
            path = BackupStateFile.PathIn(dbe.MMF.BundleDirectory);
        }

        var earlier = File.ReadAllBytes(path);
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            Spawn(dbe, 10);
            Capture(dbe, store);
        }

        File.WriteAllBytes(path, earlier);
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            Assert.That(dbe.LastOpenBackupStateResult, Is.EqualTo(BackupStateReadResult.NonceMismatch));
            AssertTrusted(dbe, false);
        }
    }

    /// <summary>A file whose nonce matches but whose database, timeline or length do not is not trusted.</summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("BKP-06")]
    public void AMatchingNonce_IsNotEnough(
        [Values(Mismatch.Database, Mismatch.Timeline, Mismatch.Pages)] Mismatch mismatch)
    {
        var store = new FakeBackupStore();
        string path;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            Capture(dbe, store);
            path = BackupStateFile.PathIn(dbe.MMF.BundleDirectory);
        }

        var nonce = BitConverter.ToUInt64(File.ReadAllBytes(path), 16);
        var c = BackupStateFile.TryRead(path, nonce, out _);
        BackupStateFile.Write(path, new BackupStateContents
        {
            Nonce = nonce,
            DatabaseId = mismatch == Mismatch.Database ? Guid.NewGuid() : c.DatabaseId,
            TimelineId = mismatch == Mismatch.Timeline ? Guid.NewGuid() : c.TimelineId,
            StoreId = c.StoreId,
            ChainId = c.ChainId,
            LastBackupId = c.LastBackupId,
            DataFilePages = mismatch == Mismatch.Pages ? c.DataFilePages + 64 : c.DataFilePages,
            LastPointLsn = c.LastPointLsn,
            Map = new ulong[((mismatch == Mismatch.Pages ? c.DataFilePages + 64 : c.DataFilePages) + 63) >> 6],
        }, flushToDisk: false);

        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            AssertTrusted(dbe, false);
            Assert.That(dbe.LastOpenBackupStateResult, Is.EqualTo(BackupStateReadResult.Invalid));
        }
    }

    /// <summary>C13: a session whose lineage is not trusted deletes any backup-state at its clean close and records nonce 0.</summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("BKP-06")]
    public void AnUntrustedSession_DeletesTheFile_AtItsCleanClose()
    {
        var store = new FakeBackupStore();
        string path;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            Capture(dbe, store);
            dbe.AdoptTimeline(Guid.NewGuid());   // a new timeline: the lineage stops being trusted
            path = BackupStateFile.PathIn(dbe.MMF.BundleDirectory);
        }

        Assert.That(File.Exists(path), Is.False, "an untrusted session must not leave a backup-state behind");
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            Assert.That(dbe.LastOpenBackupStateResult, Is.EqualTo(BackupStateReadResult.Missing));
            AssertTrusted(dbe, false);
        }
    }

    /// <summary>A backup-state that cannot be written costs the next backup a full point, never the close.</summary>
    [Test]
    [CancelAfter(15_000)]
    public void AWriteFailure_RecordsNonceZero_AndTheCloseSucceeds()
    {
        var store = new FakeBackupStore();
        string path;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            Capture(dbe, store);
            path = BackupStateFile.PathIn(dbe.MMF.BundleDirectory);
            Directory.CreateDirectory(path + ".tmp");   // the temporary file cannot be created
        }

        try
        {
            using var scope = ServiceProvider.CreateScope();
            var dbe = Open(scope);
            Assert.That(dbe.LastOpenBackupStateResult, Is.Not.EqualTo(BackupStateReadResult.Loaded));
            AssertTrusted(dbe, false);
        }
        finally
        {
            Directory.Delete(path + ".tmp");
        }
    }

    public enum Mismatch
    {
        Database,
        Timeline,
        Pages,
    }

    private static void AssertTrusted(DatabaseEngine dbe, bool expected)
        => Assert.That(dbe.BackupCoordinator.Lineage.Trusted, Is.EqualTo(expected),
            expected
                ? $"{Marker}: the open did not trust a valid backup-state"
                : $"{Marker}: the open trusted a backup-state that does not vouch for this file");

    private static byte[] Flip(byte[] bytes, int at)
    {
        var copy = (byte[])bytes.Clone();
        copy[at] ^= 0xFF;
        return copy;
    }
}
