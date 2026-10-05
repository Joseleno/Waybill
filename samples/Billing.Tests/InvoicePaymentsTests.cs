using Microsoft.EntityFrameworkCore;
using Waybill;
using Waybill.Testing;

namespace Billing.Tests;

// The use case tested the way a user of Waybill would: an in-memory DbContext and FakeOutbox, no PostgreSQL, no broker.
public sealed class InvoicePaymentsTests : IDisposable
{
    private readonly DbContextOptions<BillingDbContext> _dbOptions = new DbContextOptionsBuilder<BillingDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options;

    private readonly BillingDbContext _db;

    private readonly FakeOutbox<BillingDbContext> _outbox;

    public InvoicePaymentsTests()
    {
        _db = new BillingDbContext(_dbOptions);
        var options = new WaybillOptions();
        BillingMessages.Register(options);
        _outbox = new FakeOutbox<BillingDbContext>(_db, options);
    }

    [Fact]
    public async Task Pay_OpenInvoice_EnqueuesInvoicePaid()
    {
        var invoice = await OpenInvoiceAsync(150.25m);

        var result = await new InvoicePayments(_db, _outbox).PayAsync(invoice.Id, TestContext.Current.CancellationToken);

        Assert.Equal(PaymentResult.Paid, result);
        var paid = _outbox.ShouldContain<InvoicePaid>(m => m.InvoiceId == invoice.Id);
        Assert.Equal(150.25m, paid.Amount);
        Assert.Equal(invoice.Id.ToString(), Assert.Single(_outbox.Saved).Key);
        Assert.Equal(InvoiceStatus.Paid, (await _db.Invoices.SingleAsync(TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task Pay_AlreadyPaidInvoice_DoesNotEnqueueAgain()
    {
        var invoice = await OpenInvoiceAsync(10m);
        var payments = new InvoicePayments(_db, _outbox);
        await payments.PayAsync(invoice.Id, TestContext.Current.CancellationToken);

        var second = await payments.PayAsync(invoice.Id, TestContext.Current.CancellationToken);

        Assert.Equal(PaymentResult.AlreadyPaid, second);
        Assert.Single(_outbox.Saved);
    }

    // Two concurrent payments: the second request read the invoice while it was still open. The status is a
    // concurrency token, so its update matches no row, nothing it enqueued is saved, and it reports AlreadyPaid.
    [Fact]
    public async Task Pay_ConcurrentPayment_OnlyOneEventIsSaved()
    {
        var invoice = await OpenInvoiceAsync(10m);
        var options = new WaybillOptions();
        BillingMessages.Register(options);
        await using var secondDb = new BillingDbContext(_dbOptions);
        using var secondOutbox = new FakeOutbox<BillingDbContext>(secondDb, options);
        await secondDb.Invoices.FindAsync([invoice.Id], TestContext.Current.CancellationToken); // read before the first pays

        var first = await new InvoicePayments(_db, _outbox).PayAsync(invoice.Id, TestContext.Current.CancellationToken);
        var second = await new InvoicePayments(secondDb, secondOutbox).PayAsync(invoice.Id, TestContext.Current.CancellationToken);

        Assert.Equal((PaymentResult.Paid, PaymentResult.AlreadyPaid), (first, second));
        Assert.Single(_outbox.Saved);
        secondOutbox.ShouldBeEmpty();
    }

    [Fact]
    public async Task Pay_MissingInvoice_EnqueuesNothing()
    {
        var result = await new InvoicePayments(_db, _outbox).PayAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.Equal(PaymentResult.NotFound, result);
        _outbox.ShouldBeEmpty();
    }

    // Forgetting to register a message type must say what to do, not fail somewhere in serialization.
    [Fact]
    public void Enqueue_UnregisteredType_ErrorSaysWhatToDo()
    {
        using var outbox = new FakeOutbox<BillingDbContext>(_db, new WaybillOptions { MaxPayloadBytes = 16 * 1024 }); // no AddMessage

        var error = Assert.ThrowsAny<Exception>(() => outbox.Enqueue(new InvoicePaid(Guid.NewGuid(), "INV-1", 1m, DateTimeOffset.UtcNow)));

        Assert.Contains(nameof(InvoicePaid), error.Message);
        Assert.Contains("AddMessage", error.Message);
    }

    private async Task<Invoice> OpenInvoiceAsync(decimal amount)
    {
        var invoice = new Invoice { Number = $"INV-{Random.Shared.Next(1000, 9999)}", Amount = amount };
        _db.Invoices.Add(invoice);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return invoice;
    }

    public void Dispose()
    {
        _outbox.Dispose();
        _db.Dispose();
    }
}
