namespace Forge.Core.Logging;

/// <summary>
/// The default sink: appends entries to a file, one line each. Flushed per write
/// so a crashed run still leaves a complete log up to the last event — the log is
/// most valuable exactly when something died mid-task.
/// </summary>
public sealed class FileLogSink : ILogSink
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;

    /// <summary>The file being written to.</summary>
    public string Path { get; }

    /// <summary>Opens the log file for appending, creating its directory if needed.</summary>
    public FileLogSink(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        // FileShare.ReadWrite, because two writers on one project log is now a real
        // shape: the board's PM chat turn and its worker run in one process but hold
        // separate sinks, and a terminal `forge log` may read alongside. The default
        // (FileShare.Read) made the second open THROW on Windows.
        _writer = new StreamWriter(new FileStream(
            path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }

    /// <summary>Appends one entry and flushes it.</summary>
    public void Write(LogEntry entry)
    {
        // Serialize concurrent writers: a project could have more than one worker later.
        lock (_gate) _writer.WriteLine(entry.Serialize());
    }

    /// <summary>Closes the file.</summary>
    public void Dispose() => _writer.Dispose();
}

/// <summary>Prints entries as they happen — handy for watching a run live.</summary>
public sealed class ConsoleLogSink(TextWriter? writer = null) : ILogSink
{
    private readonly TextWriter _out = writer ?? Console.Out;

    /// <summary>Prints one entry.</summary>
    public void Write(LogEntry entry) => _out.WriteLine(entry.Display());

    public void Dispose() { }
}

/// <summary>
/// Fans one entry out to several sinks — the "push to any service we want" case:
/// keep the file, and also ship to a console or a remote sink, by composing them.
/// One misbehaving sink never stops the others.
/// </summary>
public sealed class CompositeLogSink(params ILogSink[] sinks) : ILogSink
{
    /// <summary>Appends one entry and flushes it.</summary>
    public void Write(LogEntry entry)
    {
        foreach (var sink in sinks)
        {
            try { sink.Write(entry); }
            catch { /* a broken sink must not take down the run or the other sinks */ }
        }
    }

    public void Dispose()
    {
        foreach (var sink in sinks) sink.Dispose();
    }
}

/// <summary>Discards everything. The default when no sink is wired.</summary>
public sealed class NullLogSink : ILogSink
{
    /// <summary>The shared instance; it holds no state.</summary>
    public static readonly NullLogSink Instance = new();

    public void Write(LogEntry entry) { }

    public void Dispose() { }
}
