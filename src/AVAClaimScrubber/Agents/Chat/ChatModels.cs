using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AVAClaimScrubber.Mcp;
using McpTool = ModelContextProtocol.Protocol.Tool;

namespace AVAClaimScrubber.Agents.Chat;

/// <summary>The Claude models the chat offers, by their API ids.</summary>
public static class ChatModels
{
    public const string Opus = "claude-opus-5-5";
    public const string Sonnet = "claude-sonnet-5-5";
    public const string Haiku = "claude-haiku-4-5-20251001";

    /// <summary>The model a new chat starts with.</summary>
    public const string Default = Opus;

    /// <summary>The choices, most capable first, with a name to show.</summary>
    public static readonly IReadOnlyList<(string Id, string Label)> All =
    [
        (Opus, "Claude Opus 5.5"),
        (Sonnet, "Claude Sonnet 5.5"),
        (Haiku, "Claude Haiku 4.5"),
    ];

    public static string Label(string id) => All.FirstOrDefault(m => m.Id == id).Label ?? id;
}

/// <summary>What the model is told: the MCP server's own instructions, and a few lines about working inside the program.</summary>
public static class ChatPrompt
{
    public const string InApp = """
        You are working inside AVA Claim Scrubber itself, in its AI Chat panel, with the person watching. Talk to them briefly and plainly: say what you're about to look at, look (list_files, open_file, get_batch, get_findings, get_claim, lookup_code, replay), then answer in plain words, naming the file, claim and rule (A1, C3) your answer rests on. Keep it to a few sentences; the tool calls show in the panel on their own. Read before you answer: never guess a code's meaning, whether it's valid, or a payer's policy; the tools give them. To point the person at a place, use show_in_view (it selects it in their window) or render_view. Unless your answers say "deidentified": false, names, identifiers and dates are stand-ins; say so when you quote them. Files you read and write are in the working folder; you only ever write new files there.
        """;

    /// <summary>The system prompt: the same instructions every MCP agent gets, then <see cref="InApp"/>.</summary>
    public static string System => ClaimsServer.Instructions.TrimEnd() + "\n\n" + InApp.TrimEnd();

    /// <summary>Added for a model given only <see cref="ChatTools.Core"/>.</summary>
    public const string CoreToolsNote =
        "In this chat you have a core set of the server's tools, not all of them: the instructions above may name tools you don't have, so work with the ones you're given.";

    /// <summary>Added for a model that can't see pictures (its tools' pictures become a line of text).</summary>
    public const string NoPicturesNote =
        "You can't see pictures in this chat: render_view still shows the person the claim, but read it yourself with get_claim, get_segments and get_findings.";
}

/// <summary>Which of the MCP server's tools the chat offers a model.</summary>
public static class ChatTools
{
    /// <summary>
    /// The core set for small local models, which choose better among fewer tools: finding and opening files, the
    /// claims and their findings, the rules, looking codes up, and the responses' replay.
    /// </summary>
    public static readonly IReadOnlyList<string> Core =
    [
        "list_files", "open_file", "list_open", "get_batch", "get_claim", "get_findings", "explain_rule", "lookup_code", "search_codes",
        "get_response", "replay", "save_corrected_copy", "show_in_view",
    ];

    /// <summary>The core tools among <paramref name="tools"/>, in the server's order.</summary>
    public static IReadOnlyList<McpTool> CoreOf(IReadOnlyList<McpTool> tools) => tools.Where(t => Core.Contains(t.Name)).ToList();
}
