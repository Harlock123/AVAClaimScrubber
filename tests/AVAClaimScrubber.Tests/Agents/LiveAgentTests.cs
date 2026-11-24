using System.IO.Pipelines;
using AVAClaimScrubber.Agents;
using AVAClaimScrubber.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AVAClaimScrubber.Tests.Agents;

/// <summary>Agents in the open window: the pipe door (<c>--mcp --attach</c>), the network door, and the command line.</summary>
public sealed class LiveAgentTests
{
    private static string Text(CallToolResult r) => string.Join(" ", r.Content.OfType<TextContentBlock>().Select(t => t.Text));

    [Fact]
    public void An_outside_agent_attaches_through_the_pipe_opens_a_batch_and_shows_a_claim_in_the_window() => HeadlessApp.Run(() =>
    {
        var root = Samples.Copy();
        var w = new MainWindow { AgentPipe = "avaclaimscrubber-test-" + Guid.NewGuid().ToString("N") };
        w.Show();
        Assert.StartsWith("AI agents may connect", w.SetAllowAgents(true));
        Assert.True(LiveListener.Answers(w.AgentPipe));
        var bridge = new LiveBridge(w.AgentPipe, root);
        Pipe up = new(), down = new();
        var server = McpServer.Create(new StreamServerTransport(up.Reader.AsStream(), down.Writer.AsStream(), "attach"), bridge.Options());
        using var stop = new CancellationTokenSource();
        var serving = Task.Run(() => server.RunAsync(stop.Token));
        var client = AgentTests.Wait(McpClient.CreateAsync(new StreamClientTransport(up.Writer.AsStream(), down.Reader.AsStream())));
        try
        {
            Assert.Equal(AgentTests.ToolNames.Length, AgentTests.Wait(client.ListToolsAsync().AsTask()).Count);
            var opened = AgentTests.Wait(client.CallToolAsync("open_file", new Dictionary<string, object?> { ["file"] = "coding-errors.x12" }).AsTask());
            Assert.False(opened.IsError == true, Text(opened));
            Assert.Contains("\"deidentified\":true", Text(opened));
            Assert.Equal("coding-errors.x12", Assert.Single(w.OpenFiles).Name);
            var shown = AgentTests.Wait(client.CallToolAsync("show_in_view", new Dictionary<string, object?> { ["claim"] = "3" }).AsTask());
            Assert.False(shown.IsError == true, Text(shown));
            Assert.Equal(3, w.CurrentClaim!.Number);
            Assert.Equal(1, w.Tabs.SelectedIndex);
            var bad = AgentTests.Wait(client.CallToolAsync("open_file", new Dictionary<string, object?> { ["file"] = "rules/sunplan.json" }).AsTask());
            Assert.True(bad.IsError == true);
            var closed = AgentTests.Wait(client.CallToolAsync("close_file", new Dictionary<string, object?> { ["file"] = "coding-errors.x12" }).AsTask());
            Assert.False(closed.IsError == true, Text(closed));
            Assert.Empty(w.OpenFiles);
        }
        finally
        {
            AgentTests.Wait(client.DisposeAsync().AsTask().ContinueWith(_ => true));
            stop.Cancel();
            AgentTests.Wait(serving.ContinueWith(_ => true));
            AgentTests.Wait(bridge.DisposeAsync().AsTask().ContinueWith(_ => true));
            Assert.Equal("AI agents are shut out.", w.SetAllowAgents(false));
            AgentTests.Wait(w.Agents.StopAsync().ContinueWith(_ => true));
            w.Close();
        }
    });

    [Fact]
    public void The_network_door_opens_on_the_settings_and_wants_the_token() => HeadlessApp.Run(() =>
    {
        var w = new MainWindow();
        w.Show();
        try
        {
            Assert.Equal(8777, new AgentSettings().NetworkAgentsPort);
            Assert.Equal(8777, McpCommandLine.NetworkPort);
            Assert.Equal("AVACLAIMSCRUBBER_MCP_TOKEN", McpCommandLine.TokenVariable);
            w.AgentSettings.AllowNetworkAgents = true;
            w.AgentSettings.NetworkAgentsAddress = "localhost";
            w.AgentSettings.NetworkAgentsPort = 0;
            w.AgentSettings.NetworkAgentsFolder = Samples.Copy();
            Assert.StartsWith("AI agents on other computers may connect", AgentTests.Wait(w.SyncNetworkAsync()));
            using var http = new HttpClient();
            var refused = AgentTests.Wait(http.PostAsync(w.Agents.NetworkUrls.First(), new StringContent("{}", System.Text.Encoding.UTF8, "application/json")));
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, refused.StatusCode);
            w.AgentSettings.Save();
            Assert.DoesNotContain(w.NetworkToken, File.ReadAllText(AgentSettings.Path));
            Assert.StartsWith(AgentSettings.FolderOverride!, AgentSettings.Path);
        }
        finally
        {
            w.AgentSettings.AllowNetworkAgents = false;
            AgentTests.Wait(w.SyncNetworkAsync());
            w.Close();
        }
    });

    [Fact]
    public void The_pipe_name_and_command_line_have_this_programs_names() => HeadlessApp.Run(() =>
    {
        Assert.StartsWith("AVAClaimScrubber-mcp-", LiveProtocol.PipeName);
        Assert.Contains("AVACLAIMSCRUBBER_MCP_TOKEN", McpCommandLine.Usage);
        Assert.Contains("--real", McpCommandLine.Usage);
        var output = new MemoryStream();
        var log = new StringWriter();
        Assert.Equal(2, AgentTests.Wait(McpCommandLine.RunAsync(["--mcp", "--bogus"], () => throw new InvalidOperationException(), new AvaloniaThread(), new MemoryStream(), output, log)));
        Assert.Contains("Unknown option --bogus", log.ToString());
        Assert.Equal(0, output.Length);
    });

    [Fact]
    public void Agents_see_de_identified_values_unless_the_person_says_otherwise() => HeadlessApp.Run(() =>
    {
        var w = new MainWindow();
        w.Show();
        Assert.False(w.AgentsSeeRealValues);
        Assert.StartsWith("AI agents now see real values", w.SetAgentsSeeRealValues(true));
        Assert.True(w.AgentsSeeRealValues);
        w.SetAgentsSeeRealValues(false);
        var forced = new MainWindow { ForceDeidentify = true, RealFromCommandLine = true };
        forced.Show();
        Assert.False(forced.AgentsSeeRealValues);
        Assert.Contains("--deidentify", forced.SetAgentsSeeRealValues(true));
        forced.Close();
        w.Close();
    });
}
