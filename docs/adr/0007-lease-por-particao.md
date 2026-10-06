# ADR 0007 — Lease por partição

- Status: aceito
- Data: 2026-10-06
- Etapa: 8b (segundo PR da ordenação por chave, v0.2)

## Contexto

O spike da etapa 0 mostrou que o filtro de cabeça por chave sozinho só é seguro com M = 1. Com M = 4 houve de 1.268 a 1.936 inversões em três execuções. Com lease por partição, houve 0 inversões em M = 1 e M = 4, mas sem troca de dono durante o teste. A causa das inversões são duas instâncias reivindicando a mesma chave ao mesmo tempo: uma devolve a cabeça enquanto a outra, pelo `SKIP LOCKED`, leva a mensagem seguinte. O ADR 0001 adiou o lease por partição para a v0.2, como camada sobre o claim por linha, e deixou riscos abertos: o `epoch` fora do fencing, P e lease que precisam ser globais e a coleta de instâncias mortas.

## Decisão

1. **A ordenação é opcional e desligada por padrão** (`WaybillOptions.OrderByKey`). Desligada, nenhuma tabela de posse é lida ou escrita e o claim é o da v0.1. Mensagem sem chave nunca é ordenada: sai por qualquer instância, também durante um failover.
2. **Partição = `key_hash % P`.** O `key_hash` é o murmur2 do Kafka mascarado em 31 bits, gravado desde a v0.1 (ADR 0002).
3. **P e o lease da partição são globais.** A primeira instância que ordena grava os dois em `waybill.settings` e cria as P linhas de `outbox_partitions`, num comando só. As outras comparam no startup e a cada `PartitionLease`. Divergência é erro crítico: o laço para e o health check acusa. Uma instância sem ordenação diante de `settings` também para, porque reivindicaria chaves sem filtro ao lado de quem ordena. Ligar, desligar ou mudar P exige parar todos os dispatchers e limpar o estado com o SQL do `OPERATIONS.md`, que um teste executa como está escrito.
4. **Manutenção a cada ciclo, antes do claim e com nada em voo, também com o breaker aberto.** A instância grava o heartbeat em `outbox_instances` e coleta as instâncias sem heartbeat há dez leases de partição. Depois renova as partições que ainda são dela e com lease válido: partição vencida está perdida, mesmo que ninguém a tenha tomado. Calcula a fatia justa, `ceil(P / instâncias vivas)`, e adquire partições livres ou vencidas até ela, com `epoch + 1`. Ou devolve as que passam dela. A aquisição segue a regra do ADR 0001: a condição fica no nível do `FOR UPDATE SKIP LOCKED` e é repetida no `UPDATE` externo.
5. **O claim verifica a posse no próprio comando.** Uma linha com chave só é reivindicável por quem tem a partição dela e enquanto o lease da partição durar mais que o lease da linha que vai ser tomado, medido pelo relógio do comando: `key IS NULL OR EXISTS (… p.owner = eu AND p.lease_until > clock_timestamp() + lease da linha)`. O `PartitionLease` precisa ser pelo menos o dobro do lease da linha, e a validação exige isso quando a ordenação está ligada.
6. **O `epoch` não entra no fencing da linha.** Ele identifica um mandato e serve ao transporte (o producer por partição da etapa 9 é recriado quando ele muda) e ao diagnóstico. Na linha, o fencing por `owner` e `fence` já barra a marcação atrasada. O item 5 já impede claim fora do mandato.
7. **Shutdown:** a instância devolve as linhas, depois as partições, e apaga a própria linha de instância. Quem fica redistribui a fatia no ciclo seguinte, sem esperar o lease.

## Por que o `EXISTS`, e não só a lista em memória

A proposta do SPEC era `key_hash % P = ANY(@minhas)`, com a lista de partições que a instância guarda em memória, sem tocar a tabela de posse no claim. Não basta. Uma instância pausada logo depois de renovar (GC longo, VM congelada) acorda com a lista velha, depois de outra ter assumido a partição. As duas passam a reivindicar a mesma chave, e é exatamente o que gera a inversão com M ≥ 2.

O `EXISTS` lê a posse no snapshot do comando. A armadilha do ADR 0001 é que, ao travar uma linha alterada por outra transação, o PostgreSQL reavalia só os predicados da tabela travada, e uma subconsulta em outra tabela usa a versão do snapshot. Essa armadilha não morde aqui. A versão do snapshot só pode estar desatualizada se outra instância tiver assumido a partição durante o comando. Isso exige que o `lease_until` tenha passado. Mas o claim exige que ele esteja além do relógio do comando somado a um lease de linha inteiro.

## Alternativas descartadas

| Alternativa | Por que não |
| --- | --- |
| Lista de partições só em memória (`ANY(@minhas)`) | Instância pausada reivindica depois de perder a partição (acima) |
| Advisory lock de sessão por partição | Descartado no ADR 0001: PgBouncer em modo transaction, sem token de fencing, conexão meio-aberta segura o lock por horas |
| Partições atribuídas por configuração, sem posse no banco | Exige que o operador mantenha a atribuição a cada escala, e uma instância morta deixa as chaves paradas até alguém agir |
| Ordenação sempre ligada | O spike mediu de 16% a 70% a mais no p99 da transação e de 35% a 38% da vazão do claim; quem não precisa de ordem não deve pagar |

## Consequências

- No failover, as chaves de uma instância morta esperam até um `PartitionLease` (60 s por padrão) para mudar de dono. As mensagens sem chave não esperam.
- Instâncias além de P ficam ociosas para mensagens com chave.
- Cada claim lê e pula as linhas com chave de partições alheias. O custo disso na vazão é medido na 8c, com o critério escrito no SPEC.
- A manutenção custa de dois a três round trips por ciclo por instância, inclusive ociosa.
- O teste de caos achou um furo no próprio oráculo antes de achar algum no código. A primeira versão do trigger encerrava o mandato anterior na hora da troca de `epoch`, e assim um roubo de partição parecia uma passagem limpa. Corrigido, uma aquisição que rouba partições válidas gera milhares de sobreposições.

## Testes que provam

`G4_OrdenacaoDesligada_ClaimDaV01SemTabelasDePosse`, `G4_PrimeiraInstancia_CriaConfiguracaoEParticoes`, `G4_PDiferenteDoBanco_ErroCriticoENaoReivindica`, `G4_OrdenacaoDesligadaComConfiguracaoNoBanco_Para`, `G4_ClaimSoDasParticoesDaInstancia`, `G4_DonoAntigoDaParticao_NaoReivindica`, `G4_FatiaJusta_ConvergeAoEntrarESair`, `Operacao_ReiniciarOrdenacao` (integração); `G4_EntradaESaidaSobCarga_NenhumaParticaoComDoisDonos`, com os oráculos de mandato e de claim fora do mandato (caos, 20 s no PR e 10 min no agendado); `Dispatcher_PartitionLease_SoValidadoComOrdenacao` e `Dispatcher_IntervaloForaDoLimite_FalhaNaPartidaDizendoQual` (unidade).

Verificado por mutação:
- trocar a posse no claim por "a partição tem dono" é pego pelo teste do dono antigo, pelo de claim só das próprias partições e pelo oráculo de claim fora do mandato (4006 claims);
- adquirir sem exigir partição livre ou vencida é pego pelo oráculo de sobreposição (11692);
- não adquirir partição vencida é pego pelos testes da fatia justa e do dono antigo;
- ignorar a divergência de configuração é pego pelos quatro casos de parada crítica.
