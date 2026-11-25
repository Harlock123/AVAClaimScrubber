# AGENTS.md

Notes for anyone, human or AI agent, changing this code. `README.md` covers what the program does; this file covers
how the code is put together and the rules that keep it working.

## What this is

An 837 claim scrubber in C# on .NET 10 and Avalonia 11.3: it reads 837P and 837I (5010) files into their loops and
claims, checks them in layers (envelope and syntax, structure, arithmetic and dates, code sets, NCCI and MUE edits,
payer rules, and eligibility against the payer's 271s when they're open), and says what's wrong, where, why and how to
fix it. It reads 999, 277CA and 835 responses and replays their denials against the findings. It has an AI Chat (F12) and an MCP server (in the open window with `--mcp
--attach`, headless with `--mcp`, over the network with `--http`, port 8777). Agents see de-identified values unless
the person allows otherwise; the checks always run on the real files.

## Commands

```bash
dotnet build AVAClaimScrubber.slnx
dotnet test AVAClaimScrubber.slnx
dotnet run --project src/AVAClaimScrubber [files] [--real | --deidentify]
dotnet run --project src/AVAClaimScrubber -- --mcp --root samples [--real]       # the MCP server on stdio
dotnet run --project src/AVAClaimScrubber -- --mcp --attach                      # a bridge to the open window
dotnet run --project src/AVAClaimScrubber -- --mcp --http 8777 --root FOLDER     # over the network, with a token
dotnet run --project src/AVAClaimScrubber -- --deidentify INPUT OUTPUT           # de-identified copies, file or folder
cd samples && python3 make_samples.py        # remakes the sample claims and responses
CLAIMS_LIVE_DOWNLOADS=1 dotnet test          # also downloads the real code sets and checks the samples against them
CLAIMS_SHOTS=Screenshots CLAIMS_LIVE_DOWNLOADS=1 dotnet test --filter Screenshots   # the README's pictures
CLAIMS_LIVE_SERVER=http://localhost:11434 CLAIMS_LIVE_MODEL=qwen3:1.7b dotnet test --filter LiveModelTests
```

`dotnet test | head` stalls: write the output to a file and grep it.

## Layout

