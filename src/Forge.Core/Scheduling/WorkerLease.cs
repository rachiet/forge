using System.Text.Json;

namespace Forge.Core.Scheduling;

/// <summary>Who is building right now, and how long ago they said so.</summary>
public sealed record WorkerStatus(string Project, int Pid, DateTimeOffset StartedAt, DateTimeOffset HeartbeatAt)
{
    /// <summary>
    /// A worker that has not checked in for this long is presumed dead. Comfortably
    /// longer than a heartbeat interval, because a single agent turn can be slow and
    /// a live worker must never be mistaken for a corpse.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    public bool IsLive(DateTimeOffset now) => now - HeartbeatAt <= Timeout;
}

/// <summary>
/// A machine-wide "one build at a time" lease.
///
/// Forge has no concurrency guard of its own: two workers on one project corrupt the
/// shared database and log, and the decision here is that only one project builds at
/// a time anyway. The lease makes that mechanical rather than remembered — and
/// because a terminal `forge run` takes it too, a Start button in the browser cannot
/// collide with a run someone left going in a shell.
///
/// A file rather than a table: the lock is machine-scoped and ephemeral, while the
/// databases are per-project and durable. Staleness is judged by a heartbeat the
/// holder writes, not by file mtime, so a crashed worker frees the lease by falling
/// silent rather than needing cleanup.
/// </summary>
public sealed class WorkerLease : IDisposable
{
    private readonly string _path;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Timer? _autoBeat;
    private readonly object _gate = new();
    private WorkerStatus _status;
    private bool _released;

    private WorkerLease(string path, WorkerStatus status, Func<DateTimeOffset> clock, TimeSpan? beatEvery)
    {
        _path = path;
        _status = status;
        _clock = clock;
        Write();

        // The lease beats ITSELF while held. Leaving Beat() to the worker's loop was
        // the original design and it was wrong: a single task run is many LLM calls and
        // routinely outlasts the 90s timeout, at which point the lease read as stale
        // mid-task and a second build could start — the exact collision this type
        // exists to prevent. A timer is indifferent to how long one task takes.
        var interval = beatEvery ?? TimeSpan.FromTicks(WorkerStatus.Timeout.Ticks / 3);
        _autoBeat = new Timer(_ => Beat(), null, interval, interval);
    }

    public static string PathFor(ForgePaths paths) => Path.Combine(paths.DataRoot, "worker.json");

    /// <summary>The live holder, or null when nothing is building.</summary>
    public static WorkerStatus? Current(ForgePaths paths, Func<DateTimeOffset>? clock = null)
    {
        var now = (clock ?? (() => DateTimeOffset.UtcNow))();
        var status = Read(PathFor(paths));
        return status is not null && status.IsLive(now) ? status : null;
    }

    /// <summary>
    /// Take the lease, or return null if someone else holds it. The claim itself is
    /// atomic — `FileMode.CreateNew` is create-or-fail at the OS level — so two
    /// processes racing here cannot both win, which a read-then-write check allowed:
    /// both would read "no live lease" and both would proceed. A stale file (dead
    /// holder) is deleted and the claim retried; if a rival slips into that gap and
    /// creates first, our retry fails and we correctly lose.
    /// </summary>
    public static WorkerLease? TryAcquire(
        ForgePaths paths, string project, Func<DateTimeOffset>? clock = null, TimeSpan? beatEvery = null)
    {
        clock ??= () => DateTimeOffset.UtcNow;
        var path = PathFor(paths);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var now = clock();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var status = new WorkerStatus(project, Environment.ProcessId, now, now);
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                using (var writer = new StreamWriter(stream))
                {
                    writer.Write(JsonSerializer.Serialize(status));
                }
                return new WorkerLease(path, status, clock, beatEvery);
            }
            catch (IOException)
            {
                // The file exists: someone holds (or held) the lease.
                if (Read(path) is { } held && held.IsLive(now)) return null;
                try { File.Delete(path); }             // dead holder — clear and retry once
                catch (IOException) { return null; }   // rival cleaning up too; let them win
            }
        }
        return null;
    }

    /// <summary>Refresh the heartbeat; silence is what frees the lease. The internal
    /// timer calls this on its own — the manual call is belt-and-braces per loop tick.</summary>
    public void Beat()
    {
        lock (_gate)
        {
            if (_released) return;
            _status = _status with { HeartbeatAt = _clock() };
            Write();
        }
    }

    private void Write()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = $"{_path}.{Environment.ProcessId}.tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_status));
            File.Move(temp, _path, overwrite: true);
        }
        catch (IOException)
        {
            // A heartbeat we could not write is a lease that expires early — recoverable,
            // and far better than taking down a running build over a transient file error.
        }
    }

    private static WorkerStatus? Read(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<WorkerStatus>(File.ReadAllText(path))
                : null;
        }
        catch
        {
            return null;   // an unreadable lease is no lease
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_released) return;
            _released = true;
        }
        _autoBeat?.Dispose();
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch (IOException)
        {
            // Left behind, it expires on its own after the heartbeat timeout.
        }
    }
}
