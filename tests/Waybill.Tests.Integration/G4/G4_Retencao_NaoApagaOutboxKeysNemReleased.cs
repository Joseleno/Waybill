using Npgsql;
using Waybill.Tests.Integration.Retencao;

namespace Waybill.Tests.Integration.G4;

// Retention deletes published rows past the retention, and nothing else of ordering (ADR 0008): the per-key counters
// stay (a key starting again at 1 would look like a regression), and so do released rows, the record of a release.
[Collection(PostgresCollection.Name)]
public sealed class G4_Retencao_NaoApagaOutboxKeysNemReleased(PostgresFixture postgres)
{
    [Fact]
    public async Task G4_Retencao_ApagaPublicadaAntigaEMantemContadoresELiberadas()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        // Retention walks ids by the time inside them (UUIDv7, ADR 0004): ids from 30 days ago, as the client made them.
        var published = Guid.CreateVersion7(DateTimeOffset.UtcNow.AddDays(-30));
        var released = Guid.CreateVersion7(DateTimeOffset.UtcNow.AddDays(-30));
        await database.ExecuteAsync($"""
            INSERT INTO waybill.outbox_keys VALUES ('order-42', 2);
            INSERT INTO waybill.outbox (id, type, key, key_hash, sequence, payload, content_type, status, created_at, published_at)
            VALUES ('{published}', 'billing.invoice-paid.v1', 'order-42', 0, 2, '\x00', 'application/json', 'published',
                    clock_timestamp() - interval '30 days', clock_timestamp() - interval '30 days');
            INSERT INTO waybill.outbox (id, type, key, key_hash, sequence, payload, content_type, status, created_at, dlq_reason, released_at, released_by)
            VALUES ('{released}', 'billing.invoice-paid.v1', 'order-42', 0, 1, '\x00', 'application/json', 'released',
                    clock_timestamp() - interval '30 days', 'defect', clock_timestamp() - interval '29 days', 'ops');
            """);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var cleaner = RetentionHarness.Create(dataSource, RetentionHarness.Options(database, o => o.OutboxRetention = TimeSpan.FromDays(7)));

        var pass = await cleaner.RunOnceAsync(ct);

        Assert.Equal(1, pass.OutboxDeleted);
        Assert.Equal(1, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE status = 'released'"));
        Assert.Equal(2, await database.ScalarAsync("SELECT seq FROM waybill.outbox_keys WHERE key = 'order-42'"));
    }
}
