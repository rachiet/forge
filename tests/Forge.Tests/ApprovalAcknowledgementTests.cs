using Forge.Core;
using Forge.Core.Board;
using Forge.Core.Configuration;
using Forge.Core.Db;
using Forge.Core.Model;
using Microsoft.Data.Sqlite;
using TaskStatus = Forge.Core.Model.TaskStatus;

namespace Forge.Tests;

/// <summary>Approving the requirements opens the Feature and answers the client.</summary>
public class ApprovalAcknowledgementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"forge-approve-{Guid.NewGuid():N}");
    private readonly ForgePaths _paths;

    public ApprovalAcknowledgementTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new ForgePaths(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* the temp dir is disposable */ }
        GC.SuppressFinalize(this);
    }

    private SqliteConnection Open()
    {
        var dbPath = _paths.ProjectDb("alpha");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        return Database.OpenProject(dbPath);
    }

    [Fact]
    public void Approving_opens_a_triage_feature_owned_by_the_principal()
    {
        using var conn = Open();
        new RequirementsProposal("Build it", "an objective").Save(conn);

        var feature = RequirementsProposal.Load(conn)!.Approve(conn);

        Assert.Equal(TaskType.Feature, feature.Type);
        Assert.Equal(TaskStatus.Triage, feature.Status);
        Assert.Equal(AgentRole.Principal, feature.AssignedRole);
        Assert.Null(RequirementsProposal.Load(conn));
    }

    [Fact]
    public void The_acknowledgement_reaches_the_client_and_not_just_the_log()
    {
        // The board's chat feed only renders client↔pm traffic, so an acknowledgement
        // addressed anywhere else would be invisible at the moment of commitment.
        using var conn = Open();
        var messages = new MessageRepository(conn);

        messages.Insert(Message.Create(
            MessageType.Status, "pm", "client", ClientFacing.ApprovalAcknowledgement, null));

        var line = Assert.Single(new BoardQuery(conn, "alpha").Snapshot().Chat);
        Assert.Equal("pm", line.From);
        Assert.Contains("starting work now", line.Text);
        Assert.Contains("change request", line.Text);
    }
}
