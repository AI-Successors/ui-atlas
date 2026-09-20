using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using UiAtlas.Core.Mcp;

namespace UiAtlas.Core.Windows.Tests;

public sealed class GridMcpProtocolTests
{
    [Fact]
    public async Task RealStdioExecutableListsEightBoundedToolsAndUnknownDoesNotStart()
    {
        var root = Path.Combine(Path.GetTempPath(), "ui-atlas-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executable = Path.ChangeExtension(typeof(GridMcpTools).Assembly.Location, ".exe");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var client = await McpClient.CreateAsync(new StdioClientTransport(new()
            {
                Command = executable, Arguments = ["--catalog-root", root], Name = "public-grid-test"
            }), cancellationToken: timeout.Token);
            var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
            Assert.Equal(["cancel_grid_exploration", "export_grid_to_excel", "get_grid_exploration", "get_grid_read", "list_app_grids", "list_apps", "show_app_grids", "start_grid_read"],
                tools.Select(tool => tool.Name).Order(StringComparer.Ordinal).ToArray());
            var listed = await client.CallToolAsync("list_apps", cancellationToken: timeout.Token);
            using var catalog = JsonDocument.Parse(((TextContentBlock)listed.Content[0]).Text);
            foreach (var app in catalog.RootElement.GetProperty("apps").EnumerateArray())
            {
                Assert.False(app.GetProperty("hasMap").GetBoolean());
                Assert.Equal(0, app.GetProperty("savedGridCount").GetInt32());
            }
            Assert.True(catalog.RootElement.GetProperty("listingComplete").GetBoolean());
            var unknown = await client.CallToolAsync("get_grid_exploration", new Dictionary<string, object?> { ["acquisitionId"] = "previous-host-id" }, cancellationToken: timeout.Token);
            using var response = JsonDocument.Parse(((TextContentBlock)unknown.Content[0]).Text);
            Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("unknown_acquisition", response.RootElement.GetProperty("error").GetProperty("code").GetString());
            var arguments = new Dictionary<string, object?>
            {
                ["appId"] = "unknown-app", ["requestId"] = "protocol-only-no-live-target"
            };
            var started = await client.CallToolAsync("show_app_grids", arguments, cancellationToken: timeout.Token);
            using var start = JsonDocument.Parse(((TextContentBlock)started.Content[0]).Text);
            Assert.False(start.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("unknown_app", start.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(JsonValueKind.Null, start.RootElement.GetProperty("operation").ValueKind);
            if (catalog.RootElement.GetProperty("apps").GetArrayLength() > 0)
            {
                arguments["appId"] = catalog.RootElement.GetProperty("apps")[0].GetProperty("appId").GetString();
                var empty = await client.CallToolAsync("show_app_grids", arguments, cancellationToken: timeout.Token);
                using var noMap = JsonDocument.Parse(((TextContentBlock)empty.Content[0]).Text);
                Assert.Equal("no_map", noMap.RootElement.GetProperty("error").GetProperty("code").GetString());
                Assert.Equal(JsonValueKind.Null, noMap.RootElement.GetProperty("operation").ValueKind);
            }
            var cancelled = await client.CallToolAsync("cancel_grid_exploration", new Dictionary<string, object?> { ["acquisitionId"] = "unknown-operation" }, cancellationToken: timeout.Token);
            using var terminalCancel = JsonDocument.Parse(((TextContentBlock)cancelled.Content[0]).Text);
            Assert.Equal("unknown_acquisition", terminalCancel.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ExactExecutableDispatchesWorkerBeforeMcp()
    {
        var executable = Path.ChangeExtension(typeof(GridMcpTools).Assembly.Location, ".exe");
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            ArgumentList = { "__uia-worker" }, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token);
        Assert.Equal(64, process.ExitCode);
        Assert.Equal("", await process.StandardOutput.ReadToEndAsync(timeout.Token));
    }
}
