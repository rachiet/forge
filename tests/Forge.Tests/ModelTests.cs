using Forge.Core.Model;
using TaskStatus = Forge.Core.Model.TaskStatus;

namespace Forge.Tests;

public class TaskTransitionsTests
{
    [Theory]
    [InlineData(TaskStatus.Created, TaskStatus.Ready)]
    [InlineData(TaskStatus.Ready, TaskStatus.Claimed)]
    [InlineData(TaskStatus.Claimed, TaskStatus.InProgress)]
    [InlineData(TaskStatus.InProgress, TaskStatus.InReview)]
    [InlineData(TaskStatus.InReview, TaskStatus.Merging)]
    [InlineData(TaskStatus.InReview, TaskStatus.InProgress)]
    [InlineData(TaskStatus.Merging, TaskStatus.Qa)]
    [InlineData(TaskStatus.Qa, TaskStatus.Done)]
    [InlineData(TaskStatus.InProgress, TaskStatus.Blocked)]
    [InlineData(TaskStatus.Blocked, TaskStatus.Ready)]
    public void Pipeline_transitions_are_legal(TaskStatus from, TaskStatus to) =>
        Assert.True(TaskTransitions.IsLegal(from, to));

    [Theory]
    [InlineData(TaskStatus.Created, TaskStatus.Done)]
    [InlineData(TaskStatus.Created, TaskStatus.InProgress)]
    [InlineData(TaskStatus.Done, TaskStatus.InProgress)]
    [InlineData(TaskStatus.Cancelled, TaskStatus.Ready)]
    [InlineData(TaskStatus.Ready, TaskStatus.InReview)]
    [InlineData(TaskStatus.InProgress, TaskStatus.Done)]
    public void Shortcut_transitions_are_illegal(TaskStatus from, TaskStatus to)
    {
        Assert.False(TaskTransitions.IsLegal(from, to));
        Assert.Throws<IllegalTaskTransitionException>(() => TaskTransitions.Require(from, to));
    }

    [Fact]
    public void Every_status_has_a_transition_entry()
    {
        foreach (var status in Enum.GetValues<TaskStatus>())
            _ = TaskTransitions.IsLegal(status, TaskStatus.Cancelled); // must not throw KeyNotFound
    }

    [Theory]
    [InlineData(TaskStatus.InReview, AgentRole.Principal)]
    [InlineData(TaskStatus.Qa, AgentRole.Qa)]
    // Blocked and OutOfBudget are the Principal's to triage — they authored the DAG.
    [InlineData(TaskStatus.Blocked, AgentRole.Principal)]
    [InlineData(TaskStatus.OutOfBudget, AgentRole.Principal)]
    public void Handoff_routing_is_derived_from_status(TaskStatus status, AgentRole role) =>
        Assert.Equal(role, TaskTransitions.RoleFor(status));
}

public class RequirementsRefTests
{
    [Fact]
    public void Parses_file_and_version()
    {
        var r = RequirementsRef.Parse("02-todos-read.md@v3");
        Assert.Equal("02-todos-read.md", r.File);
        Assert.Equal(3, r.Version);
        Assert.Equal("02-todos-read.md@v3", r.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("02-todos-read.md")]
    [InlineData("02-todos-read.md@")]
    [InlineData("02-todos-read.md@3")]
    [InlineData("02-todos-read.md@v0")]
    [InlineData("02-todos-read.md@vx")]
    [InlineData("@v3")]
    public void Malformed_refs_throw(string text) =>
        Assert.Throws<FormatException>(() => RequirementsRef.Parse(text));
}

public class TaskRecordFactoryTests
{
    [Fact]
    public void Create_sets_defaults()
    {
        var task = TaskRecord.Create(TaskType.Task, "Add login", "Users can log in", 50_000);
        Assert.Equal(TaskStatus.Created, task.Status);
        Assert.Equal(0, task.TokensSpent);
        Assert.Empty(task.ContextPaths);
    }

    [Theory]
    [InlineData("", "objective", 1000)]
    [InlineData("title", " ", 1000)]
    [InlineData("title", "objective", 0)]
    [InlineData("title", "objective", -5)]
    public void Invalid_packets_are_refused(string title, string objective, int budget) =>
        Assert.ThrowsAny<ArgumentException>(() =>
            TaskRecord.Create(TaskType.Task, title, objective, budget));
}

public class MessageTests
{
    [Fact]
    public void Create_builds_the_right_subtype()
    {
        var m = Message.Create(MessageType.Escalation, "engineer", "pm", "stuck", taskId: 7);
        Assert.IsType<EscalationMessage>(m);
        Assert.Equal(MessageType.Escalation, m.Type);
        Assert.Equal(MessageStatus.Pending, m.Status);
    }

    [Fact]
    public void Non_client_messages_must_be_task_anchored()
    {
        Assert.Throws<ArgumentException>(() =>
            Message.Create(MessageType.Question, "engineer", "principal", "why?"));
        // Client chat is the one exception (spec: task_id nullable only for client chat).
        _ = Message.Create(MessageType.Question, "client", "pm", "how is it going?");
        _ = Message.Create(MessageType.Status, "pm", "client", "on track");
    }

    [Fact]
    public void Empty_payload_is_refused() =>
        Assert.Throws<ArgumentException>(() =>
            Message.Create(MessageType.Answer, "pm", "client", "  "));

    [Fact]
    public void FromRow_round_trips_every_type()
    {
        foreach (var type in Enum.GetValues<MessageType>())
        {
            var m = Message.FromRow(type, 1, "pm", "client", null, "hi", MessageStatus.Done, "2026-07-19");
            Assert.Equal(type, m.Type);
            Assert.Equal(MessageStatus.Done, m.Status);
        }
    }
}
