using Forge.Core.Design;

namespace Forge.Tests;

/// <summary>
/// The interface half of the contract document: the handles a page must carry, declared in one
/// fixed shape and refused when it is written any other way — so two change requests cannot
/// leave two different formats behind.
/// </summary>
public class InterfaceContractTests
{
    /// <summary>A minimal valid document, with whatever `x-interface` block a test supplies.</summary>
    private static string Document(string interfaceBlock) => $"""
        openapi: 3.0.3
        info:
          title: Board
          version: "1.0"
        {interfaceBlock}
        paths:
          /api/board:
            get:
              operationId: board-get
              x-requirement: 01-board.md
              responses:
                "200":
                  description: the board
                "404":
                  description: not found
        """;

    private const string ValidInterface = """
        x-interface:
          - path: /
            requirement: 01-board.md
            elements:
              - testid: column-todo
                is: the To do column
              - testid: board-name-edit
                is: the input that renames the board
                visible: on-demand
              - testid: card
                is: a single card
                repeats: true
        """;

    [Fact]
    public void The_declared_handles_are_parsed_from_the_same_document_as_the_operations()
    {
        var (contract, errors) = ApiContract.Validate(Document(ValidInterface));

        Assert.Empty(errors);
        var page = Assert.Single(contract!.Interface.Pages);
        Assert.Equal("/", page.Path);
        Assert.Equal("01-board.md", page.Requirement);
        Assert.Equal(["column-todo", "board-name-edit", "card"], page.Elements.Select(e => e.TestId));

        // On-demand elements are declared as such, so "hidden until clicked" is not read as missing.
        Assert.True(page.Elements.Single(e => e.TestId == "board-name-edit").OnDemand);
        Assert.True(page.Elements.Single(e => e.TestId == "card").Repeats);
        Assert.Equal(["column-todo", "card"], contract.Interface.AlwaysVisibleTestIds);
    }

    [Fact]
    public void A_document_with_no_interface_is_valid_and_declares_no_pages()
    {
        var (contract, errors) = ApiContract.Validate(Document(""));

        Assert.Empty(errors);
        Assert.Empty(contract!.Interface.Pages);
    }

    [Fact]
    public void A_key_the_model_invented_is_refused_with_the_keys_that_exist()
    {
        var (contract, errors) = ApiContract.Validate(Document("""
            x-interface:
              - path: /
                requirement: 01-board.md
                elements:
                  - testid: column-todo
                    selector: .board-column--todo
                    is: the To do column
            """));

        Assert.Null(contract);
        var error = Assert.Single(errors);
        Assert.Contains("unknown key `selector`", error);
        Assert.Contains("testid, is, visible, repeats", error);
    }

    [Fact]
    public void A_value_outside_the_closed_set_is_refused_with_the_values_that_exist()
    {
        var (_, errors) = ApiContract.Validate(Document("""
            x-interface:
              - path: /
                requirement: 01-board.md
                elements:
                  - testid: column-todo
                    is: the To do column
                    visible: sometimes
            """));

        Assert.Contains(errors, e => e.Contains("`visible` is 'sometimes'") && e.Contains("always, on-demand"));
    }

    [Fact]
    public void A_missing_handle_or_a_missing_description_is_refused()
    {
        var (_, errors) = ApiContract.Validate(Document("""
            x-interface:
              - path: /
                requirement: 01-board.md
                elements:
                  - is: the To do column
                  - testid: Board_Name
                    is: the board name
            """));

        Assert.Contains(errors, e => e.Contains("`testid` is required"));
        Assert.Contains(errors, e => e.Contains("must be kebab-case"));
    }

    [Fact]
    public void The_same_handle_declared_twice_is_refused()
    {
        var (_, errors) = ApiContract.Validate(Document("""
            x-interface:
              - path: /
                requirement: 01-board.md
                elements:
                  - testid: card
                    is: a card
                  - testid: card
                    is: another card
            """));

        Assert.Contains(errors, e => e.Contains("declared more than once"));
    }

    [Fact]
    public void A_requirement_served_by_a_page_counts_as_covered()
    {
        // A user interface used to need listing in x-non-http-requirements to escape the gate.
        // Declaring its page is a better answer: it says what exists rather than what to ignore.
        var (contract, _) = ApiContract.Validate(Document(ValidInterface));

        Assert.Contains("01-board.md", contract!.Interface.CoveredRequirements);
    }
}
