using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests;

[TestFixture]
public sealed class SessionManagerTests
{
    private SessionManager _manager;

    [SetUp]
    public void SetUp() => _manager = new SessionManager(NullLogger<SessionManager>.Instance);

    [Test]
    public void Create_AddsSession_TryGetReturnsIt()
    {
        var session = NewSession();
        _manager.Create(session);

        Assert.That(_manager.TryGet(session.Id, out var found), Is.True);
        Assert.That(found, Is.SameAs(session));
    }

    [Test]
    public void TryGet_UnknownId_ReturnsFalse()
    {
        Assert.That(_manager.TryGet(Guid.NewGuid(), out _), Is.False);
    }

    [Test]
    public void Remove_ExistingSession_ReturnsTrueAndEvicts()
    {
        var session = NewSession();
        _manager.Create(session);

        Assert.That(_manager.Remove(session.Id), Is.True);
        Assert.That(_manager.TryGet(session.Id, out _), Is.False);
    }

    [Test]
    public void Remove_UnknownId_ReturnsFalse()
    {
        Assert.That(_manager.Remove(Guid.NewGuid()), Is.False);
    }

    [Test]
    public void Count_ReflectsActiveSessionCount()
    {
        Assert.That(_manager.Count, Is.EqualTo(0));
        var s1 = _manager.Create(NewSession());
        _manager.Create(NewSession());
        Assert.That(_manager.Count, Is.EqualTo(2));
        _manager.Remove(s1.Id);
        Assert.That(_manager.Count, Is.EqualTo(1));
    }

    [Test]
    public void ConcurrentAccess_DoesNotThrow()
    {
        var tasks = Enumerable.Range(0, 100).Select(i => Task.Run(() =>
        {
            var s = NewSession();
            _manager.Create(s);
            _manager.TryGet(s.Id, out var ignored);
            _manager.Remove(s.Id);
            return ignored;
        })).ToArray();

        Assert.DoesNotThrow(() => Task.WaitAll(tasks));
    }

    private static FakeSession NewSession() => new(Guid.NewGuid());

    // Minimal ISession fake — the real Trace/Attach/Open sessions own runtime resources we don't want
    // this fixture to pay for. SessionManager only cares about Id/Kind/State/FilePath.
    private sealed record FakeSession(Guid Id) : ISession
    {
        public SessionKind Kind => SessionKind.Attach;
        public SessionState State => SessionState.Attached;
        public string FilePath => string.Empty;
        public Typhon.Workbench.Schema.IStaticSchemaProvider StaticSchemaProvider => null;
    }
}
