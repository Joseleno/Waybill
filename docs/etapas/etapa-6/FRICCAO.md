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
