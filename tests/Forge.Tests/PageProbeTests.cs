using System.Net;
using Forge.Core.Ci;
using Forge.Core.Ui;

namespace Forge.Tests;

/// <summary>
/// The probe against a real browser and a real page. Skipped where no browser can be installed,
/// which is the same rule the harness follows: an interface it cannot render is not a failure.
/// </summary>
public class PageProbeTests : IDisposable
{
    private readonly string _browsers =
        Path.Combine(Path.GetTempPath(), "forge-test-browsers");
    private readonly HttpListener _server = new();
    private readonly string _baseUrl;

    public PageProbeTests()
    {
        // A free port, taken by asking the OS for one and handing it straight to the listener.
        var port = FreePort();
        _baseUrl = $"http://127.0.0.1:{port}";
        _server.Prefixes.Add(_baseUrl + "/");
        _server.Start();
    }

    public void Dispose()
    {
        _server.Close();
        GC.SuppressFinalize(this);
    }

    /// <summary>Serves one page, once, and returns when the browser has been given it.</summary>
    private void Serve(string html) => Task.Run(async () =>
    {
        while (_server.IsListening)
        {
            var context = await _server.GetContextAsync();
            var body = System.Text.Encoding.UTF8.GetBytes(html);
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }
    });

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task The_browser_sees_what_the_markup_does_not_say()
    {
        // The page a DOM check would call correct: the panel carries `hidden`, and a class
        // sets `display: flex`, so the browser shows it anyway. This is the live defect.
        Serve("""
            <!doctype html><html><head><title>Board</title>
            <style>
              body { margin: 0; font-family: sans-serif; }
              .row { display: flex; gap: 8px; }
              .col { width: 300px; height: 200px; }
              .todo { background: rgb(231, 250, 234); }
              .doing { background: rgb(255, 243, 224); }
            </style></head>
            <body>
              <div class="row" hidden data-testid="rename-panel"><input><button>Save</button></div>
              <div class="row">
                <div class="col todo" data-testid="column-todo">To do</div>
                <div class="col doing" data-testid="column-doing">Doing</div>
              </div>
              <script>console.error('TypeError: cards is not iterable');</script>
            </body></html>
            """);

        var pages = await PageProbe.CaptureAsync(_baseUrl, ["/"], _browsers);
        if (pages is null) return;   // no browser on this machine: the harness would skip too

        var page = Assert.Single(pages);
        Assert.Equal("Board", page.Title);

        // The panel is hidden in the markup and visible on screen — measured, not inferred.
        var panel = page.Elements.Single(e => e.TestId == "rename-panel");
        Assert.True(panel.MarkedHidden);
        Assert.True(panel.Visible);

        // The two columns really are side by side, and really are different colours.
        var todo = page.Elements.Single(e => e.TestId == "column-todo");
        var doing = page.Elements.Single(e => e.TestId == "column-doing");
        Assert.Equal(todo.Box.Y, doing.Box.Y, 1);
        Assert.True(doing.Box.X > todo.Box.X);
        Assert.True(PageHealth.Distance(
            PageHealth.Rgb(todo.Background)!.Value, PageHealth.Rgb(doing.Background)!.Value) >= 0.02);

        Assert.Contains(page.ConsoleErrors, e => e.Contains("TypeError"));

        // And the health rules turn that into feedback an engineer can act on.
        var problems = PageHealth.Problems(pages);
        Assert.Contains(problems, p => p.Contains("rename-panel") && p.Contains("marked hidden"));
    }

    [Fact]
    public async Task A_handle_the_contract_declares_and_the_page_omits_is_visible_to_the_probe()
    {
        Serve("""
            <!doctype html><html><head><title>Board</title></head>
            <body>
              <div data-testid="column-todo">To do</div>
              <div class="board-column--doing">Doing</div>
            </body></html>
            """);

        var pages = await PageProbe.CaptureAsync(_baseUrl, ["/"], _browsers);
        if (pages is null) return;

        // The engineer styled the second column but never gave it the declared handle, so the
        // rendered page carries one of the two ids the contract promised QA.
        var rendered = pages[0].Elements.Where(e => e.TestId.Length > 0).Select(e => e.TestId).ToList();
        Assert.Contains("column-todo", rendered);
        Assert.DoesNotContain("column-doing", rendered);
    }
}
