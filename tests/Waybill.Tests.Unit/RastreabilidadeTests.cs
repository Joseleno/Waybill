using System.Text.RegularExpressions;

namespace Waybill.Tests.Unit;

// GUARANTEES.md promises only what a named test proves. These tests keep the promise and the tests in
// step: a renamed or deleted test, or a link to the wrong file, fails the build instead of rotting the text.
public sealed partial class RastreabilidadeTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void Garantias_CadaTesteCitadoExiste()
    {
        var markdown = File.ReadAllText(Path.Combine(Root, "GUARANTEES.md"));
        var citations = Citations(markdown);

        var missing = citations
            .Where(c => !File.Exists(Path.Combine(Root, c.Path))
                || !Regex.IsMatch(File.ReadAllText(Path.Combine(Root, c.Path)), $@"\b(void|Task)\s+{c.Test}\s*\("))
            .Select(c => $"{c.Test} -> {c.Path}")
            .ToArray();
        // A test named without a link to its file cannot be checked, so it is not allowed.
        var unlinked = QuotedTestName().Matches(markdown).Select(m => m.Groups[1].Value)
            .Except(citations.Select(c => c.Test))
            .ToArray();

        Assert.NotEmpty(citations);
        Assert.Empty(missing);
        Assert.Empty(unlinked);
    }

    [Fact]
    public void Garantias_CadaGarantiaTemTeste()
    {
        var markdown = File.ReadAllText(Path.Combine(Root, "GUARANTEES.md"));
        var sections = GuaranteeHeading().Split(markdown);
        var ids = GuaranteeHeading().Matches(markdown).Select(m => m.Groups[1].Value).ToArray();

        Assert.Equal(["G1", "G2", "G3"], ids);
        for (var i = 0; i < ids.Length; i++)
        {
            // Split with one capture group yields: preamble, id, body, id, body...
            var body = sections[2 + (i * 2)];
            // Conditions may cite other tests (retention, enqueue validation); the guarantee itself needs its own.
            var guaranteeTests = Citations(body).Select(c => c.Test).Where(t => TestName().IsMatch(t)).ToArray();
            Assert.True(guaranteeTests.Length > 0, $"{ids[i]} cites no {ids[i]}_ test");
            Assert.All(guaranteeTests, t => Assert.StartsWith(ids[i] + "_", t, StringComparison.Ordinal));
        }
    }

    private static (string Test, string Path)[] Citations(string markdown) =>
        TestLink().Matches(markdown).Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToArray();

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Waybill.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException("Waybill.slnx not found above " + AppContext.BaseDirectory);
    }

    // [`G1_Name`](tests/path/File.cs)
    [GeneratedRegex(@"\[`(\w+)`\]\(([^)#]+\.cs)\)")]
    private static partial Regex TestLink();

    [GeneratedRegex(@"^G\d+_\w+$")]
    private static partial Regex TestName();

    // `G1_Name`, linked or not; file names inside link targets are not quoted, so they do not count.
    [GeneratedRegex(@"`(G\d+_\w+)`")]
    private static partial Regex QuotedTestName();

    // ## G1. The event exists...
    [GeneratedRegex(@"^## (G\d+)\. ", RegexOptions.Multiline)]
    private static partial Regex GuaranteeHeading();
}
