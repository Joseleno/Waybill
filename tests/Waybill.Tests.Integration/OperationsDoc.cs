using System.Text.RegularExpressions;

namespace Waybill.Tests.Integration;

/// <summary>The SQL blocks of docs/OPERATIONS.md, by their <c>&lt;!-- marker --&gt;</c>, so tests run them exactly as written.</summary>
public static class OperationsDoc
{
    public static string Sql(string marker)
    {
        var text = File.ReadAllText(Path.Combine(FindRoot(), "docs", "OPERATIONS.md"));
        var match = Regex.Match(text, $@"<!-- {Regex.Escape(marker)} -->\s*```sql\r?\n(.*?)```", RegexOptions.Singleline);
        Assert.True(match.Success, $"OPERATIONS.md has no <!-- {marker} --> SQL block");
        return match.Groups[1].Value;
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Waybill.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException("Waybill.slnx not found above " + AppContext.BaseDirectory);
    }
}
