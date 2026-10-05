# Etapa 6 — Fricção encontrada

Fonte: um agente sem contexto seguiu só o `samples/README.md` e os arquivos que ele cita (Oct 5, 2026). **Ele publicou o primeiro evento só com o README**: 38 s de `docker compose up --wait` e cerca de 4,5 min do início à limpeza. Os scripts de falha do broker (bash e PowerShell), o smoke e os testes do exemplo passaram como escritos.

## As quatro perguntas do plano

| Pergunta | Resposta | Destino |
| --- | --- | --- |
| Quantas linhas de configuração para começar? | 7 instruções em 11 linhas no `Program.cs` do Billing, mais 5 `using`, mais `modelBuilder.AddWaybillOutbox()` e o registro das mensagens (~5 linhas). Acima de dez | ADR 0005 reduz: connection string lida do `DbContext` e registros sem `using` |
| Os fakes bastam para testar o caso de uso e o handler? | Sim no caminho feliz. Faltam: o nome do pacote `Waybill.Testing` no README, o significado de `Saved`, e como testar falha do handler (o provider em memória não tem transação; os fakes documentam o limite) | README do exemplo cita o pacote; o limite já está nos `<remarks>` dos fakes e passa a ser citado no README |
| Os nomes fazem sentido para quem não escreveu o pacote? | Confundiram: o health check com o mesmo nome do registro do dispatcher, `AddWaybillOutbox` em dois lugares com sentidos diferentes, `AddWaybill` (parece registrar tudo), `Enqueue` (parece enviar já), `key` sem explicação, o `db` do callback do inbox | ADR 0005: `AddWaybillDispatcherCheck()` e `MapWaybillOutbox()`. README explica `AddWaybill`, `Enqueue`, `key` e o `db` do inbox |
| O que acontece quando o usuário esquece de registrar um tipo? | A exceção cita o tipo e `AddMessage`, no fake e no real | Teste no exemplo (`Enqueue_UnregisteredType_ErrorSaysWhatToDo`) |

## Fricções e destino

| # | Fricção | Severidade | Destino |
| --- | --- | --- | --- |
| 1 | Snippet com `INV-1` fixo: rodar duas vezes dá 500 sem corpo (número único) e, no bash, falha em silêncio | annoying | Exemplo: número gerado no snippet; a API devolve 409 para número repetido; snippet bash com `curl -f` |
| 2 | A consulta do recibo corre contra o dispatcher e volta vazia | annoying | README: o snippet espera o recibo |
| 3 | No PowerShell nada aparece em caso de sucesso | annoying | Exemplo: o pagamento devolve a fatura paga |
| 4 | Pré-requisitos incompletos (.NET 10 SDK para os testes, pwsh, bash) | nit | README |
| 5 | Diretórios de trabalho diferentes sem aviso | nit | README diz de onde roda cada comando |
| 6 | `AddHealthChecks().AddWaybillDispatcher()` lido como registro duplicado | annoying | ADR 0005: `AddWaybillDispatcherCheck()` |
| 7 | `AddWaybillOutbox` em serviços e no `modelBuilder` | annoying | ADR 0005: `modelBuilder.MapWaybillOutbox()`; README diz o que acontece se faltar um |
| 8 | Connection string passada quatro vezes | annoying | ADR 0005: `AddWaybillDispatcher<TContext>()` e `AddWaybillRetention<TContext>()` |
| 9 | Cinco `using` só para a configuração | nit | ADR 0005: registros em `Microsoft.Extensions.DependencyInjection`, mapeamento em `Microsoft.EntityFrameworkCore` |
| 10 | A configuração do inbox não aparece em nenhum arquivo citado | annoying | README cita `Receipts/Program.cs` |
| 11 | Não é claro se o `db` do callback é o contexto do `IOutbox` injetado, nem se o handler deve chamar `SaveChanges` | annoying | README e comentário no handler: é o contexto do escopo, o mesmo do outbox; o inbox salva e faz commit |
| 12 | `key` do `Enqueue` sem explicação | annoying | README: opcional, identifica o agregado, vai no header `waybill-key`; ordenação por chave só na v0.2 |
| 13 | `MaxPayloadBytes` dentro do registro de mensagens parece arbitrário | nit | Exemplo: comentário dizendo que é obrigatório e por quê |
| 14 | Headers `x-dotnet-pub-seq-no` e `x-acquired-count` sem explicação; `traceparent` some no evento publicado pelo consumidor | nit | README explica que são do cliente e do broker. O trace no consumidor depende de a aplicação abrir um `Activity` com o `traceparent` recebido: sem OpenTelemetry na v0.1, registrado como limite |
| 15 | O README mostra só o outbox do Billing | nit | README cita o outbox do Receipts |
| 16 | O link dos testes cobre só o Billing | nit | README cita os dois |
| 17 | Nomes de teste em português num exemplo em inglês | annoying | Exemplo: nomes em inglês. Os testes do pacote seguem em português (regra do projeto) |
| 18 | A limpeza deixa as imagens | nit | README: `--rmi local` |
| 19 | Pagamento devolve 200 sem corpo; número repetido dá 500 | nit | Ver 1 e 3 |
| 20 | O consumidor reenfileira para sempre uma mensagem que sempre falha | nit | Exemplo: fila quorum com `x-delivery-limit` e dead-letter para `receipts.invoice-paid.dead`; o broker para a repetição |

