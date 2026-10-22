-- Sondagem da 8c-2 (Oct 13, 2026): o claim com M >= 2 no esquema real, com as correções da revisão de desempenho
-- (prefixo sem junção, trava e UPDATE por chave primária, teto de sucessoras por cabeça). Medido em PG 15.19 e 18.6;
-- resultados no PLAN.md, seção 8c-2. Parâmetros: $1 dono, $2 lease, $3 lote efetivo B, $4 P, $5 M.
-- A versão de produção repete também a posse da partição no UPDATE externo, como o ClaimOrderedSql.
PREPARE claim7(text, interval, int, int, int) AS
WITH now AS MATERIALIZED (SELECT clock_timestamp() AS t),
heads AS MATERIALIZED (
    SELECT o.id, o.key, o.sequence FROM waybill.outbox o, now
    WHERE o.status IN ('pending', 'claimed') AND (o.sequence IS NULL OR o.is_head)
      AND (o.status = 'pending' OR o.lease_until < now.t)
      AND (o.next_attempt_at IS NULL OR o.next_attempt_at <= now.t)
      AND (o.key IS NULL OR EXISTS (
            SELECT 1 FROM waybill.outbox_partitions p
            WHERE p.partition = o.key_hash % $4 AND p.owner = $1 AND p.lease_until > now.t + $2))
      AND (o.sequence IS NULL OR o.sequence = (
            SELECT min(h.sequence) FROM waybill.outbox h
            WHERE h.key = o.key AND h.sequence IS NOT NULL AND h.status IN ('pending', 'claimed', 'dlq')))
    ORDER BY o.id
    LIMIT $3
    FOR UPDATE OF o SKIP LOCKED),
budget AS MATERIALIZED (
  SELECT $3 - count(*) AS n,
         greatest(count(*) FILTER (WHERE h.sequence < (SELECT max(m.sequence) FROM waybill.outbox m
                    WHERE m.key = h.key AND m.sequence IS NOT NULL AND m.status IN ('pending','claimed','dlq'))), 1) AS k
  FROM heads h),
series AS (
  SELECT n.id, h.id AS head, n.rank,
         bool_and(n.claimable) OVER (PARTITION BY h.id ORDER BY n.rank) AS unbroken
  FROM heads h, budget b, now,
  LATERAL (
    SELECT s.id, row_number() OVER (ORDER BY s.sequence) AS rank,
           (s.status = 'pending' OR (s.status = 'claimed' AND s.lease_until < now.t))
             AND (s.next_attempt_at IS NULL OR s.next_attempt_at <= now.t) AS claimable
    FROM (SELECT id, sequence, status, lease_until, next_attempt_at FROM waybill.outbox
          WHERE key = h.key AND sequence > h.sequence AND sequence IS NOT NULL AND status IN ('pending','claimed','dlq')
          ORDER BY sequence LIMIT least($5 - 1, (b.n + b.k - 1) / b.k)) s) n
  WHERE h.sequence IS NOT NULL AND b.n > 0),
ranked AS MATERIALIZED (
  SELECT q.id, q.head, q.rank FROM (
    SELECT id, head, rank, row_number() OVER (ORDER BY rank, head) AS k FROM series WHERE unbroken) q, budget b
  WHERE q.k <= b.n),
locked AS MATERIALIZED (
  SELECT x.id, r.head, r.rank FROM ranked r, now,
  LATERAL (SELECT x.id FROM waybill.outbox x
           WHERE x.id = r.id
             AND (x.status = 'pending' OR (x.status = 'claimed' AND x.lease_until < now.t))
             AND (x.next_attempt_at IS NULL OR x.next_attempt_at <= now.t)
           FOR UPDATE SKIP LOCKED) x),
prefix AS (
  SELECT id, rank, row_number() OVER (PARTITION BY head ORDER BY rank) = rank AS unbroken FROM locked),
cut AS (
  SELECT id, 0::bigint AS rank FROM heads
  UNION ALL
  SELECT id, rank FROM prefix WHERE unbroken)
UPDATE waybill.outbox o SET status = 'claimed', owner = $1, fence = o.fence + 1, lease_until = now.t + $2
FROM now WHERE o.id = ANY ((SELECT array_agg(id) FROM cut)::uuid[])
  AND (o.status = 'pending' OR (o.status = 'claimed' AND o.lease_until < now.t))
  AND (o.next_attempt_at IS NULL OR o.next_attempt_at <= now.t)
RETURNING o.id, o.type, o.key, o.payload, o.content_type, o.headers, o.created_at, o.fence, o.sequence,
          o.sequence IS NOT NULL AND o.sequence < (
              SELECT max(s.sequence) FROM waybill.outbox s
              WHERE s.key = o.key AND s.sequence IS NOT NULL AND s.status IN ('pending', 'claimed', 'dlq')),
          0::bigint;
