using System.Collections.Concurrent;

namespace Spike.Core;

public sealed record Published(Guid Id, string Key, long Sequence, long Fence, string Owner, long Order);

public sealed class FakeTransportException() : Exception("falha de transporte simulada");

/// <summary>
/// No lugar do RabbitMQ: registra a ordem global em que cada mensagem "chegou ao broker".
/// Latência por lote simula a espera da confirmação; falha simula queda de conexão,
/// e pode acontecer no meio do lote (parte já chegou ao broker, sem confirmação).
/// </summary>
public sealed class FakePublisher
{
    private long _order;

    public TimeSpan ConfirmLatency { get; init; } = TimeSpan.Zero;
    /// <summary>Latência variável por lote (sobrepõe ConfirmLatency); usada para estourar o lease de propósito.</summary>
    public Func<TimeSpan>? LatencyPerBatch { get; init; }
    public double FailureRate { get; init; }
    /// <summary>Na falha, publica um prefixo aleatório do lote antes de lançar (lote parcial).</summary>
    public bool PartialFailure { get; init; }
    public bool YieldBetweenMessages { get; init; }
    public ConcurrentQueue<Published> Log { get; } = new();
    public long Failures;

    /// <summary>Ponto de parada para testes: publica só depois que o gate abrir.</summary>
    public Func<string, IReadOnlyList<Claimed>, Task>? BeforePublish { get; set; }

    public async Task PublishAsync(string owner, IReadOnlyList<Claimed> batch, CancellationToken ct)
    {
        if (BeforePublish is not null)
            await BeforePublish(owner, batch);

        var fail = FailureRate > 0 && Random.Shared.NextDouble() < FailureRate;
        var count = fail ? (PartialFailure ? Random.Shared.Next(batch.Count) : 0) : batch.Count;

        for (var i = 0; i < count; i++)
        {
            var m = batch[i];
            Log.Enqueue(new Published(m.Id, m.Key, m.Sequence, m.Fence, owner, Interlocked.Increment(ref _order)));
            if (YieldBetweenMessages)
                await Task.Yield();
        }

        if (fail)
        {
            Interlocked.Increment(ref Failures);
            throw new FakeTransportException();
        }

        var latency = LatencyPerBatch?.Invoke() ?? ConfirmLatency;
        if (latency > TimeSpan.Zero)
            await Task.Delay(latency, ct);
    }
}
