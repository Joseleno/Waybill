namespace Waybill.Tests.Unit;

// The order a consumer reads from an ordered message (stage 10). Its key follows the enqueue rule exactly: not null, at
// most 255 UTF-8 bytes, and empty allowed, because Enqueue accepts an empty key and the transport sends it with the
// sequence. Refusing it here would turn a message Waybill itself produced into one the consumer can never process.
public sealed class MessageOrderTests
{
    [Fact]
    public void MessageOrder_ChaveESequencia_GuardaAsDuas()
    {
        var order = new MessageOrder("order-42", 3);

        Assert.Equal(("order-42", 3L), (order.Key, order.Sequence));
    }

    [Fact]
    public void MessageOrder_ChaveVazia_Aceita()
    {
        Assert.Equal(string.Empty, new MessageOrder(string.Empty, 1).Key);
    }

    [Fact]
    public void MessageOrder_ChaveNula_Recusa()
    {
        Assert.Throws<ArgumentNullException>(() => new MessageOrder(null!, 1));
    }

    [Fact]
    public void MessageOrder_ChaveAte255BytesUtf8_AceitaEAcimaRecusa()
    {
        _ = new MessageOrder(new string('é', 127) + "k", 1); // 255 bytes

        var error = Assert.Throws<ArgumentException>(() => new MessageOrder(new string('é', 128), 1)); // 256 bytes
        Assert.Equal("key", error.ParamName);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void MessageOrder_SequenciaMenorQueUm_Recusa(long sequence)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new MessageOrder("order-42", sequence));
        Assert.Equal("sequence", error.ParamName);
    }

    [Fact]
    public void MessageOrder_Default_NaoTemChave()
    {
        Assert.Null(default(MessageOrder).Key);
    }

    [Fact]
    public void MessageOrder_IgualdadePorValor()
    {
        Assert.Equal(new MessageOrder("order-42", 3), new MessageOrder("order-42", 3));
        Assert.NotEqual(new MessageOrder("order-42", 3), new MessageOrder("order-42", 4));
    }
}
