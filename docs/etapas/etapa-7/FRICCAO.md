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
| 8 | O pacote `Waybill.EntityFrameworkCore.PostgreSql` leva um `runtimeconfig.json` em `lib/net10.0` | menor | Registrado. Vem do `Microsoft.EntityFrameworkCore.Design`, usado para gerar as migrations do pacote. Inofensivo; decidir antes da release se vale excluí-lo do pack |

O trecho da API cresceu de 10 para 14 linhas de código, mas as linhas do Waybill continuam dez. O teste `Readme_ApiEmDezLinhas_Compila` não conta chaves, `using` e o `JsonSerializerContext` das mensagens.

**Ponto positivo relatado:** com um exchange inexistente, o log diz o que fazer ("Messages wait in the outbox until it is declared; Waybill does not create topology"), o breaker recua e a linha fica `pending` sem gastar tentativa.

## Falta

A validação do autor, seguindo o mesmo README, antes do merge.