- `src/AVAClaimScrubber.Core/`: reading and checking, with no Avalonia code.
  - `X12/`: `Segment`, `Delimiters`, `X12File`, `Deidentifier`: X12 Explorer's (`~/RiderProjects/X12_820Explorer`),
    with the namespace changed. Fix them together.
  - `Claims/`: `ClaimModel` (`Loop`, `Claim`, `ServiceLine`, `Diagnosis`, `ClaimBatch`, `Parse`), `ClaimReader` (the
    837's segments into the implementation guide's loops: 1000A/B, 2000A/B/C, 2010xx, 2300, 2310x, 2320, 2330x,
    2400, 2410, 2420x, 2430).
  - `Checks/`: `Finding` (with `Rule`, `Fix`, `ScrubContext`, `ICheck`), `EnvelopeChecks` (layer 1: E and S rules),
    `StructureChecks` (layer 2: L rules, the HL hierarchy, required loops, lines, pointers), `ArithmeticChecks`
    (layer 3: A rules), `RulePack` (JSON packs: overrides per payer, and payer rules), `Scrubber` (runs the layers,
    then applies the packs' overrides). `CodeSetChecks` (layer 4: C rules), `EditChecks` (layer 5: N rules: NCCI PTP,
    MUE, age and sex by ICD-10-CM chapter), `PayerRuleChecks` (layer 6: the packs' rules, ids P-pack-id; kinds
    timely-filing, prior-auth, require-modifier, max-units, not-covered), `EligibilityChecks` (layer 7: V1 to V4,
    against the payers' 271 answers in `ScrubSettings.Eligibility`; none open, it checks nothing; it records the claims
    answered about in `ScrubResult.Answered`), `Corrector` (a corrected copy: the fixes, and
    nothing else, in a new file).
  - `Responses/`: `ResponseCodes` (CARC, RARC and 277 status codes, worded as x12.org publishes them; only the ones
    used), `Responses` (`ResponseReader`: 999 → `Ack`, 277CA → `ClaimStatus`, 835 → `RemitClaim`, 271 →
    `Eligibility.EligibilityAnswer`; IK4-04, the bad value as sent, is never kept), `Replay` (each denial or rejection matched to its claim by CLM01, a 999's by control
    numbers and segment position; reason codes mapped to the checks that answer to them; verdicts Caught, Missed,
    NotCheckable, NoClaim; `Draft` writes a rule pack from the missed denials a payer rule would catch; `DraftAll` drafts from several
    remittances at once, one pack for each payer, each rule's evidence merged across the files).
  - `Eligibility/`: `EligibilityAnswer` (a 271's people, as far as the V rules need them, and the one about a claim),
    `EligibilityCodes` and `ServiceTypes` (AVAEligibility's lists, copied).
  - `Reports/Report.cs`: the HTML page and the Excel workbook (ClosedXML).
  - `CodeSets/`: `CodeSets` (ICD-10-CM, HCPCS Level II and modifiers, NUCC taxonomy, licensed CPT: parsers for each
    publisher's file layout, and a store in the settings folder with a manifest of where each came from),
    `PlaceOfService` (built in, from CMS's page), `Updater` (downloads from CMS and NUCC, only when asked). NCCI PTP
    files are imported, never downloaded: CMS keeps them behind the AMA's licence page.
- `src/AVAClaimScrubber.Mcp/`: the MCP server, no Avalonia.
  - `ClaimsTools.cs` (the tools: `Session` and `Write` wrap every handler, run it on the host's thread and add
    `"deidentified"` to its answer; `Lensed`, `LensResponse` and `LensReplay` give the agent the de-identified copy of a
    file, with the findings of the real one), `ClaimsTools.Orchestration.cs` (batch, steps files, checkpoints of the
    open files and the view).
  - `Hosting.cs`: `ViewState`, `ViewPicture`, `IViewPainter`, `OpenFile` and `OpenResponse`, `IExplorerSession` (the
    open 837s and their scrubs, the responses, the settings, `SeeRealValues`), `ExplorerHost` (with the session's one
    `Deidentifier`), `HeadlessSession`.
  - `ClaimsServer.cs`: the instructions every agent is told, the argument checks, the recording of calls.
    `Recipes.cs`: the 9 recipes. `LiveListener`, `LiveBridge`, `NetworkListener`, `McpCommandLine`, `Workspace` and
    `Json` are AVAHL7Explorer's with the names changed: fix them together.
- `src/AVAClaimScrubber/`: the window, built in code (`MainWindow.cs`; `MainWindow.Parts.cs` holds the hooks later
  parts fill; `MainWindow.Rules.cs` the Rules tab and the packs; `MainWindow.CodeSets.cs`, `MainWindow.Responses.cs`;
  `MainWindow.AI.cs` the AI menu, the chat's dock and the view as agents see it; `MainWindow.Agents.cs` the doors,
  the chat and `AgentsSeeRealValues`). Settings (rules, code sets) live under `MainWindow.SettingsFolder`, which
  tests point at a temporary folder (`SettingsIsolation`). `Agents/`: `Chat/` and `ChatPanel.cs` are copies of
  AVAHL7Explorer's (themselves AVA Modeler's `AVAModeler.Chat`) with this app's wording: a fix there may be wanted
  here. `LiveAgents.cs` (`WindowSession`), `AgentMain.cs` (`--mcp`), `AgentSettings.cs`. `ViewPainter.cs` is
  AVALegacyLens's.
- `tests/AVAClaimScrubber.Tests/`: xUnit v3 on Avalonia.Headless. `Agents/AgentTests` (every tool, the no-leak test,
  every recipe, the chat on `FakeModelServer` and `FakeMessagesServer`), `LiveAgentTests` (the doors), `LiveModelTests`
  (opt-in).
- `samples/`: invented claims, from `make_samples.py`; `samples/responses/` the 999s, 277CA and 835 that answer
  them; `samples/rules/` an invented payer's rule pack. `Screenshots/`: the README's pictures (`Screenshots.cs`).

## Rules that keep it working

- **Core stays free of Avalonia.**
- **Every rule has an id, a why and a source**, and a test says so. Ids: E envelope, S syntax, L loops, A arithmetic
  and dates, C code sets, N NCCI and MUE, P payer rules.
- **Findings are pinned.** `FindingsTests` lists every finding each sample has: a change that finds more (a false
  hit) or fewer fails it. Plant a problem in `make_samples.py` and pin it together.
- **Messages never quote people's details.** A finding describes a date ("27 days after the batch was made"), never
  shows it, and never names a member ID, account number or person: its text must be safe to show an AI agent.
- **Code sets come from their publishers, never bundled.** The program downloads them on Update; tests use
  `tests/…/Data/`, unchanged excerpts of the real files. CPT is the AMA's: shape only unless a licensed file is
  loaded, and none is in the repository. The test NCCI pairs are invented (and say so); never put CMS's PTP files in
  the repository. `CLAIMS_LIVE_DOWNLOADS=1 dotnet test` checks the real downloads still work.
- **A reason code maps to checks, and the map is the claim.** `Replay.Carc`, `Rarc`, `Status` and the 999 maps say which
  rules answer to which codes; a new rule that answers to a denial goes into them, with a planted denial in
  `samples/responses` and a line in `ReplayTests`. Codes that nothing in a claim can show (medical necessity) are
  `NotCheckable`, with the reason: never call those misses. Eligibility codes (CARC 22, 26, 27, 31, 197's V4; 277CA
  status 32, 33) map to the V rules: not checkable when no 271 answered about the claim, missed when one did and showed
  nothing.
- **Eligibility comes from AVAEligibility.** `Eligibility/EligibilityCodes.cs` and `Eligibility/ServiceTypes.cs` are
  copies of its lists (~/RiderProjects/AVAEligibility): fix them together. `EligibilityAnswer` reads only what the V
  rules need; AVAEligibility reads a 271 in full.
- **Drafted rules are drafts.** `Replay.Draft` and `Replay.DraftAll` save a pack with `"draft": true` and each rule's evidence in its "why";
  the window saves it to a file and never adds it: a person checks it against the payer's policy first.
- **Agents read the de-identified copy; the checks read the real file.** De-identifying moves dates but not BHT04, so
  checks on a de-identified copy would differ: never run them there. Every tool reads files through `Lensed` (or
  `LensResponse`, `LensReplay`); a tool that reads a file another way would bypass de-identification: don't. Anything
  a tool writes comes from what the agent may see, except `deidentify_file`, which always de-identifies.
- **The no-leak test is the contract.** `AgentTests.No_tool_result_…` calls every tool on every sample and fails if
  any answer, or any file an agent wrote, holds a real name, member ID, account, payer claim or authorization number,
  address or date from the samples (`Phi()`). A new tool goes into it; a new place for people's details goes into
  `Deidentifier` (shared with X12 Explorer) and `Phi()`.
- **Agents never write over or delete, and never change what the checks use.** Paths go through `Workspace`; rule
  packs an agent writes or drafts are files for the person to add.
- **New tools** need `[Description]` on the tool and every parameter (snake_case names), the right `ReadOnly`
  annotation, a place in `AgentTests.ToolNames`, a test, and a line in the README. Recipes are run call by call.
- **No real model, keys or user settings in tests.** `AgentSettingsIsolation` points `AgentSettings.FolderOverride`
  at a temporary folder; chat tests answer from the fake servers.
- **Dates are checked as of the batch's creation (BHT04)**, not today, so a file checks the same whenever it's checked.
- **No real claims in the repository.** Every person, member, provider, NPI and tax ID in the samples is invented.

## Making a change

- Run `dotnet test` after any change.
- Pushes to `main` publish builds to the rolling `latest` pre-release; a `v*` tag makes a proper release.
