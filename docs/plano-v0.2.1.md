# Waybill — Plano da 0.2.1

Oct 23, 2026 · @Joseleno

Depois da `0.2.0-alpha` (Oct 22, 2026) nada estava planejado. Este plano tem uma etapa só, de endurecimento do que já foi publicado, sem API nova. A numeração continua a da v0.2, para que `docs/etapas/etapa-N/` não colida.

Origem: o item 6 do `docs/etapas/etapa-12/FRICCAO.md`, a observação do mesmo agente de que a 0.1 continuou funcionando sobre o schema da 0.2 (sem teste no repositório), e a issue #21.

## Etapa 13 — Convivência com a 0.1 e log da ordenação

O `OPERATIONS.md` já diz duas coisas sobre binários da 0.1 sobre o schema da 0.2 sem teste que as prove: que o rollback para a 0.1 mantém o schema da 0.2, e que o claim da 0.1 não usa o índice novo. Esta etapa prova o que vale, documenta o que não vale e fecha o que falta da issue #21.

**Entregas**

- Teste da janela de upgrade e do rollback com os pacotes `0.1.0-alpha` do nuget.org, num processo à parte
- `OPERATIONS.md`: o que funciona durante a janela, o rollback com o índice da 0.1 e quando a ordenação pode ser ligada
- Log de partida do dispatcher com o estado da ordenação
- Issue #21 atualizada com o que a 8c-2 já corrigiu e o que o ADR 0008 decidiu
- ADR da convivência entre versões

**Pronto quando**

- [x] Os cenários do SPEC passam no CI, com a 0.1 restaurada do nuget.org
- [x] Cada frase do `OPERATIONS.md` sobre a 0.1 sobre o schema da 0.2 aponta para um teste
- [x] O log de partida diz se a ordenação está ligada, com P e M
