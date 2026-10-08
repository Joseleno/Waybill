# Etapa 8 — Ordenação por chave (G4 na publicação)

Derivado de `docs/plano-v0.2.md`, seção "Etapa 8", do Contrato técnico de `docs/escopo-e-fronteiras.md` (linha "Ordenação (v0.2)") e do ADR 0001, seção "Para a v0.2".

## Objetivo

Entregar a primeira metade da G4: **por chave de agregado, a partir da primeira mensagem com `sequence`, a primeira cópia de cada mensagem a chegar ao broker chega depois da primeira cópia de todas as anteriores, na ordem de commit; cópias repetidas podem chegar depois e são reconhecidas pelo `message_id`; uma mensagem liberada fica fora da sequência: deixa lacuna e, se uma tentativa anterior dela ficou sem confirmação, uma cópia ainda pode chegar a qualquer momento.** (Ressalva das liberadas achada pelo teste de propriedade, Oct 8, 2026.) (Enunciado fortalecido na abertura da 8c, em Oct 7, 2026, com o autor; antes: "na ordem de commit, salvo republicação após lease vencido".) A detecção no consumidor (etapa 10) e a ordem estrita no Kafka (etapa 9) completam a garantia. Antes disso, a etapa entrega o espaçamento entre tentativas de `basic.return`, que muda a condição de reivindicável sobre a qual a ordenação se apoia.

## Divisão em quatro PRs

| PR | Conteúdo | Cenários do plano |
| --- | --- | --- |
| **8a** | Espaçamento do `basic.return`: coluna `next_attempt_at`, condição de reivindicável, opções, migration da v0.2, ADR | `basic.return` repetido até `MaxReturns`; falha de transporte durante a espera; dez mil linhas em espera à frente do backlog |
| **8b** | Lease por partição: tabelas de posse e de instâncias, `epoch`, heartbeat, coleta, P e lease globais no banco, fatia justa | P ou lease diferente do banco; instâncias entrando e saindo |
| **8c-1** | Cabeça por chave com M = 1: trigger que numera, `outbox_keys`, lista de chaves contra deadlock, filtro de cabeça, `sequence` no envelope, DLQ bloqueia a chave, liberação, métrica, transições de `settings`, ADR 0008 (parte 1) | Sem deadlock; DLQ só para a chave; liberação; troca de dono com linhas em voo; marcação ou devolução concorrente; propriedade; ordenação desligada; backlog; upgrade da `0.1.0-alpha` |
| **8c-2** | M ≥ 2 em rodadas: `MaxPerKey`, sucessoras e prefixo, rodadas como política do transporte, ADR 0008 (parte 2) e exceção no ADR 0003. Dividida da 8c com o autor em Oct 7, 2026: a G4 não depende de M | Os cenários com M ≥ 2 (troca de dono, marcação concorrente, propriedade) |

## Decisões de desenho

