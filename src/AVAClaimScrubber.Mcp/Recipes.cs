using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace AVAClaimScrubber.Mcp;

/// <summary>
/// Saved recipes, served as MCP prompts: short step-by-step plans for what agents are often asked to do with claims,
/// written with the real tool names and arguments. Each step that calls a tool shows the call on a line of its own,
/// "→ tool {arguments}" (the tests run every recipe through the server, call by call, on the samples, so a recipe that
/// stops working fails the build). The defaults name the samples' files.
/// </summary>
[McpServerPromptType]
public static class Recipes
{
    /// <summary>The marker that starts a step's tool call in a recipe's text.</summary>
    public const string CallMarker = "→ ";

    [McpServerPrompt(Name = "scrub_batch", Title = "Scrub a batch before sending it")]
    [Description("Checks a batch of claims: which are clean, which would be rejected or denied and why, worst first, with what to change on each.")]
    public static string ScrubBatch(
        [Description("The 837 file (default \"clinic-837p.x12\").")] string? file = null)
    {
        var f = Or(file, "clinic-837p.x12");
        var r = new Recipe($"Check the claims in {f} before they're sent, and say what to change.");
        r.Step("Open the file: every claim is checked as it opens.", "open_file", new { file = f });
        r.Step("Each claim's status and how many findings.", "get_batch", new { file = f });
        r.Step("The findings that stop a claim (rejections and denials), worst first.", "get_findings", new { file = f, severity = "deny" });
        r.Step("The warnings too.", "get_findings", new { file = f, severity = "warn" });
        r.Step("Why the first rule matters and where it comes from.", "explain_rule", new { rule = "A1" });
        r.Step("Show the person the first claim with a problem.", "render_view", new { file = f, claim = "2" });
        return r.Text("Report: how many claims are clean, how many would be rejected at the door and how many denied after; then each problem claim by number and account, what's wrong (rule, where) and what to change. Group claims with the same problem. Say which fixes are clear enough to make automatically (save_corrected_copy).");
    }

    [McpServerPrompt(Name = "fix_and_resend", Title = "Make the clear fixes")]
    [Description("Writes a corrected copy of a batch with the clear fixes (counts, control numbers, totals, line numbers, decimal points), checks it, and lists what's left for a person.")]
    public static string FixAndResend(
        [Description("The 837 file (default \"envelope-errors.x12\").")] string? file = null,
        [Description("The corrected copy to write (default \"envelope-errors-corrected.x12\").")] string? output = null)
    {
        var f = Or(file, "envelope-errors.x12");
        var o = Or(output, Path.GetFileNameWithoutExtension(f) + "-corrected" + Path.GetExtension(f));
        var r = new Recipe($"Make the clear fixes to {f} in a corrected copy, {o}, and say what's left.");
        r.Step(null, "open_file", new { file = f });
        r.Step("The findings with a fix.", "get_findings", new { file = f });
        r.Step("Write the corrected copy: the fixes, and nothing else.", "save_corrected_copy", new { file = f, output = o });
        r.Step("Open it, to check it again.", "open_file", new { file = o });
        r.Step("What's left in it.", "get_findings", new { file = o });
        return r.Text("List each change (segment, element, was → now, why). Then what's still wrong: those need a person (a code to choose, a date to confirm). If agents see de-identified values, say the copy is de-identified and the person makes the real one with File › Save Corrected Copy.");
    }

    [McpServerPrompt(Name = "explain_denial", Title = "Explain a denial code")]
    [Description("Explains a payer's denial or remark code (a CARC such as CO-197, a RARC such as N54, a 277 status): what it means in X12's words, which checks answer to it, and what to do.")]
    public static string ExplainDenial(
        [Description("The code (default \"CO-197\").")] string? code = null)
    {
        var c = Or(code, "CO-197");
        var r = new Recipe($"Explain the denial code {c}: what it means and how to stop it happening again.");
        r.Step("The code in X12's words, and the checks that answer to it.", "lookup_code", new { code = c });
        r.Step("The payer rules in use (a prior-authorization or filing-limit rule may answer to it).", "list_rule_packs", new { });
        return r.Text("Say what the code means in plain words, whether it's the claim's fault (something a check can catch) or not (eligibility, medical necessity), which checks or payer rules would have stopped it, and what the person should do about the denied claim now (correct and resend, appeal, bill the patient).");
    }

