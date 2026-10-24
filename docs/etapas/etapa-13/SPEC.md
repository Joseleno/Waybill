# Etapa 13 — Convivência com a 0.1 e log da ordenação

Derivado de `docs/plano-v0.2.1.md`, seção "Etapa 13", do item 6 do `docs/etapas/etapa-12/FRICCAO.md` e da issue #21. Rascunho de Oct 23, 2026.

## Objetivo

Provar o que o `OPERATIONS.md` já diz sobre a 0.1 e corrigir o que ele não diz: **binários da `0.1.0-alpha` funcionam sobre o schema da 0.2, com a ordenação desligada, durante a janela do upgrade e depois de um rollback.** Cada frase sobre isso passa a ter um teste com os pacotes `0.1.0-alpha` do nuget.org. De quebra, o log de partida passa a dizer se a ordenação está ligada.

## O que já existe

- O `OPERATIONS.md` manda migrar antes de publicar a 0.2 e subir os dispatchers da 0.2 "logo depois", porque o claim da 0.1 não usa o `ix_outbox_claimable` novo, cujo predicado tem `is_head`. Ele não diz o que acontece com produtores e dispatchers da 0.1 nesse intervalo.
- O rollback ("Upgrading and rolling back") manda desligar a ordenação e voltar aos binários da 0.1 com o schema da 0.2. Nenhum teste roda a 0.1 sobre esse schema, e depois do rollback o claim da 0.1 fica sem índice para sempre.
- O teste de upgrade da etapa 8 (`SchemaTests`) migra um banco da 0.1 com backlog e drena com o dispatcher da 0.2. Ele não roda código da 0.1.
- O que a leitura do código prevê para a 0.1 sobre o schema da 0.2:
  - o `INSERT` da 0.1 não traz as colunas novas, e todas têm default ou aceitam nulo;
  - sem `waybill.settings`, o gatilho de numeração não numera nada;
  - o claim da 0.1 ignora `is_head` e `next_attempt_at`;
  - a retenção da 0.1 apaga só `published`, então uma linha `released` fica para sempre.
  
  Com a ordenação ligada, um dispatcher da 0.1 publicaria fora de ordem: ele não respeita a cabeça e não aparece em `outbox_instances`, então a checagem da 0.2 não o vê.
- O agente da etapa 12 viu produtores e um dispatcher da 0.1 funcionando sobre o schema da 0.2, com a ordenação desligada.
- A partida do dispatcher loga só `Waybill dispatcher {Owner} started.` (EventId 20, `WaybillDispatcherService`).
- Issue #21, conferida contra o código:
  - **já corrigido na 8c-2, com teste:** erro na varredura não derruba o ciclo (`G4_Ponteiro_Rede_ErroNaVarredura_NaoDerrubaOCiclo`); âncora da varredura em plano genérico (dois comandos, `G4_Ponteiro_Rede_PlanoGenerico_UsaOIndice`); write skew no `DELETE` (a promoção trava a linha-alvo e confere de novo);
  - **decidido no ADR 0008:** sem `lock_timeout` na marcação, porque esperar não duplica e desistir republica o lote;
  - **ainda aberto, só na migration:** `Up` rodado de novo depois da troca de índice reconstrói o índice temporário; `Down` sem `CONCURRENTLY`; guard do `outbox_head_check` só pelo nome.

## Decisões de desenho

