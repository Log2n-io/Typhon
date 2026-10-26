using NUnit.Framework;
using SwgTatooine.Server;

namespace SwgTatooine.Tests;

/// <summary>
/// The base of every fixture that serves a world to fake clients: its directory, its harness, and the process-static counters a served world writes.
/// </summary>
/// <remarks>
/// <para>
/// <b>One lifecycle, written once.</b> Twelve fixtures carried the same set-up and teardown word for word, and three that did not carry it leaked a
/// database directory per case into <c>%TEMP%</c>. NUnit runs this <c>[SetUp]</c> before a derived one and this <c>[TearDown]</c> after, whether
/// the case passed or not, so the harness is unhooked and the directory removed on the failure path too.
/// </para>
/// <para>
/// <b>The counters are reset on both sides</b> because they are statics of <see cref="TatooineReplication"/>: a case that read one left over from
/// the previous fixture would be asserting on someone else's world.
/// </para>
/// <para>The tick rate is <see cref="Worlds.ServedHz"/>, through <see cref="Worlds.Served"/>: one policy rather than a literal per fixture.</para>
/// </remarks>
internal abstract class ServedChecks
{
    /// <summary>This case's database directory, made fresh for it and deleted after it.</summary>
    protected string Dir { get; private set; }

    /// <summary>The harness a case built, so teardown can unhook it from <c>SessionHarness.Until</c>'s keepalive.</summary>
    protected SessionHarness Harness { get; set; }

    [SetUp]
    public void ServedSetUp()
    {
        Dir = Worlds.NewDirectory();
        TatooineReplication.ResetSessionAccounting();
        TatooineReplication.ResetIntentAccounting();
    }

    [TearDown]
    public void ServedTearDown()
    {
        Harness?.Release();
        Harness = null;
        TatooineReplication.ResetSessionAccounting();
        TatooineReplication.ResetIntentAccounting();
        Worlds.Delete(Dir);
    }
}
