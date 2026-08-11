using Forge.Core.Db;
using Forge.Core.Model;
using Microsoft.Data.Sqlite;

namespace Forge.Tests;

/// <summary>
/// The phases the plan is grouped into: how naming one gets it created or reused, and where it
/// lands in the order the client reads.
/// </summary>
public class MilestoneTests : IDisposable
{
    private readonly SqliteConnection _conn = Database.OpenProject(":memory:");

    public void Dispose() => _conn.Dispose();

    private MilestoneRepository Milestones => new(_conn);

    [Fact]
    public void Naming_a_phase_twice_reuses_it_rather_than_creating_a_second()
    {
        var first = Milestones.EnsureByName("Books API");
        var again = Milestones.EnsureByName("Books API");

        Assert.Equal(first.Id, again.Id);
        Assert.Single(Milestones.List());
    }

    [Fact]
    public void Phases_are_ordered_by_when_they_were_first_named()
    {
        Milestones.EnsureByName("Bootstrap");
        Milestones.EnsureByName("Books API");
        Milestones.EnsureByName("Library interface");
        // Returning to an earlier phase does not move it to the end.
        Milestones.EnsureByName("Bootstrap");

        Assert.Equal(["Bootstrap", "Books API", "Library interface"], Milestones.Names());
    }

    [Fact]
    public void A_name_differing_only_in_case_or_spacing_is_the_same_phase()
    {
        var first = Milestones.EnsureByName("Books API");

        Assert.Equal(first.Id, Milestones.EnsureByName("books api").Id);
        Assert.Equal(first.Id, Milestones.EnsureByName("  Books API  ").Id);
    }

    [Fact]
    public void The_first_phase_goes_to_the_front_however_late_it_is_created()
    {
        Milestones.EnsureByName("Books API");
        Milestones.EnsureByName("Library interface");

        Milestones.EnsureFirst(MilestoneRepository.GettingStarted);

        Assert.Equal(
            [MilestoneRepository.GettingStarted, "Books API", "Library interface"],
            Milestones.Names());
    }

    [Fact]
    public void Creating_the_first_phase_again_leaves_the_order_alone()
    {
        Milestones.EnsureFirst(MilestoneRepository.GettingStarted);
        Milestones.EnsureByName("Books API");
        Milestones.EnsureFirst(MilestoneRepository.GettingStarted);

        Assert.Equal([MilestoneRepository.GettingStarted, "Books API"], Milestones.Names());
    }

    [Fact]
    public void An_empty_name_is_refused_rather_than_creating_a_nameless_phase()
    {
        Assert.Throws<ArgumentException>(() => Milestones.EnsureByName("   "));
    }

    [Fact]
    public void A_task_carries_the_phase_and_the_name_the_client_reads()
    {
        var phase = Milestones.EnsureByName("Books API");
        var tasks = new TaskRepository(_conn);

        var task = tasks.Insert(TaskRecord.Create(
            TaskType.Task, "implement-books-http-api", "objective", 10_000,
            displayName: "Adding, listing and deleting books",
            milestoneId: phase.Id));

        var read = tasks.Get(task.Id);
        Assert.Equal(phase.Id, read.MilestoneId);
        Assert.Equal("Adding, listing and deleting books", read.DisplayName);
    }
}
