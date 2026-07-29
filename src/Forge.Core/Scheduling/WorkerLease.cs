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
    private WorkerStatus _status;
    private bool _released;

    private WorkerLease(string path, WorkerStatus status, Func<DateTimeOffset> clock)
    {
        _path = path;
        _status = status;
        _clock = clock;
        Write();
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
    /// Take the lease, or return null if someone else holds it. Checked-then-written
    /// rather than an OS lock: the holder may be a different process on a different
    /// terminal, and the heartbeat is what makes a stale claim recoverable.
    /// </summary>
    public static WorkerLease? TryAcquire(ForgePaths paths, string project, Func<DateTimeOffset>? clock = null)
    {
        clock ??= () => DateTimeOffset.UtcNow;
        var path = PathFor(paths);
        var now = clock();

        if (Read(path) is { } held && held.IsLive(now)) return null;

        return new WorkerLease(path,
            new WorkerStatus(project, Environment.ProcessId, now, now), clock);
    }

    /// <summary>Called as the worker loops; silence is what frees the lease.</summary>
    public void Beat()
    {
        if (_released) return;
        _status = _status with { HeartbeatAt = _clock() };
        Write();
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
        if (_released) return;
        _released = true;
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
