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
    public async Task IssueReceipt_FirstDelivery_SavesReceiptAndEnqueues()
    {
        var paid = new InvoicePaid(Guid.NewGuid(), "INV-1001", 99.90m, DateTimeOffset.UtcNow);
        var messageId = Guid.CreateVersion7();

        var result = await Handler().HandleAsync(messageId, Order(1), paid, TestContext.Current.CancellationToken);

        Assert.Equal(OrderedInboxResult.Processed, result);
        var receipt = await _db.Receipts.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal((paid.InvoiceId, 99.90m), (receipt.InvoiceId, receipt.Amount));
        var issued = _outbox.ShouldContain<ReceiptIssued>();
        Assert.Equal((receipt.Id, paid.InvoiceId), (issued.ReceiptId, issued.InvoiceId));
        _inbox.Memory.ShouldHaveProcessed(InvoicePaidHandler.Name, messageId);
    }

    [Fact]
    public async Task IssueReceipt_RepeatedDelivery_OneReceipt()
    {
        var paid = new InvoicePaid(Guid.NewGuid(), "INV-1002", 10m, DateTimeOffset.UtcNow);
        var messageId = Guid.CreateVersion7();
        var handler = Handler();
        await handler.HandleAsync(messageId, Order(1), paid, TestContext.Current.CancellationToken);

        var again = await handler.HandleAsync(messageId, Order(1), paid, TestContext.Current.CancellationToken);

        Assert.Equal(OrderedInboxResult.Duplicate, again);
        Assert.Equal(1, await _db.Receipts.CountAsync(TestContext.Current.CancellationToken));
        Assert.Single(_outbox.Saved);
    }

    [Fact]
    public async Task IssueReceipt_OlderThanTheLastApplied_RegressedAndNoReceipt()
    {
        var invoice = Guid.NewGuid();
        var handler = Handler();
        await handler.HandleAsync(Guid.CreateVersion7(), Order(2, invoice), new InvoicePaid(invoice, "INV-1003", 10m, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);

        var late = await handler.HandleAsync(Guid.CreateVersion7(), Order(1, invoice), new InvoicePaid(invoice, "INV-1003", 10m, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);

        Assert.Equal(OrderedInboxResult.Regressed, late);
        Assert.Equal(1, await _db.Receipts.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, _inbox.Memory.Regressions);
    }

    // Billing keys each invoice's messages by its id; the sequence is the message's place among them.
    private static MessageOrder Order(long sequence, Guid? invoice = null) => new((invoice ?? Guid.NewGuid()).ToString(), sequence);

    private InvoicePaidHandler Handler() => new(_inbox, new ReceiptIssuer(_outbox));

    public void Dispose()
    {
        _outbox.Dispose();
        _db.Dispose();
    }
}
