using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Waybill.EntityFrameworkCore;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Integration.G4;

// Turning ordering on, rows written before it, a disabled numbering trigger, and a partition changing hands with a head
// in flight (ADR 0008).
[Collection(PostgresCollection.Name)]
public sealed class G4_TransicoesDaOrdenacao(PostgresFixture postgres)
{
    // A transaction whose snapshot predates waybill.settings does not see it: its keyed message is written unnumbered and
    // is not ordered, as OPERATIONS.md says. Nothing raises an alarm for it.
    [Fact]
    public async Task G4_LigarComTransacaoAberta_LinhaSemSequenceNaoOrdenada()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = database.Services();
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await context.Invoices.CountAsync(ct); // snapshot taken before ordering is on

        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var transport = new FakeTransport();
        var dispatcher = OrderingHarness.Create(dataSource, transport, OrderingHarness.Options(database, partitions: 4));
        Assert.Null(await dispatcher.CheckOrderingAsync(atStartup: true, ct));
        scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>().Enqueue(new InvoicePaid(Guid.NewGuid(), 1m), "order-42");
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        Assert.Equal(1, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE key = 'order-42' AND sequence IS NULL"));
        await dispatcher.RunOnceAsync(ct);
        Assert.Single(transport.Received);
        Assert.Null(await dispatcher.CheckOrderingAsync(atStartup: false, ct));
    }

    // Rows written with ordering off have no sequence and are not ordered: they drain as before. Order holds from the
    // first numbered message on.
    [Fact]
    public async Task G4_OrdenacaoLigadaComBacklog_OrdemValeAPartirDaPrimeiraSequence()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await DispatcherHarness.EnqueueAsync(database, 3, key: _ => "order-42"); // ordering off: no sequence
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var transport = new FakeTransport();
        var dispatcher = OrderingHarness.Create(dataSource, transport, OrderingHarness.Options(database, partitions: 4));
        Assert.Null(await dispatcher.CheckOrderingAsync(atStartup: true, ct));
        await DispatcherHarness.EnqueueAsync(database, 2, key: _ => "order-42"); // sequences 1 and 2

        await dispatcher.RunOnceAsync(ct); // the old three, and the head of the numbered ones
        await dispatcher.RunOnceAsync(ct);

        var sent = new List<long>();
        foreach (var message in transport.Received)
            sent.Add(await database.ScalarAsync($"SELECT coalesce(sequence, 0) FROM waybill.outbox WHERE id = '{message.MessageId}'"));
        Assert.Equal(5, sent.Count);
        Assert.Equal(3, sent.Count(s => s == 0)); // the unnumbered ones drained
        Assert.True(sent.IndexOf(1) < sent.IndexOf(2), "sequence 2 went out before sequence 1");
        Assert.Equal(5, await database.CountAsync("published"));
    }

    [Fact]
    public async Task G4_TriggerDesligado_ErroCritico()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var dispatcher = OrderingHarness.Create(dataSource, new FakeTransport(), OrderingHarness.Options(database, partitions: 4));
        Assert.Null(await dispatcher.CheckOrderingAsync(atStartup: true, ct));

        await database.ExecuteAsync("ALTER TABLE waybill.outbox DISABLE TRIGGER outbox_sequence");
        Assert.Contains("outbox_sequence", await dispatcher.CheckOrderingAsync(atStartup: false, ct));
        Assert.Contains("ENABLE TRIGGER", await dispatcher.CheckOrderingAsync(atStartup: true, ct));

        await database.ExecuteAsync("ALTER TABLE waybill.outbox ENABLE TRIGGER outbox_sequence");
        Assert.Null(await dispatcher.CheckOrderingAsync(atStartup: false, ct));
    }

    // The old owner claimed the head and paused; the partition lease is forced to run out before the row's, and another
    // instance takes the partition. Nothing of the key moves while the head's lease is valid; then it all goes, in order.
    [Fact]
    public async Task G4_TrocaDeDonoComLinhasEmVoo_ChaveEsperaOLeaseDasLinhas()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var old = OrderingHarness.Create(dataSource, new FakeTransport(), OrderingHarness.Options(database, partitions: 1));
        Assert.Null(await old.CheckOrderingAsync(atStartup: true, ct));
        await old.RunOnceAsync(ct); // holds the only partition
        await DispatcherHarness.EnqueueAsync(database, 2, key: _ => "order-42");
        var inFlight = await new OutboxStore(dataSource).ClaimAsync(old.Owner, 10, TimeSpan.FromSeconds(30), ct, partitions: 1);
        Assert.Equal([1L], inFlight.Select(c => c.Sequence!.Value)); // the head, then the old owner pauses
        await database.ExecuteAsync("UPDATE waybill.outbox_partitions SET lease_until = clock_timestamp() - interval '1 second'");

        var transport = new FakeTransport();
        var successor = OrderingHarness.Create(dataSource, transport, OrderingHarness.Options(database, partitions: 1));
        Assert.Null(await successor.CheckOrderingAsync(atStartup: true, ct));
        await successor.RunOnceAsync(ct);
        Assert.Equal([0], successor.HeldPartitions.Keys);
        Assert.Empty(transport.Received);

        await database.ExecuteAsync("UPDATE waybill.outbox SET lease_until = clock_timestamp() - interval '1 second' WHERE status = 'claimed'");
        await successor.RunOnceAsync(ct);
        await successor.RunOnceAsync(ct);
        var sent = new List<long>();
        foreach (var message in transport.Received)
            sent.Add(await database.ScalarAsync($"SELECT sequence FROM waybill.outbox WHERE id = '{message.MessageId}'"));
        Assert.Equal([1L, 2L], sent);
    }
}
