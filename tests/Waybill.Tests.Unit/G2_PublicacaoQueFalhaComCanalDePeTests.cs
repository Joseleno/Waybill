using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Waybill.RabbitMQ;

namespace Waybill.Tests.Unit;

// ADR 0003: a publish that throws while the connection and the channel stay up is not a network problem; retrying it
// would fail the same way forever and, as the oldest row, block every half-open probe behind it. So it is a defect of
// the message (DLQ, with the reason). With the channel closed, the same exception is a transport failure: retry, never
// a defect. No real broker provokes the first case on demand, so the channel here is a stand-in that throws.
public sealed class G2_PublicacaoQueFalhaComCanalDePeTests
{
    [Theory]
    [InlineData(false, PublishStatus.Defect)]
    [InlineData(true, PublishStatus.Retry)]
    public async Task G2_PublicacaoQueFalhaComConexaoECanalDePe_EhDefeito_ComCanalFechado_EhRetry(bool channelClosesOnPublish, PublishStatus expected)
    {
        var connection = Fake<IConnection>(isOpen: true);
        var channel = Fake<IChannel>(isOpen: true, closeOnPublish: channelClosesOnPublish);
        var options = Options.Create(new WaybillRabbitMqOptions { Uri = new Uri("amqp://localhost"), Exchange = "events" });
        await using var transport = new RabbitMqTransport(options, NullLogger<RabbitMqTransport>.Instance, connection, channel);

        var results = await transport.PublishAsync([Message()], TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.Equal(expected, result.Status);
        if (expected == PublishStatus.Defect)
            Assert.StartsWith("publish failed with the connection and channel up", result.Reason, StringComparison.Ordinal);
        else
            Assert.Equal(TransportFailure.Connection, result.Failure);
    }

    private static OutgoingMessage Message() => new()
    {
        MessageId = Guid.CreateVersion7(),
        Name = "billing.invoice-paid.v1",
        Payload = Encoding.UTF8.GetBytes("{}"),
        ContentType = "application/json",
        Headers = new Dictionary<string, string>(),
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static T Fake<T>(bool isOpen, bool closeOnPublish = false) where T : class
    {
        var fake = DispatchProxy.Create<T, ThrowingClient>();
        ((ThrowingClient)(object)fake).IsOpen = isOpen;
        ((ThrowingClient)(object)fake).CloseOnPublish = closeOnPublish;
        return fake;
    }

    /// <summary>
    /// Stands in for a RabbitMQ.Client connection or channel: reports <see cref="IsOpen"/>, throws on publish (closing
    /// first when <see cref="CloseOnPublish"/>, as a channel that goes down under the publish), and does nothing for
    /// the rest (dispose, close).
    /// </summary>
    public class ThrowingClient : DispatchProxy
    {
        public bool IsOpen { get; set; }

        public bool CloseOnPublish { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == "get_IsOpen")
                return IsOpen;
            if (targetMethod.Name == "BasicPublishAsync")
            {
                IsOpen = !CloseOnPublish;
                throw new InvalidOperationException("the client refused this message");
            }
            var type = targetMethod.ReturnType;
            return type == typeof(void) || !type.IsValueType ? null : Activator.CreateInstance(type);
        }
    }
}
