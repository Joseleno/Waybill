using Microsoft.EntityFrameworkCore;
using Waybill;
using Waybill.Testing;

namespace Billing.Tests;

// The use case tested the way a user of Waybill would: an in-memory DbContext and FakeOutbox, no PostgreSQL, no broker.
public sealed class InvoicePaymentsTests : IDisposable
{
    private readonly BillingDbContext _db = new(new DbContextOptionsBuilder<BillingDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);

    private readonly FakeOutbox<BillingDbContext> _outbox;

    public InvoicePaymentsTests()
    {
        var options = new WaybillOptions();
        BillingMessages.Register(options);
        _outbox = new FakeOutbox<BillingDbContext>(_db, options);
    }

    [Fact]
    public async Task Pagar_FaturaAberta_EnfileiraInvoicePaid()
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
    public async Task Pagar_FaturaJaPaga_NaoEnfileiraDeNovo()
    {
        var invoice = await OpenInvoiceAsync(10m);
        var payments = new InvoicePayments(_db, _outbox);
        await payments.PayAsync(invoice.Id, TestContext.Current.CancellationToken);

        var second = await payments.PayAsync(invoice.Id, TestContext.Current.CancellationToken);

        Assert.Equal(PaymentResult.AlreadyPaid, second);
        Assert.Single(_outbox.Saved);
    }

    [Fact]
    public async Task Pagar_FaturaInexistente_NaoEnfileira()
    {
        var result = await new InvoicePayments(_db, _outbox).PayAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.Equal(PaymentResult.NotFound, result);
        _outbox.ShouldBeEmpty();
    }

    // Forgetting to register a message type must say what to do, not fail somewhere in serialization.
    [Fact]
    public void Enfileirar_TipoNaoRegistrado_ErroDizOQueFazer()
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
