# ADR 0005 — Ergonomia do registro

- Status: aceito
- Data: 2026-10-05
- Etapa: 6 (exemplo executável)

## Contexto

Na etapa 6 um agente sem contexto usou o pacote só pelo README do exemplo (`docs/etapas/etapa-6/FRICCAO.md`). Ele publicou o primeiro evento, mas a configuração passou de dez linhas e quatro nomes confundiram. A API ainda não foi publicada: é a última chance barata de mudar, antes do congelamento da v0.1.

## Decisão

| Antes | Depois | Por quê |
| --- | --- | --- |
| `AddHealthChecks().AddWaybillDispatcher()` | `AddHealthChecks().AddWaybillDispatcherCheck()` | Tinha o mesmo nome do registro do dispatcher e foi lido como registro duplicado. Segue o padrão do EF Core (`AddDbContextCheck<T>()`) |
| `modelBuilder.AddWaybillOutbox()` | `modelBuilder.MapWaybillOutbox()` | `AddWaybillOutbox` existia em dois lugares com sentidos diferentes, ambos obrigatórios. Um é registro de serviço, o outro é mapeamento do modelo |
| `AddWaybillDispatcher(o => o.ConnectionString = ...)` e `AddWaybillRetention(o => o.ConnectionString = ...)` | Também `AddWaybillDispatcher<TContext>()` e `AddWaybillRetention<TContext>()` | A connection string aparecia quatro vezes. O outbox mora no banco do `DbContext`, então a sobrecarga lê a connection string dele na partida. Uma `ConnectionString` explícita continua valendo e tem precedência. Dispatcher e retenção seguem com data source próprio, fora do pool da aplicação |
| Registros em `Waybill`, `Waybill.EntityFrameworkCore`, `.Dispatching`, `.Retention`, `Waybill.RabbitMQ`, `Waybill.Testing` | Métodos `Add*` em `Microsoft.Extensions.DependencyInjection`; `MapWaybillOutbox` em `Microsoft.EntityFrameworkCore` | Cinco `using` só para configurar. É a convenção dos pacotes .NET (EF Core, Npgsql, HealthChecks). Os tipos (opções, `IOutbox`, `IInbox`, resultados) ficam nos namespaces do Waybill |

O núcleo continua dependendo só de `Microsoft.Extensions.*` e da BCL: mudar o namespace não muda dependência.

## Consequências

- Quebra de API em relação ao código das etapas 2 a 5. Ainda não houve release; o CHANGELOG registra as mudanças em "Changed".
- A mensagem de erro de quem esquece o mapeamento passa a citar `MapWaybillOutbox()`.
- `AddWaybillDispatcher<TContext>()` falha na partida, com mensagem, se o contexto não tiver connection string (por exemplo, configurado só com uma conexão aberta).
- Não mudam: `AddWaybill` (registra opções e mensagens; o README explica), `Enqueue` (o README e a documentação dizem que entra no próximo `SaveChanges`) e `key` (documentado como identificador do agregado; ordenação por chave é da v0.2).

## Testes que provam

`Registro_DispatcherDoContexto_UsaAConnectionStringDoDbContext`, `Registro_ConnectionStringExplicita_TemPrecedencia`, `Registro_ContextoSemConnectionString_FalhaNaPartida`, `Registro_SoComUsingDeDependencyInjection_RegistraTudo` (unidade). Os testes de integração e o exemplo passam a usar os nomes novos.
