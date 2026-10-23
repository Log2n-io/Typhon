using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace SwgTatooine.Server;

/// <summary>
/// The server's TLS certificate, loaded from PEM files and reloaded when they change on disk.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reloaded rather than read once, because the certificate expires every ninety days.</b> Let's Encrypt renews on a timer,
/// and the alternative to noticing is a <c>--deploy-hook</c> that restarts the service — which kicks every connected session,
/// on a schedule, forever. The engine shuts down cleanly so that would be survivable, but a load soak or a demo should not have
/// a scheduled interruption in it when the fix is to re-read a file.
/// </para>
/// <para>
/// <b>Keyed on the write time and length of both files, not a file watcher.</b> A watcher is a second thing to get wrong on a
/// path that runs per connection; a stat of two files is microseconds, and certbot replaces them through a symlink swap in
/// <c>live/</c>, which changes both. The check is throttled anyway: once a minute is far more often than a ninety-day renewal
/// needs, and it means a connection storm does not become a stat storm.
/// </para>
/// <para>
/// <b>A failed reload keeps serving the old certificate.</b> Certbot writes the two files separately, so there is a window where
/// the chain and the key do not match each other; throwing there would refuse connections for a certificate that is about to be
/// perfectly valid. The old one stays until a load succeeds, which is also the right behaviour for a key file briefly
/// unreadable during a permissions fix.
/// </para>
/// </remarks>
internal sealed class TlsCertificate
{
    /// <summary>How long between two checks of the files on disk.</summary>
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(1);

    private readonly string _chainPath;
    private readonly string _keyPath;
    private readonly TimeSpan _checkEvery;
    private readonly object _gate = new();

    private X509Certificate2 _current;
    private string _loadedFrom;
    private long _nextCheckTicks;

    /// <summary>Loads the certificate for the first time, and fails loudly if it cannot.</summary>
    /// <param name="chainPath">The PEM chain, certbot's <c>fullchain.pem</c>.</param>
    /// <param name="keyPath">The PEM private key, certbot's <c>privkey.pem</c>.</param>
    /// <exception cref="InvalidOperationException">Either file is missing or does not load as a pair.</exception>
    public TlsCertificate(string chainPath, string keyPath)
        : this(chainPath, keyPath, CheckEvery)
    {
    }

    /// <summary>
    /// The same, with the re-check interval given.
    /// </summary>
    /// <param name="chainPath">The PEM chain.</param>
    /// <param name="keyPath">The PEM private key.</param>
    /// <param name="checkEvery">How long between two stats of the files.</param>
    /// <remarks>
    /// <b>A seam for the tests, and the only one.</b> The reload is the part of this class that matters and the part that
    /// cannot be observed in production for ninety days — so a test has to be able to make the deadline pass without waiting a
    /// minute for it. Nothing else about the behaviour is parameterised.
    /// </remarks>
    internal TlsCertificate(string chainPath, string keyPath, TimeSpan checkEvery)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chainPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);

        _chainPath = chainPath;
        _keyPath = keyPath;
        _checkEvery = checkEvery;

        // At construction it IS fatal: the caller asked for HTTPS, and binding 443 with nothing to present would accept
        // connections and then fail every handshake -- an outage that looks like a network fault.
        _current = Load(chainPath, keyPath)
            ?? throw new InvalidOperationException(
                $"could not load a certificate from {chainPath} and {keyPath}. Run certbot first, or start without --tls to serve plain HTTP.");

        _loadedFrom = Stamp(chainPath, keyPath);
        _nextCheckTicks = DateTime.UtcNow.Add(checkEvery).Ticks;
    }

    /// <summary>The subject and expiry, for the startup banner.</summary>
    public string Describe()
    {
        lock (_gate)
        {
            return $"{_current.Subject} until {_current.NotAfter:yyyy-MM-dd}";
        }
    }

    /// <summary>
    /// The certificate to present, reloading it first if the files have changed.
    /// </summary>
    /// <returns>The current certificate.</returns>
    /// <remarks>Kestrel calls this per connection through <c>ServerCertificateSelector</c>, so it must stay cheap.</remarks>
    public X509Certificate2 Current()
    {
        var now = DateTime.UtcNow.Ticks;
        if (now < Volatile.Read(ref _nextCheckTicks))
        {
            lock (_gate)
            {
                return _current;
            }
        }

        lock (_gate)
        {
            // Re-read inside the lock: several connections can arrive past the deadline at once, and only one should stat.
            if (now < _nextCheckTicks)
            {
                return _current;
            }

            _nextCheckTicks = DateTime.UtcNow.Add(_checkEvery).Ticks;

            var stamp = Stamp(_chainPath, _keyPath);
            if (string.Equals(stamp, _loadedFrom, StringComparison.Ordinal))
            {
                return _current;
            }

            var replacement = Load(_chainPath, _keyPath);
            if (replacement == null)
            {
                // Mid-renewal, or a transient permission problem. Keep serving; the next check will pick it up.
                return _current;
            }

            var previous = _current;
            _current = replacement;
            _loadedFrom = stamp;
            Console.WriteLine($"  tls: reloaded {replacement.Subject} until {replacement.NotAfter:yyyy-MM-dd}");

            // After the swap, so no handshake in flight is holding the one being released.
            previous.Dispose();
            return _current;
        }
    }

    /// <summary>Reads the pair, or answers <see langword="null"/> when they are not currently a usable pair.</summary>
    /// <param name="chainPath">The PEM chain.</param>
    /// <param name="keyPath">The PEM key.</param>
    /// <returns>The certificate, or <see langword="null"/>.</returns>
    private static X509Certificate2 Load(string chainPath, string keyPath)
    {
        try
        {
            if (!File.Exists(chainPath) || !File.Exists(keyPath))
            {
                return null;
            }

            var pair = X509Certificate2.CreateFromPemFile(chainPath, keyPath);

            // Windows needs the key persisted into a PFX round-trip before SChannel will use it for a server handshake; on
            // Linux, where this actually deploys, the PEM pair is usable as-is. Doing it unconditionally keeps one code path
            // and costs a few milliseconds once per renewal.
            return X509CertificateLoader.LoadPkcs12(pair.Export(X509ContentType.Pkcs12), null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException
            or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>A cheap fingerprint of both files, so a change is noticed without reading them.</summary>
    /// <param name="chainPath">The PEM chain.</param>
    /// <param name="keyPath">The PEM key.</param>
    /// <returns>The fingerprint.</returns>
    private static string Stamp(string chainPath, string keyPath)
    {
        try
        {
            var chain = new FileInfo(chainPath);
            var key = new FileInfo(keyPath);
            return $"{chain.LastWriteTimeUtc.Ticks}:{chain.Length}:{key.LastWriteTimeUtc.Ticks}:{key.Length}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An unreadable file reads as "unchanged", which keeps the current certificate rather than dropping TLS.
            return null;
        }
    }
}
