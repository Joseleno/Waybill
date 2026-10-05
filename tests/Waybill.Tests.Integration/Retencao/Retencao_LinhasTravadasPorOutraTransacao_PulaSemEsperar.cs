using Npgsql;

namespace Waybill.Tests.Integration.Retencao;

// A cleaner stuck mid-batch (or any transaction holding the rows) must not stall the others: SKIP LOCKED takes the
// rows nobody holds and leaves the held ones to the next pass.
[Collection(PostgresCollection.Name)]
public sealed class Retencao_LinhasTravadasPorOutraTransacao_PulaSemEsperar(PostgresFixture postgres)
{
    [Fact]
    public async Task Retencao_LinhasTravadas_ApagaAsOutrasSemEsperarEAsTravadasNaProximaPassada()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await database.InsertPublishedOutboxBulkAsync(30, TimeSpan.FromDays(40));
        await database.InsertInboxBulkAsync(30, TimeSpan.FromDays(40));
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var cleaner = RetentionHarness.Create(dataSource, RetentionHarness.Options(database, o => o.BatchSize = 5));

        await using (var holder = await dataSource.OpenConnectionAsync(ct))
        await using (var transaction = await holder.BeginTransactionAsync(ct))
        {
            await using (var hold = new NpgsqlCommand("""
                SELECT id FROM waybill.outbox ORDER BY id LIMIT 7 FOR UPDATE;
                SELECT message_id FROM waybill.inbox ORDER BY processed_at LIMIT 7 FOR UPDATE;
                """, holder, transaction))
                await hold.ExecuteNonQueryAsync(ct);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var pass = await cleaner.RunOnceAsync(deadline.Token);

            Assert.Equal((23, 23), (pass.OutboxDeleted, pass.InboxDeleted));
            await transaction.CommitAsync(ct);
        }

        var next = await cleaner.RunOnceAsync(ct);

        Assert.Equal((7, 7), (next.OutboxDeleted, next.InboxDeleted));
        Assert.Equal(0, await database.OutboxCountAsync());
    }
}
