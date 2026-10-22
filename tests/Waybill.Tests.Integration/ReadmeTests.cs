using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Waybill.EntityFrameworkCore;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.RabbitMQ;

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
        services.AddWaybillRabbitMQ(o => { o.Uri = new Uri("amqp://localhost"); o.Exchange = "events"; });
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

        var code = SnippetLines(ApiSnippet(), "api");
        Assert.Empty(MissingFromTests(code));
        // Ten lines of Waybill: braces, usings and the System.Text.Json context of the messages do not count.
        var counted = code.Count(line => line is not ("{" or "}" or "});")
            && !line.StartsWith("using ", StringComparison.Ordinal)
            && !line.StartsWith("[JsonSerializable", StringComparison.Ordinal)
            && !line.Contains(": JsonSerializerContext", StringComparison.Ordinal));
        Assert.True(counted <= 10, $"The README API takes {counted} lines, more than ten");
    }

    // The README's "Ordering by key" section. Every line must be in this file (the Enqueue line runs in the test above).
    // The dispatcher line is registered and read back; the consumer line runs against a real database, on deliveries
    // built as RabbitMQ.Client hands them over, and the one older than the applied one comes back Regressed.
    [Fact]
    public async Task Readme_Ordenacao_Compila()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var connectionString = database.ConnectionString;
        await WaybillSchema.MigrateAsync(connectionString, ct);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));
        services.AddWaybill(o =>
        {
            o.MaxPayloadBytes = 16 * 1024;
            o.AddMessage("billing.invoice-paid.v1", AppJson.Default.InvoicePaid);
        });
        services.AddWaybillOutbox<AppDbContext>();
        services.AddWaybillInbox<AppDbContext>();
        services.AddWaybillRabbitMQ(o => { o.Uri = new Uri("amqp://localhost"); o.Exchange = "events"; });
        services.AddWaybillDispatcher<AppDbContext>(o => o.OrderByKey = true);

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        Assert.True(provider.GetRequiredService<IOptions<WaybillDispatcherOptions>>().Value.OrderByKey);
        var invoiceId = Guid.NewGuid();
        var results = new List<OrderedInboxResult>();
        foreach (var args in new[] { Delivery(invoiceId, 2), Delivery(invoiceId, 1) })
        {
            await using var scope = provider.CreateAsyncScope();
            var inbox = scope.ServiceProvider.GetRequiredService<IInbox<AppDbContext>>();

            var result = await inbox.ProcessInOrderAsync("receipts", args.BasicProperties.GetWaybillMessageId(),
                args.BasicProperties.GetWaybillOrder(), (db, ct) => ApplyAsync(db, args.Body, ct), ct);
            results.Add(result);
        }

        Assert.Equal([OrderedInboxResult.Processed, OrderedInboxResult.Regressed], results);
        Assert.Equal(1, await database.ScalarAsync("SELECT count(*) FROM invoices"));
        Assert.Empty(MissingFromTests(SnippetLines(OrderingSnippet(), "ordering")));
    }

    // A delivery as RabbitMQ.Client hands it to a consumer, with the headers the Waybill transport writes: header strings
    // arrive from the broker as bytes.
    private static BasicDeliverEventArgs Delivery(Guid invoiceId, long sequence)
    {
        var properties = new BasicProperties
        {
            MessageId = Guid.CreateVersion7().ToString(),
            Headers = new Dictionary<string, object?>
            {
                ["waybill-key"] = Encoding.UTF8.GetBytes(invoiceId.ToString()),
                ["waybill-sequence"] = Encoding.UTF8.GetBytes(sequence.ToString(CultureInfo.InvariantCulture)),
            },
        };
        var body = JsonSerializer.SerializeToUtf8Bytes(new InvoicePaid(invoiceId, sequence), AppJson.Default.InvoicePaid);
        return new BasicDeliverEventArgs("consumer", (ulong)sequence, false, "events", "billing.invoice-paid.v1", properties, body);
    }

    private static Task ApplyAsync(AppDbContext db, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        var paid = JsonSerializer.Deserialize(body.Span, AppJson.Default.InvoicePaid)!;
        db.Invoices.Add(new Invoice { Number = "INV-" + paid.Amount.ToString(CultureInfo.InvariantCulture), Amount = paid.Amount });
        return Task.CompletedTask;
    }

    private static string[] SnippetLines(Regex block, string name)
    {
        var snippet = block.Match(File.ReadAllText(Path.Combine(FindRoot(), "README.md")));
        Assert.True(snippet.Success, $"README.md has no <!-- {name} --> code block");
        return snippet.Groups[1].Value.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("//", StringComparison.Ordinal))
            .ToArray();
    }

    // The README lines that appear in no line of this file or of the test model.
    private static string[] MissingFromTests(string[] code)
    {
        var root = FindRoot();
        var sources = File.ReadAllText(Path.Combine(root, "tests/Waybill.Tests.Integration/ReadmeTests.cs"))
            + File.ReadAllText(Path.Combine(root, "tests/Waybill.Tests.Integration/TestModel.cs"));
        var known = sources.Split('\n').Select(line => line.Trim()).ToHashSet();
        return code.Where(line => !known.Contains(StripComment(line))).ToArray();
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

    [GeneratedRegex(@"<!-- ordering -->\s*```csharp\r?\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex OrderingSnippet();

    [GeneratedRegex(@"\s+//.*$")]
    private static partial Regex TrailingComment();
}
