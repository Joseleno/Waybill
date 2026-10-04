# ADR 0002 — Enfileiramento explícito, schema do pacote e registro de tipos

- Status: aceito
- Data: 2026-10-04
- Etapa: 2 (núcleo e G1)

## Contexto

A etapa 2 fecha a API que o usuário toca para gravar um evento junto com os dados, e o schema que o pacote cria no banco. As duas coisas são difíceis de mudar depois da alpha: a API porque está no código de cada consumidor, o schema porque precisa de upgrade compatível com mensagens pendentes.

## Decisões

1. **Enfileiramento explícito por `IOutbox`.** A aplicação chama `outbox.Enqueue(message, key)` antes do `SaveChanges`. O `IOutbox<TContext>` é scoped e ligado ao `DbContext` do mesmo escopo. Não há varredura de eventos de domínio nas entidades: isso acoplaria o domínio a uma interface do pacote, e quem quiser pode escrever esse adaptador por cima do `IOutbox`.
2. **Serialização e validação no `Enqueue`.** O `message_id` (UUIDv7), a verificação de tipo registrado, a serialização e a verificação de tamanho acontecem no momento do enfileiramento. O erro aparece para quem enfileira, antes de qualquer gravação, e o conteúdo fica congelado naquele instante. O interceptor só anexa as linhas pendentes em `SavingChanges`, com deduplicação por referência, e as libera em `SavedChanges`.
3. **Registro de tipos com nome estável e `JsonTypeInfo`.** `o.AddMessage("billing.invoice-paid.v1", AppJson.Default.InvoicePaid)`. O nome vai no envelope e é o contrato com os consumidores: renomear ou mover a classe não o muda. A serialização usa source generation. Nunca `Type.GetType`. `MaxPayloadBytes` é configuração obrigatória.
4. **Schema próprio, com migrations do pacote.** Tabelas no schema `waybill`, histórico em `waybill.__waybill_migrations`, aplicadas por `WaybillSchema.MigrateAsync`, que um serviço de inicialização chama (nunca a API em produção). O `DbContext` do usuário só mapeia a outbox, com `ExcludeFromMigrations`, para o INSERT entrar no mesmo `SaveChanges` e na mesma transação. Cada versão do pacote traz as migrations do seu schema.
5. **`key` e `key_hash` no lugar de `partition`.** A outbox grava a chave de agregado (opcional) e um hash estável dela (murmur2, como o particionador do Kafka; sem chave, o hash do `message_id`). A v0.2 calcula a partição como `key_hash % P` na consulta: mudar P não reescreve linhas pendentes.
6. **Detecção de mensagem pendente no fim do escopo de DI.** O EF não oferece gancho de descarte do `DbContext`. O `IOutbox<TContext>` scoped verifica o buffer no próprio `Dispose`, junto com o do `DbContext`, e loga erro (ou lança, por opção). Um `DbContext` criado à mão fora do DI não é coberto, e isso fica documentado.
7. **Fake num pacote separado, `Waybill.Testing`.** Para o usuário testar o próprio código sem PostgreSQL, sem levar código de teste aos pacotes de produção.

## Consequências

- G2 muda: tipo não registrado e falha de serialização deixam de ser defeitos que levam à DLQ, porque nunca chegam à tabela. À DLQ só vai a mensagem que o broker recusa por tamanho (limite do broker menor que o configurado) ou que foi gravada sob um limite maior que o atual.
- O Escopo deixa de prever `partition` gravada; a ordenação da v0.2 parte de `key_hash`.
- São quatro pacotes: `Waybill`, `Waybill.EntityFrameworkCore.PostgreSql`, `Waybill.RabbitMQ` e `Waybill.Testing`.
- O usuário precisa de duas linhas a mais no `DbContext` e no registro (`AddWaybillOutbox()` e `UseWaybill(sp)`), em troca de não gerar migrations do pacote a cada versão.

## Alternativas descartadas

- **Eventos de domínio nas entidades**: acopla o domínio ao pacote.
- **Tabelas nas migrations do usuário** (estilo MassTransit): cada upgrade do pacote vira trabalho do usuário.
- **`partition` gravada**: exige P definido já na v0.1 e reescrita das linhas pendentes ao mudar P.
- **Nome do tipo derivado da classe**: renomear a classe quebra os consumidores.
- **Base `WaybillDbContext` com `Dispose` sobrescrito**: cobriria todos os casos de descarte, mas impõe herança, contra o "sem framework".
