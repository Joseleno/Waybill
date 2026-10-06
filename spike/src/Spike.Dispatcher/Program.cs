using System.Globalization;
using Npgsql;
using Spike.Core;

// Processo filho da pergunta 2, morto com Process.Kill (equivalente ao kill -9: sem finally, sem shutdown gracioso).
// window = "publishing": reivindica, faz commit e trava "publicando" (lote volta pelo lease).
// window = "open-claim": reivindica e trava com a transação de claim ABERTA (lote volta quando o backend aborta).
// Uso: Spike.Dispatcher <connString> <owner> <mode> <partitions> <batch> <leaseSeconds> <window>
var conn = args[0];
var owner = args[1];
var mode = Enum.Parse<ClaimMode>(args[2]);
var partitions = int.Parse(args[3], CultureInfo.InvariantCulture);
var batch = int.Parse(args[4], CultureInfo.InvariantCulture);
var lease = TimeSpan.FromSeconds(double.Parse(args[5], CultureInfo.InvariantCulture));
var window = args[6];

await using var ds = NpgsqlDataSource.Create(conn);
var dispatcher = new Dispatcher(ds, new DispatcherOptions(owner, mode, partitions, batch, lease));

if (window == "open-claim")
{
    var (_, _, rows) = await dispatcher.ClaimUncommittedAsync();
    Console.WriteLine($"CLAIMED-OPEN {rows.Count}");
}
else
{
    var claimed = await dispatcher.ClaimAsync();
    Console.WriteLine($"CLAIMED {claimed.Count}");
}
Console.Out.Flush();

await Task.Delay(Timeout.Infinite);
