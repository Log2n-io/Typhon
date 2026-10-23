using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace SwgTatooine.Bots;

/// <summary>
/// The accounts file: one JSON object per line, indexed by bot index, rewritten atomically.
/// </summary>
/// <remarks>
/// <para>
/// <b>JSONL and not one JSON document, because the file is append-shaped by nature</b> — a population grows, and a thousand lines
/// that can each be read, grepped and diffed on their own are worth more operationally than one array that has to be parsed whole
/// to be understood. A corrupt line costs one bot rather than the fleet: <see cref="Load"/> skips what it cannot parse and says how
/// many it skipped, because silently signing up a replacement for a line that was merely truncated would be worse than either
/// failing or reporting.
/// </para>
/// <para>
/// <b>Writes are batched and the whole file is rewritten.</b> A thousand bots persisting a resume token each would otherwise be a
/// thousand file writes on a path the run does not care about; <see cref="FlushIfDue"/> collapses them. The rewrite goes to a
/// temporary file and is moved over the target, so a kill in the middle leaves the previous generation intact rather than a
/// half-written file — which for this file means "the fleet logs in again", not "the fleet is lost", but the same reasoning that
/// makes it cheap makes it right.
/// </para>
/// <para>
/// <b>One lock, and it is held for every field.</b> This started out documented as "not thread-safe by design" while reaching for
/// <c>Interlocked</c> and <c>Volatile</c> on the counters, which is the worst of both: the atomics bought nothing a single-threaded
/// contract needed, and a caller who believed them would still have been publishing record references with no release -- visible
/// on arm64, which is what the bot box runs. A lock is correct, it is the same correctness under B7's continuous connect and
/// disconnect, and it costs nothing here: this is one file write per sixty-four resume tokens, not a hot path.
/// </para>
/// </remarks>
public sealed class BotAccountStore
{
    /// <summary>How many pending changes trigger a write, when the caller does not say.</summary>
    public const int DefaultFlushThreshold = 64;

    private readonly object _gate = new();
    private readonly string _path;
    private readonly BotAccountRecord[] _records;
    private readonly int _flushThreshold;
    private int _pending;
    private int _flushes;
    private int _skippedLines;

    /// <summary>Opens or creates a store for a population.</summary>
    /// <param name="path">The accounts file. Created on the first flush when it does not exist.</param>
    /// <param name="count">How many bots the population has. The file may hold more; extra lines are kept.</param>
    /// <param name="flushThreshold">Pending changes that trigger a write; <c>0</c> scales one from the population.</param>
    public BotAccountStore(string path, int count, int flushThreshold = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ArgumentOutOfRangeException.ThrowIfNegative(flushThreshold);

        _path = Path.GetFullPath(path);
        _records = new BotAccountRecord[count];

        // SCALED WITH THE POPULATION, because a flush rewrites the whole file, so a fixed threshold is quadratic in bytes. At the
        // thousand bots this was written for, sixty-four means sixteen rewrites of a ~150 KB file and nobody notices; at 07-bots
        // § 6's five-to-ten-thousand stretch target it is about 157 rewrites of a 1.5 MB file — a quarter of a gigabyte written
        // during a connect ramp, on the one path the run is trying to measure. A sixteenth of the population holds it at sixteen
        // rewrites whatever the size, and the floor keeps small runs flushing often enough to be worth anything.
        _flushThreshold = flushThreshold > 0 ? flushThreshold : Math.Clamp(count / 16, DefaultFlushThreshold, 1024);
        Load();
    }

    /// <summary>Where the file lives, for the run banner.</summary>
    public string FilePath => _path;

    /// <summary>How many lines could not be used and were skipped, for whatever reason.</summary>
    public int SkippedLines
    {
        get
        {
            lock (_gate)
            {
                return _skippedLines;
            }
        }
    }

    /// <summary>How many times the file has been rewritten.</summary>
    public int Flushes
    {
        get
        {
            lock (_gate)
            {
                return _flushes;
            }
        }
    }

    /// <summary>How many changes are waiting to be written.</summary>
    public int Pending
    {
        get
        {
            lock (_gate)
            {
                return _pending;
            }
        }
    }

