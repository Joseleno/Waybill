# Etapa 7 — Fricção encontrada

Fonte: um agente sem contexto recebeu só o `README.md` da release e um feed local com os pacotes `0.1.0-alpha.local` (Oct 5, 2026). Ele criou um projeto novo, subiu PostgreSQL e RabbitMQ em Docker e **publicou o primeiro evento só com o README**, em cerca de 3 minutos. Também usou o inbox: a primeira entrega voltou `Processed`, a segunda `Duplicate`, e ficou um recibo. A única coisa que ele consultou fora do README foi o XML doc dos pacotes, como faria numa IDE.

## Fricções e destino

| # | Fricção | Severidade | Destino |
| --- | --- | --- | --- |
| 1 | Ordem entre `WaybillSchema.MigrateAsync` e `EnsureCreated`: chamado depois, o `EnsureCreated` não cria a tabela da aplicação (o banco já tem tabelas), e o `SaveChanges` falha com `42P01` | bloqueante | README: os dois schemas são independentes; com `EnsureCreated`, chamá-lo antes do `MigrateAsync` |
| 2 | `o.Uri = rabbitUri` sem tipo: com uma string, dá `CS0029` | irritante | README: `new Uri("amqp://localhost")` no próprio trecho |
| 3 | Faltava o `using Waybill.EntityFrameworkCore;` (`WaybillSchema`, `IOutbox<T>`, `IInbox<T>`), e o `Waybill.IOutbox` não genérico confunde | irritante | README: `using` no trecho, com o que ele traz |
| 4 | `AppJson` não estava definido | irritante | README: as duas linhas do `JsonSerializerContext` no trecho |
| 5 | O consumo com o inbox não dizia o formato do corpo | irritante | README: o corpo é o JSON do `JsonTypeInfo` registrado, o `type` AMQP traz o nome, e há link para o consumidor do exemplo |
| 6 | `--prerelease` e `--version` juntos falham no `dotnet add package` | sem efeito | Só aconteceu pelo desvio permitido (feed local com versão fixa); no nuget.org, o README está certo |
| 7 | De onde vêm `services` e o `AddDbContext` | menor | README: `services` é o `builder.Services`, e o comentário cita o `AddDbContext` |
| 8 | O pacote `Waybill.EntityFrameworkCore.PostgreSql` leva um `runtimeconfig.json` em `lib/net10.0` | menor | Mantido. Vem do `Microsoft.EntityFrameworkCore.Design`, que liga `GenerateRuntimeConfigurationFiles` para o `dotnet ef` gerar as migrations do pacote. Tirá-lo do pack dependeria de um target interno do NuGet, por um arquivo inofensivo de 411 bytes |

O trecho da API cresceu de 10 para 14 linhas de código, mas as linhas do Waybill continuam dez. O teste `Readme_ApiEmDezLinhas_Compila` não conta chaves, `using` e o `JsonSerializerContext` das mensagens.

**Ponto positivo relatado:** com um exchange inexistente, o log diz o que fazer ("Messages wait in the outbox until it is declared; Waybill does not create topology"), o breaker recua e a linha fica `pending` sem gastar tentativa.

## Segunda rodada (no lugar da validação do autor)

Por decisão do autor, um segundo agente sem contexto validou o README já corrigido, partindo de `dotnet new worker` e de um feed local com o código atual da branch. Isso substitui a validação do autor prevista no plano. **Ele publicou o primeiro evento na primeira execução**, sem erro de compilação nem de execução. Também usou o inbox: `Processed` na primeira entrega, `Duplicate` na segunda.

| # | Fricção | Severidade | Destino |
| --- | --- | --- | --- |
| 1 | Com a exchange declarada e nenhuma fila ligada, a mensagem vai para a DLQ do outbox em segundos (`312 NO_ROUTE`, depois de `MaxReturns` = 5), e o README não avisava | séria | README: ligar as filas antes de publicar, e o que acontece se não ligar. `OPERATIONS.md`: o SQL que devolve da DLQ, executado pelo teste `Operacao_SqlDoOperationsReenfileiraDaDlq_EODispatcherPublica`. O comportamento em si (orçamento de `basic.return`, ADR 0003) fica para decisão do autor |
| 2 | Não estava claro se o handler do inbox chama `SaveChanges` | irritante | README: o Waybill salva e faz o commit |
| 3 | O tipo de exchange não era dito | menor | README: topic, porque a routing key é o nome registrado |
| 4 | Onde roda o `MigrateAsync` | menor | README: passo de deploy, como o comando `migrate` do exemplo |
| 5 | De onde o dispatcher tira a connection string | menor | README: do contexto registrado; com `UseNpgsql(NpgsqlDataSource)`, `ConnectionString` explícito |
| 6 | Links para o GitHub não ajudam num feed local | sem efeito | No nuget.org e no GitHub, os links absolutos funcionam |

Observação de ambiente: reaproveitar a mesma versão local (`0.1.0-alpha.local`) entre pacotes diferentes deixa o binário antigo no cache global do NuGet, sem aviso. Em validações futuras, usar uma versão nova a cada pack ou um `globalPackagesFolder` local.
