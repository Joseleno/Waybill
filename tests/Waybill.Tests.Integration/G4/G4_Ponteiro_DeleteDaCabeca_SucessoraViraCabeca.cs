namespace Waybill.Tests.Integration.G4;

// OPERATIONS.md says release, never delete; people delete anyway, often as "cleaning the DLQ". Before the head pointer,
// deleting a stopped head let its key move on, the claim finding the next row by itself. With the pointer, the next row
// is claimable only once marked, so a delete must mark it as a release does, or the key stops for good.
[Collection(PostgresCollection.Name)]
public sealed class G4_Ponteiro_DeleteDaCabeca_SucessoraViraCabeca(PostgresFixture postgres)
{
    [Fact]
    public async Task G4_Ponteiro_DeleteDaCabecaNaDlq_ChaveSegueEmOrdem()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scenario = await DlqScenario.StartAsync(postgres);
        await scenario.Dispatcher.RunOnceAsync(ct);
        Assert.Equal("1:dlq,2:pending,3:pending", await scenario.StatusesAsync("order-42"));

        await scenario.Database.ExecuteAsync("DELETE FROM waybill.outbox WHERE key = 'order-42' AND status = 'dlq'");
        scenario.Fix();
        for (var i = 0; i < 3; i++)
            await scenario.Dispatcher.RunOnceAsync(ct);

        Assert.Equal("2:published,3:published", await scenario.StatusesAsync("order-42"));
    }
}
