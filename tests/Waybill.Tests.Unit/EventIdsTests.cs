using System.Reflection;
using Microsoft.Extensions.Logging;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.RabbitMQ;

namespace Waybill.Tests.Unit;

// Operators filter and alert on a log's EventId: one id must mean one thing across Waybill's packages.
public sealed class EventIdsTests
{
    [Fact]
    public void EventIds_UnicosEmTodosOsPacotes()
    {
        var ids = new[] { typeof(OutgoingMessage).Assembly, typeof(WaybillDispatcherOptions).Assembly, typeof(WaybillRabbitMqOptions).Assembly }
            .SelectMany(a => a.GetTypes())
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Select(m => (Method: $"{m.DeclaringType!.Name}.{m.Name}", Attribute: m.GetCustomAttribute<LoggerMessageAttribute>()))
            .Where(m => m.Attribute is not null)
            .ToList();

        Assert.NotEmpty(ids);
        var repeated = ids.GroupBy(m => m.Attribute!.EventId).Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(m => m.Method))}");
        Assert.Empty(repeated);
    }
}
