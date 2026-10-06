using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;

namespace Waybill.Tests.Integration.G4;

// A rolling deploy starts several ordering dispatchers at once. Only one creates the settings; the others wait on its
// INSERT and then must read what it wrote, even though their statement's snapshot predates its commit (ADR 0007).
[Collection(PostgresCollection.Name)]
public sealed class G4_InstanciasSobemJuntas_TodasLeemAConfiguracao(PostgresFixture postgres)
{
    private static readonly OrderingSettings Settings = new(8, TimeSpan.FromSeconds(60));

    [Fact]
    public async Task G4_InstanciasSobemJuntas_TodasLeemAConfiguracao_NenhumaFalha()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var round = 0; round < 5; round++)
        {
            var database = await TestDatabase.CreateAsync(postgres);
            await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
            using var start = new ManualResetEventSlim();
            var instances = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
            {
                start.Wait(ct);
                return await new PartitionStore(dataSource).EnsureSettingsAsync(Settings, ct);
            }, ct)).ToList();
            start.Set();

            Assert.All(await Task.WhenAll(instances), stored => Assert.Equal(Settings, stored));
            Assert.Equal(8, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox_partitions"));
        }
    }
}
