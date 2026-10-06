using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.Tests.Integration.Retencao;

namespace Waybill.Tests.Integration.Observabilidade;

// With nothing waiting to be published the gauge reads 0, in seconds; published and dead-lettered rows do not count
// (the DLQ is watched on its own, see OPERATIONS.md).
[Collection(PostgresCollection.Name)]
public sealed class Metrica_SemPendentes_Zero(PostgresFixture postgres)
{
    [Fact]
    public async Task Metrica_SoPublicadasEDlq_GaugeEmZeroSegundos()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await database.InsertOutboxAsync("published", createdAgo: TimeSpan.FromHours(1), publishedAgo: TimeSpan.FromHours(1));
        await database.InsertOutboxAsync("dlq", createdAgo: TimeSpan.FromHours(1));
        await using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = services.GetRequiredService<IMeterFactory>();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        using var metrics = new OutboxMetrics(factory);
        var sampler = new OldestPendingSampler(new OutboxStore(dataSource), metrics);

        await sampler.SampleAsync(ct);

        Assert.Equal((0d, "s"), GaugeReader.Read(factory));
    }
}
