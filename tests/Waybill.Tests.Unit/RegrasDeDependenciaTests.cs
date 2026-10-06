using System.Reflection;

namespace Waybill.Tests.Unit;

public sealed class RegrasDeDependenciaTests
{
    // The core package may depend only on the BCL and Microsoft.Extensions.*; anything else belongs
    // in a transport or persistence package.
    [Fact]
    public void Nucleo_DependeSoDeSystemEMicrosoftExtensions()
    {
        var core = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Waybill.dll"));

        var forbidden = core.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => name is not ("System" or "netstandard" or "mscorlib")
                && !name.StartsWith("System.", StringComparison.Ordinal)
                && !name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(forbidden);
    }
}
