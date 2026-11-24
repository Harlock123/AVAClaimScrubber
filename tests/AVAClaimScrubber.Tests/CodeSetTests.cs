using AVAClaimScrubber.Core.Checks;
using AVAClaimScrubber.Core.CodeSets;

namespace AVAClaimScrubber.Tests;

/// <summary>The code sets (read from excerpts of CMS's and NUCC's real files) and layer 4's checks against them.</summary>
public sealed class CodeSetTests
{
    [Fact]
    public void CMS_and_NUCC_files_read_as_their_layouts_say()
    {
        var sets = Samples.CodeSets;
        Assert.True(sets.Icd!["E119"].Billable);
        Assert.False(sets.Icd["E11"].Billable);
        Assert.Equal("Type 2 diabetes mellitus without complications", sets.Icd["E119"].Title);
        var ended = sets.Hcpcs!["G9975"];
        Assert.Equal(new DateOnly(2024, 12, 31), ended.Terminated);
        Assert.Null(sets.Hcpcs["G0439"].Terminated);
        Assert.Equal(new DateOnly(2011, 1, 1), sets.Hcpcs["G0439"].Added);
        Assert.Contains("GT", sets.HcpcsModifiers!);
        Assert.DoesNotContain("X9", sets.HcpcsModifiers!);
        Assert.Equal("Family Medicine Physician", sets.Taxonomy!["207Q00000X"]);
        Assert.Equal(52, PlaceOfService.Codes.Count);
        Assert.Equal(["HCPCS Level II", "ICD-10-CM", "MUE (practitioner)", "NCCI PTP edits", "NUCC provider taxonomy"], sets.Info.Select(i => i.Name).Order());
        Assert.Equal(new MueEdit("G0439", 1, 2, "Code Descriptor / CPT Instruction"), sets.MuePractitioner!["G0439"]);
        Assert.Equal('0', sets.Ptp!["G0438|G0439"].Modifier);
        Assert.Throws<FormatException>(() => CodeSets.ParseIcd("not an order file"));
    }

    [Fact]
    public void Each_planted_coding_problem_is_found_and_the_clean_claim_passes()
    {
        var r = Samples.Scrub("coding-errors.x12");
        Assert.Equal(
        [
            "C1 NOTACODE-03",
            "C2 HEADER-02",
            "C3 ENDED-05 line 1",
            "C4 CPT-09 line 1",
            "C5 MODIFIER-06 line 1",
            "C6 POS-07",
            "C7 NPI-04",
            "C8 TAXONOMY-08",
        ], r.Pinned());
        Assert.Equal("Diagnosis 1 (E11, Type 2 diabetes mellitus) is a header code, not billable; billable codes under it include E1110, E1121, E1122, E1165.",
            r.Findings.Single(f => f.Rule.Id == "C2").Message);
        Assert.Equal("Line 1's procedure G9975 (Doc med rsn no dil mac exam) ended 672 days before the service.", r.Findings.Single(f => f.Rule.Id == "C3").Message);
        Assert.Equal("The rendering provider's NPI (1234567894) fails its check digit.", r.Findings.Single(f => f.Rule.Id == "C7").Message);
        Assert.Equal("The claim's place of service 30 isn't one of CMS's codes (it's an unassigned number).", r.Findings.Single(f => f.Rule.Id == "C6").Message);
    }

    [Fact]
    public void Without_code_sets_their_codes_go_unjudged_and_that_is_said_once()
    {
        var r = Samples.Scrub("coding-errors.x12", new ScrubSettings());
        var info = Assert.Single(r.Findings, f => f.Rule.Id == "C9");
        Assert.Equal(Severity.Info, info.Severity);
        Assert.Equal("ICD-10-CM, HCPCS Level II, the NUCC taxonomy aren't loaded, so their codes weren't checked.", info.Message);
        // Shapes are still checked: CPT shape, place of service, NPI check digits, taxonomy shape.
        Assert.Equal(["C4", "C6", "C7", "C8", "C9"], r.Findings.Select(f => f.Rule.Id).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void NPIs_and_fiscal_years_are_worked_out_right()
    {
        Assert.True(CodeSetChecks.IsNpi("1234567893"));
        Assert.False(CodeSetChecks.IsNpi("1234567894"));
        Assert.False(CodeSetChecks.IsNpi("123456789"));
        Assert.Equal(2027, Updater.FiscalYear(new DateOnly(2026, 10, 1)));
        Assert.Equal(2026, Updater.FiscalYear(new DateOnly(2026, 9, 30)));
        Assert.True(CodeSetChecks.IsCptShape("0001F"));
        Assert.False(CodeSetChecks.IsCptShape("A0425"));
    }

    /// <summary>Downloads the real code sets (CLAIMS_LIVE_DOWNLOADS=1), and checks the samples against them. Off by default: it uses the network.</summary>
    [Fact]
    public void The_real_code_sets_download_and_check_the_samples()
    {
        if (Environment.GetEnvironmentVariable("CLAIMS_LIVE_DOWNLOADS") != "1") return;
        var folder = Directory.CreateTempSubdirectory("claims-live-").FullName;
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AVAClaimScrubber/1.0");
        var said = Updater.UpdateAll(folder, http, new DateOnly(2026, 11, 24)).GetAwaiter().GetResult();
        Assert.All(said, s => Assert.DoesNotContain("couldn't", s));
        var sets = CodeSets.Load(folder);
        Assert.True(sets.Icd!.Count > 70_000);
        Assert.True(sets.Hcpcs!.Count > 5_000);
        Assert.True(sets.Taxonomy!.Count > 800);
        Assert.Equal(
            Samples.Scrub("coding-errors.x12").Pinned(),
            Samples.Scrub("coding-errors.x12", new ScrubSettings { CodeSets = sets }).Pinned());
    }
}