## Segunda passada (Oct 5, 2026)

Outro agente sem contexto repetiu tudo depois das correções. **Publicou o primeiro evento só com o README, em Bash e em PowerShell, sem nenhum comando falhando.** Os dois scripts de falha do broker, o smoke e os testes passaram. Ele mediu 12 linhas de configuração do Waybill no `Program.cs` do Billing, contando o `using` e o `MigrateAsync`, e achou a configuração clara. O que ainda apareceu, e o destino:

| Fricção | Destino |
| --- | --- |
| Os logs dizem `Error loading shared library libgssapi_krb5.so.2`: o Npgsql procura Kerberos e a imagem alpine não tem a biblioteca | Os Dockerfiles instalam `krb5-libs` |
| `fail:` sobre `__EFMigrationsHistory` no primeiro migrate | É o EF verificando a tabela de histórico antes de criá-la; o README avisa |
| O README não citava `ReceiptsDbContext.cs` nem `ReceiptsMessages.cs`, nem dizia se o inbox precisa de mapeamento | README cita os dois e diz que o inbox não precisa de mapeamento |
| O snippet esperava 2 s fixos | O snippet tenta por até 10 s |
| `key` "names the aggregate", mas o recibo usa o id da fatura | README: identifica a entidade cujas mensagens andam juntas (a fatura, também para o recibo dela) |
| `x-acquired-count` só aparece depois de a mensagem ser lida | README corrigido |
| `AddWaybill` registra só mensagens e opções; os fakes têm formatos diferentes (`FakeOutbox.ShouldContain` e `FakeInbox.Memory.ShouldHaveProcessed`) | Registrados. Não mudam na v0.1 (o README explica `AddWaybill`); a assimetria dos fakes vem da memória do inbox compartilhada entre escopos |

## Revisão de código (Oct 5, 2026)

A revisão independente não achou nada crítico. O que motivou correção:

- **Pagamento concorrente.** Dois pagamentos simultâneos da mesma fatura publicavam dois eventos. Agora `Status` é token de concorrência, e o perdedor descarta o que enfileirou. Isso revelou um desvio do fake: o `FakeOutbox` não descartava a mensagem num `ChangeTracker.Clear()`, e o real descarta. O fake passou a acompanhar o change tracker, com cenário novo no teste de equivalência. Verificado também contra o banco real: dez faturas, cada uma com pagamentos simultâneos, deram dez eventos.
- **Healthcheck do Postgres.** Ele podia dar saudável durante o `init.sql`. Passou a checar por TCP.
- **Dead-letter.** Mensagens boas iam para o dead-letter depois de uns 5 s de banco fora. Agora há espera crescente (1, 2, 4… 30 s) e `x-delivery-limit` 20.
- **Fatura duplicada e entrada inválida.** O número repetido tinha corrida e caía em 500, e a entrada não era validada. Agora: 409 pela violação de unicidade, 400 para entrada inválida.
- **Sobrecargas `<TContext>`.** O contexto não registrado passou a ter mensagem clara. Com `UseNpgsql(NpgsqlDataSource)` o Npgsql omite a senha (verificado), então isso ficou documentado.
- **Menores:**
  - ack separado do handler;
  - reconexão do consumidor em qualquer falha;
  - filas sem consumidor com teto;
  - `/receipts` limitado;
  - scripts que religam o broker mesmo em falha, e o `.ps1` com leitura estrita;
  - timeouts no job `sample`;
  - aviso de que as credenciais são só do exemplo;
  - portas configuráveis por variável: na máquina do autor, um RabbitMQ local já ocupava a 15672.

Publicar as portas só em `127.0.0.1` foi tentado e revertido. No Windows, `localhost` tenta o IPv6 primeiro e cada requisição do PowerShell passou a levar 2 s. Publicar também em `[::1]` quebraria quem tem IPv6 desligado no Docker. O compose avisa que as portas ficam acessíveis na rede.