    [McpServerPrompt(Name = "replay_remittance", Title = "Replay a remittance against the claims")]
    [Description("Sets each denial in an 835 (or rejection in a 277CA or 999) against the claims' findings: which a check caught, which it missed, and which no check could catch.")]
    public static string ReplayRemittance(
        [Description("The response (default \"responses/sunplan-835.x12\").")] string? response = null,
        [Description("The claims it answers (default \"payer-837p.x12\").")] string? claims = null)
    {
        var resp = Or(response, "responses/sunplan-835.x12");
        var c = Or(claims, "payer-837p.x12");
        var r = new Recipe($"Replay the denials in {resp} against the claims in {c}.");
        r.Step("Open the claims.", "open_file", new { file = c });
        r.Step("Open the response.", "open_file", new { file = resp });
        r.Step("What the payer said, claim by claim.", "get_response", new { file = resp });
        r.Step("Each denial set against the findings.", "replay", new { file = resp });
        r.Step("Show the person the replay.", "render_view", new { file = resp });
        return r.Text("Report the totals (caught, missed, not checkable, no claim open), then each missed denial: the claim, the code in X12's words, and which rule would have caught it. Missed ones a payer rule would catch can be drafted (draft_rule_pack). Say plainly that not-checkable ones (eligibility, medical necessity) need a different fix: an eligibility check before the visit, documentation.");
    }

    [McpServerPrompt(Name = "draft_payer_rules", Title = "Draft payer rules from denials")]
    [Description("Drafts a rule pack from a remittance's missed denials, tries it on the claims, and saves it for the person to check and add.")]
    public static string DraftPayerRules(
        [Description("The response (default \"responses/sunplan-835.x12\").")] string? response = null,
        [Description("The claims it answers (default \"payer-837p.x12\").")] string? claims = null,
        [Description("The draft to write (default \"sunplan-drafted.json\").")] string? output = null)
    {
        var resp = Or(response, "responses/sunplan-835.x12");
        var c = Or(claims, "payer-837p.x12");
        var o = Or(output, "sunplan-drafted.json");
        var r = new Recipe($"Draft payer rules from the denials in {resp} that no check caught, and save them in {o} for the person.");
        r.Step(null, "open_file", new { file = c });
        r.Step(null, "open_file", new { file = resp });
        r.Step("Which denials were missed.", "replay", new { file = resp });
        r.Step("Draft the rules, and save the draft.", "draft_rule_pack", new { file = resp, output = o });
        r.Step("The payer rule kinds, and the packs already in use.", "list_rule_packs", new { });
        return r.Text("Show each drafted rule with its evidence (its \"why\"), and what it would flag (test_rule_pack, given the pack's JSON). Say it's a draft: a filing limit drafted from denials is an upper bound, and a prior-authorization list only names the procedures seen. The person checks it against the payer's policy and adds it with File › Add Rule Pack…; never claim it's in use.");
    }

    [McpServerPrompt(Name = "draft_rules_from_weeks", Title = "Draft payer rules from weeks of remittances")]
    [Description("Opens several weeks of remittances beside the claims they answer, replays each, and drafts one rule pack for each payer from all their missed denials together.")]
    public static string DraftRulesFromWeeks(
        [Description("The remittances, comma-separated (default the samples' two Sun Plan 835s and Lakeshore Health's).")] string? responses = null,
        [Description("The claims they answer, comma-separated (default \"payer-837p.x12, payer-later-837p.x12\").")] string? claims = null,
        [Description("The folder to save the drafts in (default \"drafts\").")] string? output = null)
    {
        var resp = Or(responses, "responses/sunplan-835.x12, responses/sunplan-835-1204.x12, responses/lakeshore-835-1205.x12").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var c = Or(claims, "payer-837p.x12, payer-later-837p.x12").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var o = Or(output, "drafts");
        var r = new Recipe($"Draft payer rules from the denials no check caught across {resp.Length} remittances, one pack for each payer, saved in {o}/ for the person.");
        foreach (var f in c) r.Step(null, "open_file", new { file = f });
        foreach (var f in resp) r.Step(null, "open_file", new { file = f });
        foreach (var f in resp) r.Step($"Which denials in {Path.GetFileName(f)} were missed.", "replay", new { file = f });
        r.Step("Draft from them all together, one pack for each payer, and save the drafts.", "draft_rule_pack", new { all = true, output = o });
        r.Step("The packs already in use.", "list_rule_packs", new { });
        return r.Text("For each payer, show its drafted rules with their evidence (each \"why\" names the files it rests on), and say what the weeks together showed that one file didn't: more procedures needing authorization, a shorter filing gap, the same modifier on more visits. It's a draft: a filing limit drafted from denials is an upper bound, and a list of procedures only names the ones seen. The person checks each pack against the payer's policy and adds it with File › Add Rule Pack…; never claim it's in use.");
    }