    /// <summary>The record for a bot index, or <see langword="null"/> when it has never been provisioned.</summary>
    /// <param name="index">The bot index.</param>
    /// <returns>The record, or <see langword="null"/>.</returns>
    public BotAccountRecord this[int index]
    {
        get
        {
            lock (_gate)
            {
                return index >= 0 && index < _records.Length ? _records[index] : null;
            }
        }
    }

    /// <summary>How many records hold a token that has not expired yet.</summary>
    /// <param name="skewSeconds">Treat a token expiring within this many seconds as already gone.</param>
    /// <returns>The count.</returns>
    public int UsableTokens(int skewSeconds)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var usable = 0;
        lock (_gate)
        {
            foreach (var record in _records)
            {
                if (record != null && !string.IsNullOrEmpty(record.Token) && record.TokenExpUnix - now > skewSeconds)
                {
                    usable++;
                }
            }
        }

        return usable;
    }

    /// <summary>Replaces a bot's record and counts the change as pending.</summary>
    /// <param name="record">The new record. Its <see cref="BotAccountRecord.Index"/> places it.</param>
    public void Put(BotAccountRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Index < 0 || record.Index >= _records.Length)
        {
            return;
        }

        lock (_gate)
        {
            _records[record.Index] = record;
            _pending++;
        }
    }

    /// <summary>Records the resume token a session was issued, so a restarted fleet can present it.</summary>
    /// <param name="index">The bot index.</param>
    /// <param name="resume">The raw token. <see langword="null"/> or empty leaves the stored one alone; use <see cref="ForgetResume"/> to clear it.</param>
    /// <remarks>
    /// <para>
    /// Keeping this beside the token rather than in a second file is what makes a restart one read: the two things a bot needs to
    /// come back are its login and its continuity, and splitting them would let one survive without the other.
    /// </para>
    /// <para>
    /// <b>An absent token means "nothing new to record", NOT "forget what you had".</b> The client assigns its resume token from
    /// every <c>WELCOME</c> unconditionally, so a server with resume disabled -- <c>--resume-grace 0</c> -- hands back none and
    /// the client's token becomes null. Writing that through would silently erase every stored token the moment a fleet was
    /// pointed at such a server, turning the next restart into a thousand cold logins for a reason nothing reported. Clearing is
    /// therefore something a caller has to ask for by name.
    /// </para>
    /// </remarks>
    public void PutResume(int index, byte[] resume)
    {
        if (resume == null || resume.Length == 0)
        {
            return;
        }

        Store(index, Convert.ToBase64String(resume));
    }

    /// <summary>Forgets a bot's resume token, for a caller that means it.</summary>
    /// <param name="index">The bot index.</param>
    /// <remarks>
    /// Separate from <see cref="PutResume"/> so that losing continuity is always an explicit act. Nothing in the fleet calls this
    /// today; it exists so that the absent-token case above has an answer other than overloading <see cref="PutResume"/>.
    /// </remarks>
    public void ForgetResume(int index) => Store(index, null);

    /// <summary>Writes a bot's resume field, if it has a record and the value actually changed.</summary>
    /// <param name="index">The bot index.</param>
    /// <param name="encoded">The base64 token, or <see langword="null"/>.</param>
    private void Store(int index, string encoded)
    {
        lock (_gate)
        {
            var existing = index >= 0 && index < _records.Length ? _records[index] : null;
            if (existing == null)
            {
                return;
            }

            if (string.Equals(existing.Resume, encoded, StringComparison.Ordinal))
            {
                return;   // nothing changed, so nothing is owed to the disk
            }

            _records[index] = existing with { Resume = encoded };
            _pending++;
        }
    }

    /// <summary>The resume token stored for a bot, or <see langword="null"/>.</summary>
    /// <param name="index">The bot index.</param>
    /// <returns>The raw token, or <see langword="null"/> when there is none or it is unreadable.</returns>
    public byte[] Resume(int index)
    {
        var record = this[index];
        if (record == null || string.IsNullOrEmpty(record.Resume))
        {
            return null;
        }

        // A malformed resume token is not worth failing a run over: the server answers an unknown token with an ordinary new
        // session, so the worst a bad line costs is one cold login.
        //
        // Sized from the ENCODED length rather than from the sixteen bytes the engine mints today. TryFromBase64String answers
        // false for a buffer that is too small, so a stack span fixed at 32 would silently turn every longer token into a cold
        // login the day the token grew -- a decode path that fails closed on a value it could have read perfectly well.
        var decoded = new byte[(record.Resume.Length / 4 * 3) + 3];
        return Convert.TryFromBase64String(record.Resume, decoded, out var written) ? decoded[..written] : null;
    }

    /// <summary>Writes the file when enough changes have piled up.</summary>
    /// <returns><see langword="true"/> when a write happened.</returns>
    public bool FlushIfDue()
    {
        lock (_gate)
        {
            if (_pending < _flushThreshold)
            {
                return false;
            }

            FlushLocked();
            return true;
        }
    }

    /// <summary>
    /// Rewrites the file, atomically.
    /// </summary>
    /// <remarks>
    /// Temp-then-move, and the temp file sits beside the target so the move is within one filesystem and therefore a rename rather
    /// than a copy — a cross-device move is not atomic and would reintroduce the window this is here to close.
    /// </remarks>
    public void Flush()
    {
        lock (_gate)
        {
            FlushLocked();
        }
    }

    /// <summary>Writes the file. The caller holds <see cref="_gate"/>.</summary>
    private void FlushLocked()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = _path + ".tmp";

        // Serialised STRAIGHT to the stream, one record at a time. Building a StringBuilder, calling ToString() and then encoding
        // that was three full copies of the file per flush, the middle one a ~160 KB large-object allocation at a thousand bots —
        // on a path that runs during the very ramp this program exists to measure.
        //
        // UTF8Encoding(false), because Encoding.UTF8 writes a BOM and this file is meant to be read by other things: a leading
        // U+FEFF makes `jq` reject the first line and `head -1 | python -m json.tool` fail, which for an operational file that
        // exists to be grepped is a worse bug than it looks.
        //
        // Flushed THROUGH to the device before the move, which is what makes the word "atomic" above true for more than a kill:
        // a write that returns once the bytes are in the page cache can have the rename ordered before the data on a crash,
        // leaving a zero-length accounts file. That costs a round of logins rather than the accounts, which is why it is cheap
        // insurance rather than a correctness emergency — but the file is small and this is once per flush.
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using (var writer = new StreamWriter(file, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true))
            {
                foreach (var record in _records)
                {
                    if (record != null)
                    {
                        writer.Write(JsonSerializer.Serialize(record, BotAccountJson.Default.BotAccountRecord));
                        writer.Write('\n');
                    }
                }
            }

            file.Flush(flushToDisk: true);
        }

        RestrictToOwner(temporary);
        File.Move(temporary, _path, overwrite: true);

        _pending = 0;
        _flushes++;
    }

    /// <summary>Reads what is already on disk.</summary>
    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        foreach (var line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            BotAccountRecord record = null;
            try
            {
                record = JsonSerializer.Deserialize(line, BotAccountJson.Default.BotAccountRecord);
            }
            catch (JsonException)
            {
                // Counted, not thrown: see the class remarks. A truncated last line is the ordinary outcome of a kill.
            }

            if (record == null || record.Index < 0)
            {
                // Unparseable, or parseable with an index that cannot name a bot. Both are corruption and both are counted: a
                // negative index used to fall in with the next case and vanish, which made the count a partial truth.
                _skippedLines++;
                continue;
            }

            if (record.Index >= _records.Length)
            {
                // Past the population. NOT counted and not an error: the file may legitimately describe a bigger fleet than this
                // run asked for, and a thousand-bot file read by a hundred-bot run is the ordinary case.
                continue;
            }

            if (_records[record.Index] != null)
            {
                // A duplicate index, which is the one case where the file is PROVABLY losing a record -- two lines claim one bot
                // and the second wins. Counted, because it is the shape a half-written flush leaves behind and the only signal
                // that it happened.
                _skippedLines++;
            }

            _records[record.Index] = record;
        }
    }

    /// <summary>
    /// Takes the group and world bits off the file where the platform has them.
    /// </summary>
    /// <param name="path">The file to restrict.</param>
    /// <remarks>
    /// The file holds tokens and resume tokens, both bearer secrets. On Windows the inherited ACL is already the right answer and
    /// there is no mode to set; on Linux, which is where the bot box runs, the default umask leaves it group- and world-readable.
    /// </remarks>
    private static void RestrictToOwner(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Best effort. A file that could not be restricted is still better than no accounts file, and the alternative — failing
            // the run — would make a fleet depend on a filesystem's permission model.
        }
    }
}
