# ADR 0012 — Convivência entre versões

- Status: aceito
- Data: 2026-10-24
- Etapa: 13 (0.2.1)

## Contexto

Um upgrade não troca todas as aplicações de uma vez. Primeiro se migra o schema, depois os serviços sobem para a nova versão um a um, e por um tempo as duas versões dividem o mesmo banco e o mesmo broker. Um rollback faz o caminho inverso: os binários voltam, o schema fica.

Na `0.2.0-alpha`, o `OPERATIONS.md` já mandava voltar aos binários da 0.1 mantendo o schema da 0.2, e avisava que o claim da 0.1 não usa o índice novo. Nenhum teste rodava código da 0.1 sobre esse schema. O teste de upgrade da etapa 8 migra um banco da 0.1 e drena com a 0.2.

A etapa 13 roda os pacotes `0.1.0-alpha` do nuget.org num processo à parte (`tests/Waybill.Tests.LegacyHost`), porque duas versões dos mesmos assemblies não carregam no mesmo processo. O harness recusa um filho que não carregou a `0.1.0-alpha`, para que um pacote velho no cache do NuGet não esvazie os testes. Imagens: `postgres:18-alpine` e `postgres:15-alpine` (CI), RabbitMQ do `RabbitMqFixture`.

## Decisão

1. **A 0.1 sobre o schema da 0.2 é suportada com a ordenação desligada.** Produtor, dispatcher e consumidor da 0.1 funcionam ao lado dos da 0.2 durante o upgrade e sozinhos depois de um rollback. Nada se perde, e o inbox das duas versões descarta as cópias. Isso vale porque o schema da 0.2 só acrescenta: toda coluna nova tem default ou aceita nulo, e os gatilhos novos só agem com `waybill.settings` presente ou com `sequence` não nula.
2. **A ordenação exige que nenhum dispatcher da 0.1 esteja vivo.** O claim da 0.1 não conhece a cabeça da chave e publica a seguinte com a anterior parada. A 0.1 não registra nada que a 0.2 leia (`outbox_instances` e `waybill.settings` são da 0.2), então a regra é do operador, não uma checagem. Produtores da 0.1 podem continuar: o banco numera as mensagens com chave de qualquer versão. Sem o interceptor da 0.2, um produtor da 0.1 é como um contexto montado à mão: uma transação com várias chaves pode dar deadlock com outra. O teste prova uma chave gravada uma transação por vez; o deadlock é o limite que o `OPERATIONS.md` já dava para o contexto montado à mão.
3. **O índice da 0.1 é de quem faz rollback.** O índice de claim da 0.2 tem `is_head` no predicado, e o claim da 0.1 não o usa: depois de um rollback, ele percorreria a chave primária pelas linhas publicadas que a retenção guarda. O `OPERATIONS.md` traz o SQL que recria o índice da 0.1 com o nome `ix_outbox_claimable_v01`, e quem volta à 0.2 o apaga à mão, porque a migration já está no histórico e não roda de novo. A migration publicada não muda.
4. **Espaçamento do `basic.return` não sobrevive à 0.1.** O claim da 0.1 não lê `next_attempt_at`: uma mensagem em espera sai no ciclo seguinte de um dispatcher da 0.1, como sempre saiu na 0.1. Nada se perde; só a espera some.
5. **Cada versão diz o que roda sobre o schema da seguinte.** Uma versão que mexe no schema traz, na etapa de release, os testes de convivência com os pacotes publicados da anterior, e o `OPERATIONS.md` diz o que vale durante o upgrade e o rollback.

## Alternativas descartadas

| Alternativa | Por que não |
| --- | --- |
| Simular a 0.1 copiando o SQL dela para os testes | Prova o SQL copiado, não o pacote que o usuário tem; o host lê até o claim da constante do pacote publicado |
| Carregar a 0.1 por `AssemblyLoadContext` no processo de teste | As dependências comuns (EF Core, Npgsql) teriam de ser isoladas também; um processo à parte é o que roda em produção |
| Detectar um dispatcher da 0.1 com a ordenação ligada | A 0.1 não deixa rastro que a 0.2 leia; o que haveria de vestígio (linhas reivindicadas por um dono desconhecido) aparece só depois do dano |
| Corrigir na migration publicada o índice para servir às duas versões | Um predicado sem `is_head` traria de volta ao índice as linhas paradas atrás da cabeça, o que o ponteiro de cabeça tirou (ADR 0008); e mudar uma migration já publicada custa mais que um SQL de rollback |
| Proibir a convivência e exigir troca de todas as aplicações de uma vez | Não é como se faz deploy; a regra seria ignorada |

