# Etapa 2 — Núcleo e gravação atômica (G1)

Derivado de `docs/plano.md`, seção "Etapa 2", e do Contrato técnico de `docs/escopo-e-fronteiras.md`.

## Objetivo

Entregar G1: **o evento existe se, e somente se, a transação fizer commit.** Cada armadilha que a revisão encontrou vira um teste.

## Decisões de desenho (Oct 4, 2026; ADR 0002)

| Tema | Decisão |
| --- | --- |
| Enfileiramento | `IOutbox` explícito, ligado ao `DbContext`: `outbox.Enqueue(message, key)` antes do `SaveChanges`. Eventos de domínio nas entidades podem virar um adaptador depois |
| Schema | Migrations do próprio pacote, schema `waybill`, histórico em `waybill.__waybill_migrations`, aplicadas por `WaybillSchema.MigrateAsync`. O `DbContext` do usuário só mapeia a outbox (`modelBuilder.AddWaybillOutbox()`) com `ExcludeFromMigrations`, para o INSERT entrar no mesmo `SaveChanges` |
| Chave | A outbox grava `key` (opcional) e `key_hash` (murmur2 estável da chave, ou do `message_id` sem chave). Na v0.2 a partição é `key_hash % P` na consulta, então mudar P não reescreve linhas. Substitui "partition gravada" no Escopo |
| Tipos | Registro explícito com nome estável e `JsonTypeInfo` de source generation: `o.AddMessage("billing.invoice-paid.v1", AppJson.Default.InvoicePaid)`. Nunca `Type.GetType`. `MaxPayloadBytes` é obrigatório |

## API (alvo da etapa)

```csharp
// registro
services.AddWaybill(o =>
{
    o.AddMessage("billing.invoice-paid.v1", AppJson.Default.InvoicePaid);
    o.MaxPayloadBytes = 256 * 1024;
});
services.AddDbContext<AppDbContext>(db => db.UseNpgsql(cs));
services.AddWaybillOutbox<AppDbContext>();

// modelo do usuário
protected override void OnModelCreating(ModelBuilder b) => b.AddWaybillOutbox();

// uso
outbox.Enqueue(new InvoicePaid(invoice.Id, invoice.Amount), key: invoice.Id.ToString());
await db.SaveChangesAsync(ct);

// schema, por um serviço de inicialização (nunca pela API em produção)
await WaybillSchema.MigrateAsync(connectionString, ct);
```

## Mecanismo

1. `Enqueue` gera o `message_id` (UUIDv7) **no momento do enfileiramento**, valida que o tipo está registrado, serializa com o `JsonTypeInfo` registrado, valida o tamanho e captura `traceparent`/`tracestate` do `Activity.Current`. O erro de tipo não registrado ou payload grande demais aparece **para quem enfileira**, antes de qualquer gravação.
2. No mesmo `Enqueue`, a linha de outbox é adicionada ao change tracker do `DbContext` (`context.Add`). A unidade de trabalho do EF é a única fonte de verdade: o próximo `SaveChanges` bem-sucedido insere a linha, um que falha a mantém Added, um repetido na mesma transação não duplica, e `ChangeTracker.Clear()` ou o reset de um contexto de pool a descartam junto com os dados.
3. Depois do save, a linha continua rastreada (vira Unchanged quando as mudanças são aceitas). Isso mantém correto o padrão `SaveChanges(acceptAllChangesOnSuccess: false)` com retry da estratégia de execução (teste `G1_SaveChangesSemAceitarRepetidoPelaEstrategia_ReinsereOEvento`).
4. O `Enqueue` recusa o que deixaria a linha commitar separada dos dados: `TransactionScope` e `AutoTransactionBehavior.Never` sem transação aberta.
5. Ao fim do escopo, linha de outbox ainda Added gera **log de erro**; lançar exceção só com `o.ThrowOnPendingMessagesAtDispose = true`, e então o `DbContext` é descartado antes, para não vazar conexão e transação.

Histórico: a primeira versão usava um buffer próprio e um interceptor que anexava as linhas em `SavingChanges`. A revisão de código mostrou dois furos na G1 (`Clear()` depois de uma falha reinseria o evento sem os dados; um `Enqueue` durante o `SavingChanges` se perdia), cobertos agora por `G1_UnidadeDeTrabalhoDoEf_FonteDeVerdade`.

## Schema `waybill` (v0.1)

`outbox`: `id uuid PK`, `type text`, `key text NULL`, `key_hash int`, `sequence bigint NULL` (v0.2), `payload bytea`, `content_type text`, `headers jsonb NULL`, `status text` (`pending`, `claimed`, `published`, `dlq`), `owner text NULL`, `fence bigint`, `attempts int`, `lease_until timestamptz NULL`, `created_at timestamptz` (relógio do banco), `published_at timestamptz NULL`, `dlq_reason text NULL`. Índice parcial em `(id) WHERE status IN ('pending','claimed')`.

`inbox`: `handler text`, `message_id uuid`, `processed_at timestamptz`, PK `(handler, message_id)`. Só a tabela; o uso é da etapa 4.

## Fake do outbox

`FakeOutbox<TContext>` num pacote novo, `Waybill.Testing`, para o usuário testar o próprio código sem PostgreSQL. Registra o que foi enfileirado, liga cada mensagem aos eventos públicos `SavedChanges`/`SaveChangesFailed` do `DbContext` e oferece asserções: "contém mensagem do tipo X que satisfaz P" e "nenhuma mensagem pendente". Limite documentado: o fake trata o `SaveChanges` como commit e não observa commit ou rollback de transação explícita.

## Fora do escopo

Dispatcher, claim e transporte (etapa 3); uso do inbox (etapa 4); limpeza (etapa 5).
