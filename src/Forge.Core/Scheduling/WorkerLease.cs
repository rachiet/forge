using System.Text.Json;

namespace Forge.Core.Scheduling;

/// <summary>The contents of the lease file: who holds it, and when they last checked in.</summary>
public sealed record WorkerStatus(string Project, int Pid, DateTimeOffset StartedAt, DateTimeOffset HeartbeatAt)
{
    /// <summary>How long without a heartbeat before a worker is presumed dead.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    public bool IsLive(DateTimeOffset now) => now - HeartbeatAt <= Timeout;
}

/// <summary>
/// A machine-wide lease that allows one build at a time. Both a terminal `forge run` and the
/// board's Start button take it, so they cannot collide on one database. It is a file rather
/// than a table because the lock is machine-scoped and ephemeral while the databases are
/// per-project; a holder is judged live by the heartbeat it writes, so a crashed worker frees
/// the lease by falling silent.
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

        // The lease beats itself on a timer, so it stays live through a task that runs far
        // longer than the stale timeout.
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
    /// Takes the lease, or returns null if a live worker holds it. The claim is atomic —
    /// create-or-fail at the OS level — so two processes racing cannot both win. A stale file
    /// from a dead holder is deleted and the claim retried once.
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

    /// <summary>Writes a fresh heartbeat. The internal timer calls this on its own.</summary>
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
