using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Waybill.EntityFrameworkCore;

namespace Waybill.Tests.Integration;

[JsonSerializable(typeof(InvoicePaid))]
internal sealed partial class AppJson : JsonSerializerContext;

// The README shows the API in ten lines. This test runs those lines against a real database, and fails when the
// README shows a line that is not here (or in the test model), so the README cannot drift from code that compiles.
[Collection(PostgresCollection.Name)]
public sealed partial class ReadmeTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Readme_ApiEmDezLinhas_Compila()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var connectionString = database.ConnectionString;
        var rabbitUri = new Uri("amqp://localhost");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));

#pragma warning disable xUnit1051 // shown as the README shows it, where a startup step has no test token
        await WaybillSchema.MigrateAsync(connectionString);
#pragma warning restore xUnit1051
        services.AddWaybill(o =>
        {
            o.MaxPayloadBytes = 16 * 1024;
            o.AddMessage("billing.invoice-paid.v1", AppJson.Default.InvoicePaid);
        });
        services.AddWaybillOutbox<AppDbContext>();
        services.AddWaybillRabbitMQ(o => { o.Uri = rabbitUri; o.Exchange = "events"; });
        services.AddWaybillDispatcher<AppDbContext>();

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>();
        var invoice = new Invoice { Number = "INV-1", Amount = 10m };
        db.Invoices.Add(invoice);

        outbox.Enqueue(new InvoicePaid(invoice.Id, invoice.Amount), key: invoice.Id.ToString());
        await db.SaveChangesAsync(ct);

        Assert.Equal(1, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox"));

        var root = FindRoot();
        var snippet = ApiSnippet().Match(File.ReadAllText(Path.Combine(root, "README.md")));
        Assert.True(snippet.Success, "README.md has no <!-- api --> code block");
        var code = snippet.Groups[1].Value.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("//", StringComparison.Ordinal))
            .ToArray();
        var sources = File.ReadAllText(Path.Combine(root, "tests/Waybill.Tests.Integration/ReadmeTests.cs"))
            + File.ReadAllText(Path.Combine(root, "tests/Waybill.Tests.Integration/TestModel.cs"));
        var known = sources.Split('\n').Select(line => line.Trim()).ToHashSet();

        var missing = code.Where(line => !known.Contains(StripComment(line))).ToArray();
        Assert.Empty(missing);
        Assert.True(code.Count(line => line is not ("{" or "}" or "});")) <= 10, "The README API takes more than ten lines");
    }

    // A trailing "// comment" in the README explains the line; the code before it is what must exist.
    private static string StripComment(string line) => TrailingComment().Replace(line, "").TrimEnd();

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Waybill.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException("Waybill.slnx not found above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"<!-- api -->\s*```csharp\r?\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex ApiSnippet();

    [GeneratedRegex(@"\s+//.*$")]
    private static partial Regex TrailingComment();
}
