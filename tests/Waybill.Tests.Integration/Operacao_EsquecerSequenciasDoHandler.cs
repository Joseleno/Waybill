using Waybill.EntityFrameworkCore;
using Waybill.Tests.Integration.G4;

namespace Waybill.Tests.Integration;

// OPERATIONS.md documents the SQL that forgets a handler's sequences, for a producer whose keys start again at 1 (its
// database rebuilt). This test runs that exact SQL: afterwards sequence 1 applies again for that handler, and another
// handler's sequences stay.
[Collection(PostgresCollection.Name)]
public sealed class Operacao_EsquecerSequenciasDoHandler(PostgresFixture postgres)
{
    [Fact]
    public async Task Operacao_SqlDoOperationsEsqueceSequencias_UmVoltaAAplicarEOutroHandlerFica()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = InboxOrderHarness.Services(database, out _);
        const string handler = "receipts.issue-receipt"; // the handler the documented SQL names
        await InboxOrderHarness.DeliverAsync(services, Guid.CreateVersion7(), 5, handler: handler);
        await InboxOrderHarness.DeliverAsync(services, Guid.CreateVersion7(), 5, handler: "audit.record");
        Assert.Equal(OrderedInboxResult.Regressed, await InboxOrderHarness.DeliverAsync(services, Guid.CreateVersion7(), 1, handler: handler));

        await database.ExecuteAsync(OperationsDoc.Sql("inbox-forget-keys"));

        Assert.Equal(OrderedInboxResult.Processed, await InboxOrderHarness.DeliverAsync(services, Guid.CreateVersion7(), 1, handler: handler));
        Assert.Equal(OrderedInboxResult.Regressed, await InboxOrderHarness.DeliverAsync(services, Guid.CreateVersion7(), 1, handler: "audit.record"));
    }
}
