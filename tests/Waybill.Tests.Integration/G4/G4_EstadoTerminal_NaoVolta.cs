using Npgsql;

namespace Waybill.Tests.Integration.G4;

// published and released are terminal (ADR 0008). The claim decides a key's head from its snapshot and does not repeat
// that decision on the outer UPDATE, which is safe only because a row never leaves a terminal status: an operator's SQL
// that sent a released row back to pending would let it go out after its successors.
[Collection(PostgresCollection.Name)]
public sealed class G4_EstadoTerminal_NaoVolta(PostgresFixture postgres)
{
    [Theory]
    [InlineData("published", "pending")]
    [InlineData("published", "dlq")]
    [InlineData("released", "pending")]
    [InlineData("released", "dlq")]
    public async Task G4_EstadoTerminal_NaoVolta_Recusado(string terminal, string target)
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await database.ExecuteAsync($"""
            INSERT INTO waybill.outbox (id, type, key, key_hash, payload, content_type, status)
            VALUES ('{Id}', 'billing.invoice-paid.v1', 'invoice-1', 0, '\x00', 'application/json', '{terminal}')
            """);

        var error = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(
            $"UPDATE waybill.outbox SET status = '{target}' WHERE id = '{Id}'"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Contains("terminal", error.MessageText);
        Assert.Equal(1, await database.ScalarAsync($"SELECT count(*) FROM waybill.outbox WHERE id = '{Id}' AND status = '{terminal}'"));
    }

    [Fact]
    public async Task G4_EstadoTerminal_OutrasColunasEMudancasNaoTerminais_Permitidas()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await database.ExecuteAsync($"""
            INSERT INTO waybill.outbox (id, type, key, key_hash, payload, content_type, status)
            VALUES ('{Id}', 'billing.invoice-paid.v1', 'invoice-1', 0, '\x00', 'application/json', 'claimed')
            """);

        await database.ExecuteAsync($"UPDATE waybill.outbox SET status = 'published', published_at = clock_timestamp() WHERE id = '{Id}'");
        await database.ExecuteAsync($"UPDATE waybill.outbox SET status = 'published', lease_until = NULL WHERE id = '{Id}'");

        Assert.Equal(1, await database.ScalarAsync($"SELECT count(*) FROM waybill.outbox WHERE id = '{Id}' AND status = 'published'"));
    }

    private static readonly Guid Id = Guid.Parse("01900000-0000-7000-8000-000000000001");
}