## Consequências

- O `OPERATIONS.md`, em "Upgrading from 0.1", diz que as aplicações sobem em qualquer ordem e que a 0.1 continua funcionando até a última subir. Pede os dispatchers cedo, pelo índice e pelo espaçamento, e manda ligar a ordenação só sem dispatcher da 0.1.
- "Before turning it on" ganha a regra do dispatcher da 0.1.
- "Upgrading and rolling back" ganha o SQL do índice da 0.1 (`<!-- rollback-claim-index -->`) e diz quando apagá-lo.
- Depois de um rollback, a retenção da 0.1 apaga só `published`. As linhas `released` ficam, e isso não tem teste nem frase no `OPERATIONS.md`.
- O host da 0.1 restaura do nuget.org em todo build do CI. Um pacote retirado da listagem continua restaurável; um apagado quebraria o build.

## Testes que provam

- `Convivencia_LegacyHost_CarregaA01DoNuGet`: o filho carrega a `0.1.0-alpha` (`+6b04362`, o commit da tag) e lê o claim do pacote; com a versão esperada trocada, o harness recusa o filho.
- `Convivencia_LegacyHost_FilhoQueMorre_FalhaNaHora`: um filho que morre sem a linha esperada falha o teste na hora, dizendo isso, e não depois do tempo-limite (achado da revisão; a corrida depende do agendamento e não foi vista falhando antes da correção).
- `Convivencia_JanelaDoUpgrade_01E02Juntas_NenhumaSePerde` (decisão 1): 200 mensagens de cada versão, metade com chave, publicadas pelos dois dispatchers; cada um publica ao menos 40, e as linhas da 0.1 ficam sem `sequence`.
- `Convivencia_Consumidor01_Recebe02EDeduplica` (decisão 1): o consumidor da 0.1 lê o que a 0.2 publica com a ordenação ligada, com `waybill-sequence`, e o inbox da 0.1 descarta uma cópia.
- `Convivencia_Rollback_01DrenaBacklogNumerado` (decisão 1): backlog numerado com sucessoras não marcadas, ordenação desligada pelo `<!-- ordering-reset -->`, drenado só pela 0.1.
- `Convivencia_Rollback_LinhaEmEsperaDeReturn_01PublicaNoCicloSeguinte` (decisão 4): a 0.2 deixa a linha esperando; a 0.1 a publica no primeiro ciclo.
- `Convivencia_Rollback_IndiceDa01_ClaimDa01UsaOIndice` (decisão 3): em plano genérico, o claim da 0.1 lê o `pk_outbox` antes do SQL e o `ix_outbox_claimable_v01` depois, sem `Seq Scan`.
- `Convivencia_Produtor01ComOrdenacaoLigada_SaiEmOrdem` (decisão 2): dez mensagens de uma chave gravadas pela 0.1 saem com as sequências 1 a 10, nessa ordem.
- `Convivencia_Dispatcher01ComOrdenacaoLigada_PublicaForaDeOrdem` (decisão 2, o limite): com a cabeça na DLQ, a 0.2 segura a chave e a 0.1 publica a seguinte.

Mutações vistas morrendo:
- SQL do índice da 0.1 com o predicado da 0.2 (`Convivencia_Rollback_IndiceDa01_ClaimDa01UsaOIndice`);
- transporte do host que confirma sem imprimir um id em cada dez (`Convivencia_JanelaDoUpgrade_01E02Juntas_NenhumaSePerde`). A primeira versão do teste não a matava: a 0.2 em laço apertado levava quase tudo, e a 0.1 publicava menos de 50;
- gatilho `outbox_sequence` desabilitado antes de a 0.1 gravar (`Convivencia_Produtor01ComOrdenacaoLigada_SaiEmOrdem`).
