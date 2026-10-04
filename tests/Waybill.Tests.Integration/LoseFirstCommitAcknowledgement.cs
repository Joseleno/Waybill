using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Waybill.Tests.Integration;

// The COMMIT reaches the server; the acknowledgement is "lost" on the way back, once. The transient error makes an
// execution strategy with retries run the unit again although it had committed.
public sealed class LoseFirstCommitAcknowledgement : DbTransactionInterceptor
{
    public bool Fired { get; private set; }

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => LoseOnce();

    public override Task TransactionCommittedAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        LoseOnce();
        return Task.CompletedTask;
    }

    private void LoseOnce()
    {
        if (Fired)
            return;
        Fired = true;
        throw new NpgsqlException("Simulated connection loss after COMMIT.", new IOException("connection reset"));
    }
}