| Tema | Decisão |
| --- | --- |
| Ordenação opcional | Desligada por padrão. O dispatcher a liga e grava no banco, com P e o lease (8b), conferidos no startup; divergência é erro crítico. Revisto na 8c, com o autor: quem decide a numeração é o banco, com um trigger que numera as linhas com chave enquanto a linha de `settings` existe. `OrderByKey` passa para `WaybillDispatcherOptions`, e do lado de quem enfileira não sobra opção: a lista de chaves contra deadlock viaja sempre numa coluna da própria linha. Desligada, nem o contador nem o filtro rodam; o trigger só confere a tabela de uma linha. Mensagem sem chave nunca é ordenada. Linhas gravadas antes de ligar (com `sequence` nula) não entram no filtro: a ordem vale a partir da primeira mensagem com `sequence` |
| Critério de custo | Escrito antes da medição; revisto na abertura da 8c, antes de qualquer medição, porque o desenho do contador mudou. **Desligada (sem a linha de `settings`):** o claim é o da 8a mais uma guarda de `settings` avaliada uma vez por instrução; na gravação, o único acréscimo é o trigger, que confere `settings` e anula a coluna da lista, sem tocar `outbox_keys`. Um teste estrutural prova isso. O p99 da transação e a vazão do claim ficam a no máximo 5% da tag `v0.1.0-alpha`, medidos no job agendado. **Ligada:** o custo é medido no p99 da transação e na vazão do claim, e publicado no `OPERATIONS.md`; não há critério de aprovação, porque quem liga escolhe pagar |
| Espaçamento do `basic.return` (8a) | Depois do k-ésimo retorno, a linha volta a `pending` com `next_attempt_at = clock_timestamp() + min(ReturnBackoff × 2^(k−1), MaxReturnBackoff)`, no relógio do banco. Defaults: `ReturnBackoff` = 1 min, `MaxReturnBackoff` = 10 min, `MaxReturns` = 5 (inalterado): 15 min de espera somada antes da DLQ, contra ~5 s na v0.1. `ReturnBackoff` = 0 devolve o comportamento da v0.1. Só o `basic.return` agenda espera: defeito vai direto à DLQ, falha de transporte e lease vencido reabrem na hora, sem gastar tentativa e sem reiniciar a progressão |
| Condição de reivindicável (8a) | Ganha `(next_attempt_at IS NULL OR next_attempt_at <= clock_timestamp())`, no nível do `FOR UPDATE` e repetida no `UPDATE` externo (ADR 0001, item 2). O índice parcial `ix_outbox_claimable` não muda: o relógio não pode entrar no predicado de um índice |
| Espera e cabeça por chave | Uma linha em espera é cabeça e bloqueia a chave; na DLQ, continua bloqueando até liberação. O teto total (15 min por padrão) é o tempo que o operador tem para criar o binding antes de a chave travar. Vai para o ADR da 8a e é retomado no da 8c |
| Posse da partição (8b) | Revisto no PLAN da 8b, com o autor, em Oct 6, 2026: o claim verifica a posse com um `EXISTS` na tabela de posse, no snapshot do próprio comando, exigindo que o lease da partição dure mais que o lease da linha. Só a lista de partições em memória deixaria uma instância pausada reivindicar depois de perder a partição. A posse é adquirida e renovada em comando à parte, com `epoch` que sobe a cada troca de dono. Com um lote em voo por instância, nenhuma chave tem dois claims concorrentes, que é o que gerou as inversões do spike com M ≥ 2. O papel do `epoch` (renovação e devolução da partição; `transactional.id` na etapa 9) e a prova de que ele não precisa entrar no fencing da linha fecham no PLAN da 8b, com ADR |
| Liberação de chave (8c) | Status novo `released`, terminal, com `released_at` e `released_by`: não bloqueia a cabeça, não é publicado, não volta a `pending` (trigger de estado terminal) e não é apagado pela retenção, como a DLQ. O SQL fica no `OPERATIONS.md`, marcado para um teste que o executa, como o reenvio da DLQ (`<!-- dlq-requeue -->`). A API continua na v1.0 |
| Migration | Uma só para a v0.2, criada na 8a e refeita na 8b e na 8c enquanto não houver tag. Aditiva: nenhuma linha pendente é reescrita |

## Perguntas da 8c (respondidas em Oct 7, 2026, com o autor; desenho no `PLAN.md`)

- **Retorno ou defeito no meio de uma série de M ≥ 2.** Com publisher confirms, o `basic.return` da mensagem N chegaria depois de N+1 já enviada. Resposta: rodadas, na 8c-2 (a 8c-1 entrega a G4 com M = 1). M é configurável, default 1; a k-ésima mensagem de cada chave sai na rodada k, e a rodada k+1 só começa depois do resultado da k. A chave cuja rodada falhou devolve as seguintes sem gastar tentativa.
- **Mensagem com chave gravada com a ordenação desligada e depois ligada com backlog.** A regra acima vale (a ordem começa na primeira `sequence`), com teste e uma linha no `OPERATIONS.md`.

## API da 8a

```csharp
services.AddWaybillDispatcher<AppDbContext>(o =>
{
    o.MaxReturns = 5;                                // inalterado
    o.ReturnBackoff = TimeSpan.FromMinutes(1);       // espera depois do 1º retorno; dobra a cada retorno
    o.MaxReturnBackoff = TimeSpan.FromMinutes(10);   // teto de cada espera
});
```

## Fora da 8a

Lease por partição (8b); cabeça por chave, contador, `sequence`, liberação de chave e a opção de ligar a ordenação (8c).
