using AVAClaimScrubber.Core.Claims;

namespace AVAClaimScrubber.Tests;

/// <summary>The 837 read into its loops, claims, diagnoses and lines.</summary>
public sealed class ReaderTests
{
    [Fact]
    public void A_professional_batch_reads_into_its_loops_and_claims()
    {
        var b = Samples.Load("clinic-837p.x12");
        Assert.Equal(7, b.Claims.Count);
        var (_, root, kind) = Assert.Single(b.Transactions);
        Assert.Equal(ClaimKind.Professional, kind);
        Assert.Equal(["1000A", "1000B", "2000A"], root.Children.Select(c => c.Id));
        var provider = root.Children[2];
        Assert.Equal(["2010AA", "2000B", "2000B", "2000B", "2000B", "2000B", "2000B", "2000B"], provider.Children.Select(c => c.Id));
        Assert.Equal(["NM1", "N3", "N4", "REF"], provider.Children[0].Segments.Select(s => s.Id));
        Assert.Equal(["HL", "PRV"], provider.Segments.Select(s => s.Id));

        var c = b.Claim("CLEAN-01");
        Assert.Equal(1, c.Number);
        Assert.Equal(150.00m, c.Total);
        Assert.Equal("11", c.Facility);
        Assert.Equal("1", c.Frequency);
        Assert.Equal("1234567893", c.BillingNpi![9]);
        Assert.Equal("SUN01", c.Payer![9]);
        Assert.Equal("19700312", c.Demographics![2]);
        Assert.Equal(new DateOnly(2026, 11, 15), c.Created);
        Assert.Equal(["2010BA", "2010BB"], c.Subscriber!.Children.Where(x => x.Id != "2300").Select(x => x.Id));
        Assert.Equal(["2310B", "2400", "2400"], c.Loop.Children.Select(x => x.Id));
        var dx = Assert.Single(c.Diagnoses);
        Assert.Equal(("ABK", "I10"), (dx.Qualifier, dx.Code));
        Assert.Equal(2, c.Lines.Count);
        var line = c.Lines[1];
        Assert.Equal((2, "HC", "36415", 25.00m, 1m, "UN", "20261103"), (line.Number, line.ProcedureQualifier, line.Procedure, line.Charge, line.Units, line.UnitQualifier, line.DateText));
        Assert.Equal([1], line.Pointers);
    }

    [Fact]
    public void An_institutional_batch_reads_revenue_codes_and_its_attending_provider()
    {
        var b = Samples.Load("hospital-837i.x12");
        var c = b.Claim("INPT-CLEAN-01");
        Assert.Equal(ClaimKind.Institutional, c.Kind);
        Assert.Equal("11", c.Facility);
        Assert.Equal(["I214", "I10"], c.Diagnoses.Select(d => d.Code));
        Assert.Equal(["0120", "0300"], c.Lines.Select(l => l.RevenueCode));
        Assert.Equal("80053", c.Lines[1].Procedure);
        Assert.Equal(3m, c.Lines[0].Units);
        Assert.Contains(c.Loop.Children, l => l.Id == "2310A" && l.Name == "Attending provider");
    }

    /// <summary>X12 Explorer's sample 837, when it's on this machine: it reads as claims with lines.</summary>
    [Fact]
    public void Another_programs_sample_837_reads()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "RiderProjects", "X12_820Explorer", "tests", "X12Explorer.Tests", "Samples", "claim-837.edi");
        if (!File.Exists(path)) return;
        var b = ClaimReader.Load(path);
        Assert.NotEmpty(b.Claims);
        Assert.All(b.Claims, c => Assert.NotEmpty(c.Lines));
    }
}