| Tema | Decisão |
| --- | --- |
| Como rodar a 0.1 | Um host à parte, `tests/Waybill.Tests.LegacyHost`, com `PackageReference` para os pacotes `0.1.0-alpha` do nuget.org, versão fixa. É um processo filho, como o `ChaosHost`: duas versões do mesmo assembly não carregam no mesmo processo sem `AssemblyLoadContext`, e um processo à parte é o que roda em produção. O host produz, despacha ou consome por argumento e recebe as conexões do teste. Fora do pacote e da solution de release, dentro do CI |
| Onde | `Waybill.Tests.Integration`, com os contêineres de lá, fora de `Category=Long`. Nomes com prefixo `Convivencia_` e sem `G`, porque o que se prova é o que o `OPERATIONS.md` diz, não uma garantia nova do `GUARANTEES.md` |
| Oráculo | O mesmo da G2: toda mensagem gravada chega ao broker ao menos uma vez e nenhuma fica `pending` ou `claimed` depois do dreno; o inbox, da 0.1 ou da 0.2, absorve as cópias pelo `message_id` |
| Rollback com índice | O `OPERATIONS.md` ganha o SQL que recria o índice da 0.1 (`CREATE INDEX CONCURRENTLY ... WHERE status IN ('pending', 'claimed')`) para quem voltar aos binários da 0.1. Um teste executa exatamente esse SQL e prova que o claim da 0.1 passa a usá-lo, em plano genérico, como os testes estruturais da etapa 8 |
| Ordenação ligada com a 0.1 | Vira limite documentado, com teste: um dispatcher da 0.1 ao lado da ordenação ligada publica uma sucessora antes da cabeça. O `OPERATIONS.md` diz para ligar a ordenação só depois que o último dispatcher da 0.1 saiu. Detectar a 0.1 em tempo de execução não é possível: ela não registra nada que a 0.2 leia |
| Log de partida | O EventId 20 continua o mesmo e ganha os campos: `Waybill dispatcher {Owner} started; ordering by key is on (Partitions = {Partitions}, MaxPerKey = {MaxPerKey}).` ou `...; ordering by key is off.` Um evento só por partida, sem EventId novo |
| Migration publicada | Não muda. Os três itens abertos da #21 só aparecem num `Up` interrompido ou num `Down` antes de qualquer chave numerada. Mexer na migration que já está no nuget.org custa mais que eles |

## Cenários de aceite

| Cenário | Resultado esperado |
| --- | --- |
| Janela do upgrade: schema da 0.2, ordenação desligada; produtor e dispatcher da 0.1 e da 0.2 ao mesmo tempo | Toda mensagem das duas versões chega ao broker; nada fica pendente; um consumidor da 0.1 e um da 0.2 deduplicam pelo inbox |
| Rollback depois da ordenação: backlog numerado, com sucessoras não marcadas; ordenação desligada pelo SQL do `OPERATIONS.md`; só binários da 0.1 | A 0.1 drena tudo, cabeças e sucessoras |
| Rollback, linha em espera de `basic.return` (`next_attempt_at` no futuro) | A 0.1 publica de novo no ciclo seguinte, como na 0.1: o espaçamento some, nada se perde |
| SQL do índice da 0.1, executado como o `OPERATIONS.md` mostra | O claim da 0.1 usa o índice em plano genérico, sem `Seq Scan` |
| Produtor da 0.1 com a ordenação ligada nos dispatchers da 0.2 | O banco numera as mensagens da 0.1, e elas saem na ordem da chave. Sem o interceptor da 0.1, transações com várias chaves travam as chaves na ordem de gravação, como um contexto montado à mão (limite que o `OPERATIONS.md` já diz) |
| Dispatcher da 0.1 com a ordenação ligada por um da 0.2 | A 0.1 publica uma sucessora antes da cabeça da chave. O teste prova o limite que o `OPERATIONS.md` passa a dizer |
| Partida com a ordenação ligada e desligada | O EventId 20 traz o estado e, ligada, P e M |

**Pronto quando** (do plano):
- os cenários passam no CI, com a 0.1 restaurada do nuget.org;
- cada frase do `OPERATIONS.md` sobre a 0.1 sobre o schema da 0.2 aponta para um teste;
- o log de partida diz se a ordenação está ligada, com P e M.

## Decisões do autor (Oct 24, 2026)

As três recomendações, aprovadas com o SPEC:

1. **Sem release na etapa.** O CHANGELOG fica em `[Unreleased]`; a `0.2.1-alpha` sai quando houver correção que afete quem usa a `0.2.0-alpha`. Se um cenário achar defeito no código da 0.2, a release volta à mesa no PR da etapa.
2. **ADR 0012, "Convivência entre versões":** a 0.1 sobre o schema da 0.2 é suportada com a ordenação desligada; a ordenação exige que nenhum dispatcher da 0.1 esteja vivo; o índice da 0.1 é de quem faz rollback. Cada versão seguinte diz o que roda sobre o schema da próxima.
3. **Issue #21:** um comentário com o teste ou o ADR de cada item, os resolvidos marcados, e a issue aberta só com os três da migration.

## Fora da etapa

- Os três itens da migration na #21 (decisão acima).
- 0.2 sobre o schema da 0.1: o `OPERATIONS.md` já manda migrar antes, e o `INSERT` da 0.2 falha sem as colunas novas.
- Detectar um dispatcher da 0.1 com a ordenação ligada (decisão acima).
- Kafka (ADR 0009) e a API da DLQ e de liberação de chave (v1.0).
