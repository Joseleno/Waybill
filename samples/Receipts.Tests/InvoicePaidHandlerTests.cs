using Microsoft.EntityFrameworkCore;
using Waybill;
using Waybill.EntityFrameworkCore;
using Waybill.Testing;

namespace Receipts.Tests;

// The consumer's handler tested without PostgreSQL or RabbitMQ: FakeInbox stands in for waybill.inbox and FakeOutbox
// for the event the handler publishes in turn.
public sealed class InvoicePaidHandlerTests : IDisposable
{
    private readonly ReceiptsDbContext _db = new(new DbContextOptionsBuilder<ReceiptsDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);

    private readonly FakeOutbox<ReceiptsDbContext> _outbox;
    private readonly FakeInbox<ReceiptsDbContext> _inbox;

    public InvoicePaidHandlerTests()
    {
        var options = new WaybillOptions();
        ReceiptsMessages.Register(options);
        _outbox = new FakeOutbox<ReceiptsDbContext>(_db, options);
        _inbox = new FakeInbox<ReceiptsDbContext>(_db);
    }

    [Fact]
    public async Task EmitirRecibo_PrimeiraEntrega_GravaReciboEEnfileira()
    {
        var paid = new InvoicePaid(Guid.NewGuid(), "INV-1001", 99.90m, DateTimeOffset.UtcNow);
        var messageId = Guid.CreateVersion7();

        var result = await Handler().HandleAsync(messageId, paid, TestContext.Current.CancellationToken);

        Assert.Equal(InboxResult.Processed, result);
        var receipt = await _db.Receipts.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal((paid.InvoiceId, 99.90m), (receipt.InvoiceId, receipt.Amount));
        var issued = _outbox.ShouldContain<ReceiptIssued>();
        Assert.Equal((receipt.Id, paid.InvoiceId), (issued.ReceiptId, issued.InvoiceId));
        _inbox.Memory.ShouldHaveProcessed(InvoicePaidHandler.Name, messageId);
    }

    [Fact]
    public async Task EmitirRecibo_EntregaRepetida_UmRecibo()
    {
        var paid = new InvoicePaid(Guid.NewGuid(), "INV-1002", 10m, DateTimeOffset.UtcNow);
        var messageId = Guid.CreateVersion7();
        var handler = Handler();
        await handler.HandleAsync(messageId, paid, TestContext.Current.CancellationToken);

        var again = await handler.HandleAsync(messageId, paid, TestContext.Current.CancellationToken);

        Assert.Equal(InboxResult.Duplicate, again);
        Assert.Equal(1, await _db.Receipts.CountAsync(TestContext.Current.CancellationToken));
        Assert.Single(_outbox.Saved);
    }

    private InvoicePaidHandler Handler() => new(_inbox, new ReceiptIssuer(_outbox));

    public void Dispose()
    {
        _outbox.Dispose();
        _db.Dispose();
    }
}
