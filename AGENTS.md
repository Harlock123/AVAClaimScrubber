# AGENTS.md

Notes for anyone, human or AI agent, changing this code. `README.md` covers what the program does; this file covers
how the code is put together and the rules that keep it working.

## What this is

An 837 claim scrubber in C# on .NET 10 and Avalonia 11.3: it reads 837P and 837I (5010) files into their loops and
claims, checks them in layers (envelope and syntax, structure, arithmetic and dates, code sets, NCCI and MUE edits,
payer rules), and says what's wrong, where, why and how to fix it.

## Commands

```bash
dotnet build AVAClaimScrubber.slnx
dotnet test AVAClaimScrubber.slnx
dotnet run --project src/AVAClaimScrubber [files]
cd samples && python3 make_samples.py        # remakes the sample claims
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
    then applies the packs' overrides). `CodeSetChecks` (layer 4: C rules).
  - `CodeSets/`: `CodeSets` (ICD-10-CM, HCPCS Level II and modifiers, NUCC taxonomy, licensed CPT: parsers for each
    publisher's file layout, and a store in the settings folder with a manifest of where each came from),
    `PlaceOfService` (built in, from CMS's page), `Updater` (downloads from CMS and NUCC, only when asked).
- `src/AVAClaimScrubber/`: the window, built in code (`MainWindow.cs`; `MainWindow.Parts.cs` holds the hooks later
  parts fill; `MainWindow.Rules.cs` the Rules tab and the packs). Settings (rules, code sets) live under
  `MainWindow.SettingsFolder`, which tests point at a temporary folder (`SettingsIsolation`).
- `tests/AVAClaimScrubber.Tests/`: xUnit v3 on Avalonia.Headless.
- `samples/`: invented claims, from `make_samples.py`; `samples/rules/` an invented payer's rule pack.

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
  loaded, and none is in the repository. `CLAIMS_LIVE_DOWNLOADS=1 dotnet test` checks the real downloads still work.
- **Dates are checked as of the batch's creation (BHT04)**, not today, so a file checks the same whenever it's checked.
- **No real claims in the repository.** Every person, member, provider, NPI and tax ID in the samples is invented.

## Making a change

- Run `dotnet test` after any change.
- Pushes to `main` publish builds to the rolling `latest` pre-release; a `v*` tag makes a proper release.
