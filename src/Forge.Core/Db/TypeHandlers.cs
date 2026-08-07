using System.Data;
using System.Text.Json;
using Dapper;
using Forge.Core.Model;

namespace Forge.Core.Db;

// NOTE: enums are converted explicitly in repositories via SnakeCaseEnum, not via
// Dapper type handlers — Dapper binds enum parameters as their numeric value and
// never consults AddTypeHandler for them (verified by test; long-standing Dapper
// limitation). The CHECK constraints would reject the numbers, so explicit
// conversion at the repository boundary is the reliable path.

/// <summary>JSON list ⇄ TEXT (context_paths is JSON by design).</summary>
/// <summary>Stores a string list as a JSON array in a TEXT column, and reads it back.</summary>
public sealed class StringListHandler : SqlMapper.TypeHandler<IReadOnlyList<string>>
{
    /// <summary>Reads the stored JSON array; anything unparseable reads as empty.</summary>
    public override IReadOnlyList<string> Parse(object value) =>
        value is string s && !string.IsNullOrWhiteSpace(s)
            ? JsonSerializer.Deserialize<List<string>>(s) ?? []
            : [];

    /// <summary>Writes the list as JSON, or null when it is empty.</summary>
    public override void SetValue(IDbDataParameter parameter, IReadOnlyList<string>? value)
    {
        parameter.DbType = DbType.String;
        parameter.Value = value is null ? DBNull.Value : JsonSerializer.Serialize(value);
    }
}

/// <summary>"02-todos-read.md@v3" ⇄ RequirementsRef; parse-don't-validate at the DB boundary.</summary>
/// <summary>Stores a requirement ref as its `file.md@vN` text, and parses it back.</summary>
public sealed class RequirementsRefHandler : SqlMapper.TypeHandler<RequirementsRef?>
{
    /// <summary>Parses the stored text; throws if it is malformed.</summary>
    public override RequirementsRef? Parse(object value) =>
        value is string s ? RequirementsRef.Parse(s) : null;

    /// <summary>Writes the ref as text, or null when there is none.</summary>
    public override void SetValue(IDbDataParameter parameter, RequirementsRef? value)
    {
        parameter.DbType = DbType.String;
        parameter.Value = value?.ToString() ?? (object)DBNull.Value;
    }
}

/// <summary>Registers Forge's Dapper type handlers once per process.</summary>
public static class TypeHandlerRegistry
{
    private static bool _registered;
    private static readonly object Gate = new();

    /// <summary>Registers the handlers if they have not been registered yet.</summary>
    public static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (_registered) return;
            SqlMapper.AddTypeHandler(new StringListHandler());
            SqlMapper.AddTypeHandler(new RequirementsRefHandler());
            _registered = true;
        }
    }
}