    [McpServerPrompt(Name = "look_up_codes", Title = "Look up codes on a claim")]
    [Description("Looks up every code on a claim (diagnoses, procedures, modifiers, place of service, NPIs, taxonomy) and says what each is and whether it's valid for the claim's dates.")]
    public static string LookUpCodes(
        [Description("The 837 file (default \"coding-errors.x12\").")] string? file = null,
        [Description("The claim (default \"2\").")] string? claim = null)
    {
        var f = Or(file, "coding-errors.x12");
        var c = Or(claim, "2");
        var r = new Recipe($"Look up every code on claim {c} of {f}.");
        r.Step(null, "open_file", new { file = f });
        r.Step("The claim's codes and findings.", "get_claim", new { file = f, claim = c });
        r.Step("Which code sets are loaded (only those are checked against).", "get_code_sets", new { });
        r.Step("Each diagnosis, procedure and modifier, one call each (this one is the sample's header code).", "lookup_code", new { code = "E11" });
        r.Step("Words to find a better code.", "search_codes", new { text = "type 2 diabetes", set = "icd" });
        return r.Text("List each code with its description and whether it's valid (billable, in effect on the date, the right shape). For a header code, offer the billable codes under it, but say the choice is the coder's: it depends on the record. Never call a code valid or invalid from memory.");
    }

    [McpServerPrompt(Name = "try_rule_pack", Title = "Try a rule pack before adding it")]
    [Description("Shows what a rule pack would add to or take from the findings on the open claims, without adding it.")]
    public static string TryRulePack(
        [Description("The 837 file (default \"payer-837p.x12\").")] string? file = null)
    {
        var f = Or(file, "payer-837p.x12");
        var r = new Recipe($"Try a payer rule pack on {f} before anyone adds it.");
        r.Step(null, "open_file", new { file = f });
        r.Step("The payer rule kinds and the packs in use.", "list_rule_packs", new { });
        r.Step("Try the pack: here, a 90-day filing limit and telehealth modifier 95 for Sun Plan.", "test_rule_pack",
            new { file = f, pack = """{"format":"avaclaimscrubber.rules","name":"Sun Plan trial","payers":["SUN01"],"rules":[{"id":"timely","kind":"timely-filing","days":90},{"id":"tele","kind":"require-modifier","places":["02","10"],"modifier":"95"}]}""" });
        return r.Text("Say what the pack would flag (each finding it adds, claim and why) and what it would stop flagging. If it looks right, save it with write_rule_pack for the person to read and add; never say it's in use.");
    }

    [McpServerPrompt(Name = "month_end_report", Title = "A report of the month's claims")]
    [Description("Checks every 837 in the working folder, replays the responses, and writes a report (.html or .xlsx).")]
    public static string MonthEndReport(
        [Description("The report to write (default \"claims-report.html\"; .xlsx for a workbook).")] string? output = null)
    {
        var o = Or(output, "claims-report.html");
        var r = new Recipe($"Check this month's claims and responses and write a report, {o}.");
        r.Step("The X12 files in the working folder.", "list_files", new { });
        r.Step("Open each 837 and each response (here, two of the samples).", "batch", new { steps = new object[] { new { tool = "open_file", args = new { file = "clinic-837p.x12" } }, new { tool = "open_file", args = new { file = "responses/clinic-277ca.x12" } } } });
        r.Step("The open files' totals.", "list_open", new { });
        r.Step("Write the report.", "export_report", new { output = o });
        return r.Text("Summarise: claims checked, clean, rejected or denied before sending; the most common problems (by rule); and for each response, how many denials a check caught or missed. Name the report file. Say the report lists claims by account number, so it's handled like the claims.");
    }

    private static string Or(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private sealed class Recipe(string intro)
    {
        private readonly StringBuilder _text = new(intro + "\n\nFollow these steps in order. A line starting with \"" + CallMarker.Trim() + "\" is one tool call, with its arguments as JSON. If a step fails, read the error, fix the cause and carry on. File names and claims in these calls are the samples': use the person's own.\n");
        private int _number;

        public void Step(string? says, string tool, object args)
        {
            _number++;
            _text.Append('\n').Append(_number).Append('.').Append(says != null ? " " : "");
            if (says != null) _text.Append(says);
            _text.Append("\n   ");
            _text.Append(CallMarker).Append(tool).Append(' ').Append(JsonSerializer.Serialize(args, Json.Options));
        }

        public string Text(string closing) => _text.Append("\n\n").Append(closing).ToString();
    }
}
