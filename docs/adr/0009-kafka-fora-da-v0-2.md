# ADR 0009 — Transporte Kafka fora da v0.2

- Status: aceito
- Data: 2026-10-21
- Etapa: entre a 8 e a 10 (v0.2)

## Contexto

O `plano-v0.2.md` tinha o transporte Kafka como etapa 9, de duas semanas, e como parte do núcleo da versão junto com a 8 e a 10. A entrega previa:

- um pacote `Waybill.Kafka`, com a dependência nativa do librdkafka;
- um producer transacional por partição do lease;
- uma mudança na API pública do `ITransport`;
- Kafka e Toxiproxy nos testes de integração e de caos.

Ele é a única forma de ordem estrita do escopo, porque o broker fenceia o producer zumbi.

A justificativa registrada no escopo e na revisão era a "demanda do projeto de exemplo". Essa demanda não se concretizou: os exemplos (`Billing`, `Receipts`) usam RabbitMQ, e nenhum adotante pediu Kafka.

Com a etapa 8 fechada, a ordenação por chave já existe no RabbitMQ (ADR 0008), e a etapa 10 a completa com a verificação de sequência no inbox.

## Decisão

1. **O transporte Kafka sai da v0.2** e fica sem versão até haver um pedido com caso de uso real, na issue #24.
2. **O núcleo da v0.2 passa a ser as etapas 8 e 10.** A ordem segue a do plano: 10 (verificação de sequência no inbox), 11 (OpenTelemetry, ainda cortável) e 12 (documentação e release).
3. **A seção da etapa 9 fica no `plano-v0.2.md`, marcada como adiada.** Ela já tem entregas, cenários e armadilha; quando o Kafka voltar, ela é o ponto de partida do SPEC, revisto à luz do caso de uso que motivar a volta.
4. **A G4 da v0.2 é a do RabbitMQ:** a primeira chegada de cada mensagem segue a ordem de commit, e a verificação no consumidor detecta regressões (ADR 0008). A ordem estrita, que depende de fencing no broker, sai da promessa da versão.
5. **O que já foi feito pensando no Kafka continua:**
   - o `key_hash` é o murmur2 do partitioner padrão do Kafka (ADR 0002 e 0007);
   - o `epoch` da partição existe e não entra no fencing da linha (ADR 0007, item 6).

   Nada disso custa à v0.2, e mantém a porta aberta.
6. **O `ITransport` não muda na v0.2.** A declaração "preservo a ordem dentro do lote", o resultado "fenceado" e a partição com o `epoch` no contrato ficam para o transporte que os usar (ADR 0008, item 19 e alternativas).

## Alternativas descartadas

| Alternativa | Por que não |
| --- | --- |
| Manter a etapa 9 como no plano | É a etapa mais cara e de maior risco da versão. Sem um uso real, o default de P, o custo de conexões por instância e o formato do consumidor seriam chute. A v0.2 sairia cerca de duas semanas depois |
| Kafka sem producer transacional (só `EnableIdempotence`) | Perde o motivo de existir no escopo: sem fencing pelo broker, a ordem no Kafka seria a mesma do RabbitMQ, com um pacote e uma dependência nativa a mais |
| Cortar a etapa 11 (OpenTelemetry) em vez da 9 | O plano já a tinha como cortável. Ainda pode sair se o calendário apertar; o rastreio da requisição até o handler ajuda mais quem adota hoje do que um segundo broker sem demanda |

## Consequências

- **A v0.2 sai mais cedo.** Sobram as etapas 10, 11 e 12, de duas a três semanas pelo plano, contra quatro a cinco com a etapa 9.
- **O escopo muda:**
  - a linha do Kafka na tabela "Funcionalidades por versão" fica sem versão;
  - a G4 descreve só o RabbitMQ para a v0.2;
  - a matriz de suporte e o contrato do Kafka ficam marcados como adiados.

  Os textos da análise de negócio e da revisão ficam como registro do que se pensava.
- **O README** deixa de prometer Kafka na 0.2 e aponta para a issue #24.
- **O ADR 0008 e o ADR 0007** apontam para este ADR onde diziam "etapa 9".
- **Quem precisa de ordem estrita com fencing no broker** não a tem na v0.2. O escopo já não promete ordem estrita de ponta a ponta no RabbitMQ.

## Como volta

Um pedido na issue #24 que descreva o uso: volume, número de partições, como o consumidor roda e se a ordem estrita é necessária. Com ele, a seção da etapa 9 vira SPEC, as decisões "Como o `ITransport` recebe a partição?" e "O que exatamente a G4 promete no Kafka?" do plano são respondidas, e o transporte entra na versão seguinte à decisão.
