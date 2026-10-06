# Etapa 8 — Ordenação por chave (G4 na publicação)

Derivado de `docs/plano-v0.2.md`, seção "Etapa 8", do Contrato técnico de `docs/escopo-e-fronteiras.md` (linha "Ordenação (v0.2)") e do ADR 0001, seção "Para a v0.2".

## Objetivo

Entregar a primeira metade da G4: **eventos da mesma chave de agregado são publicados na ordem de commit, salvo republicação após lease vencido.** A detecção no consumidor (etapa 10) e a ordem estrita no Kafka (etapa 9) completam a garantia. Antes disso, a etapa entrega o espaçamento entre tentativas de `basic.return`, que muda a condição de reivindicável sobre a qual a ordenação se apoia.

## Divisão em três PRs

| PR | Conteúdo | Cenários do plano |
| --- | --- | --- |
| **8a** | Espaçamento do `basic.return`: coluna `next_attempt_at`, condição de reivindicável, opções, migration da v0.2, ADR | `basic.return` repetido até `MaxReturns`; falha de transporte durante a espera; dez mil linhas em espera à frente do backlog |
| **8b** | Lease por partição: tabelas de posse e de instâncias, `epoch`, heartbeat, coleta, P e lease globais no banco, fatia justa | P ou lease diferente do banco; instâncias entrando e saindo |
| **8c** | Cabeça por chave: filtro, contador `outbox_keys`, `sequence` no envelope, DLQ bloqueia a chave, liberação, métrica, ADR | Os outros oito cenários da etapa 8 |

## Decisões de desenho

| Tema | Decisão |
| --- | --- |
| Ordenação opcional | Desligada por padrão. Liga-se em `AddWaybill`, porque o enfileiramento (contador) e o dispatcher (filtro de cabeça) precisam concordar. O valor é gravado no banco com P e o lease (8b) e conferido no startup; divergência é erro crítico. Desligada, nem o contador nem o filtro rodam. Mensagem sem chave nunca é ordenada. Linhas gravadas antes de ligar (com `sequence` nula) não entram no filtro: a ordem vale a partir da primeira mensagem com `sequence` |
| Critério de custo | Escrito antes da medição. **Desligada:** o caminho de gravação e o claim são os da v0.1, mais o predicado da 8a; um teste estrutural prova que nada lê ou grava `outbox_keys` nem usa o filtro de cabeça, e a vazão do claim fica a no máximo 5% da do commit anterior à etapa (medida no job agendado). **Ligada:** o custo é medido no p99 da transação e na vazão do claim, e publicado no `OPERATIONS.md`; não há critério de aprovação, porque quem liga escolhe pagar |
| Espaçamento do `basic.return` (8a) | Depois do k-ésimo retorno, a linha volta a `pending` com `next_attempt_at = clock_timestamp() + min(ReturnBackoff × 2^(k−1), MaxReturnBackoff)`, no relógio do banco. Defaults: `ReturnBackoff` = 1 min, `MaxReturnBackoff` = 10 min, `MaxReturns` = 5 (inalterado): 15 min de espera somada antes da DLQ, contra ~5 s na v0.1. `ReturnBackoff` = 0 devolve o comportamento da v0.1. Só o `basic.return` agenda espera: defeito vai direto à DLQ, falha de transporte e lease vencido reabrem na hora, sem gastar tentativa e sem reiniciar a progressão |
| Condição de reivindicável (8a) | Ganha `(next_attempt_at IS NULL OR next_attempt_at <= clock_timestamp())`, no nível do `FOR UPDATE` e repetida no `UPDATE` externo (ADR 0001, item 2). O índice parcial `ix_outbox_claimable` não muda: o relógio não pode entrar no predicado de um índice |
| Espera e cabeça por chave | Uma linha em espera é cabeça e bloqueia a chave; na DLQ, continua bloqueando até liberação. O teto total (15 min por padrão) é o tempo que o operador tem para criar o binding antes de a chave travar. Vai para o ADR da 8a e é retomado no da 8c |
| Posse da partição (8b) | A condição no claim é sobre a própria `outbox`: `key_hash % P = ANY(@minhas)`, um predicado da tabela travada, sem `JOIN` com a tabela de posse. A posse é adquirida e renovada em comando à parte, com `epoch` que sobe a cada troca de dono, e a instância só reivindica enquanto o lease da partição dura mais que o lease da linha. Com um lote em voo por instância, nenhuma chave tem dois claims concorrentes, que é o que gerou as inversões do spike com M ≥ 2. O papel do `epoch` (renovação e devolução da partição; `transactional.id` na etapa 9) e a prova de que ele não precisa entrar no fencing da linha fecham no PLAN da 8b, com ADR |
| Liberação de chave (8c) | Status novo `released`, terminal: não bloqueia a cabeça, não é publicado e não é apagado pela retenção, como a DLQ. O SQL fica no `OPERATIONS.md`, marcado para um teste que o executa, como o reenvio da DLQ (`<!-- dlq-requeue -->`). A API continua na v1.0 |
| Migration | Uma só para a v0.2, criada na 8a e refeita na 8b e na 8c enquanto não houver tag. Aditiva: nenhuma linha pendente é reescrita |

## Perguntas abertas para a 8c

- **Retorno ou defeito no meio de uma série de M ≥ 2.** As M mensagens da chave saem em série no mesmo canal, mas com publisher confirms o `basic.return` da mensagem N chega depois de N+1 já ter sido enviada. N+1 é publicada e N fica em espera ou na DLQ: o consumidor vê um gap e, se N for republicada depois, uma regressão. As saídas candidatas: M = 1 por padrão; esperar a confirmação de N antes de enviar N+1, o que mata o ganho de M; ou aceitar e documentar. Decidir com medição.
- **Mensagem com chave gravada com a ordenação desligada e depois ligada com backlog.** A regra acima (a ordem vale a partir da primeira `sequence`) precisa de teste e de uma linha no `OPERATIONS.md`.

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
