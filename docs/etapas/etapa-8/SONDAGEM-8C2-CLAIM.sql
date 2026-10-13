-- Sondagem da 8c-2 (Oct 13, 2026, PG 18): o claim com M ≥ 2, numa tabela reduzida às colunas do claim.
-- $1 dono, $2 lote efetivo B, $3 M. Os índices são os da migration: ix_outbox_claimable (aqui ix_cl) e ix_outbox_key_sequence (ix_ks).
-- Resultado e casos conferidos: PLAN.md, seção 8c-2.
PREPARE claim(text, int, int) AS
WITH now AS MATERIALIZED (SELECT clock_timestamp() AS t),
heads AS MATERIALIZED (
  SELECT x.id, x.key, x.sequence FROM o x, now
  WHERE x.status IN ('pending','claimed') AND (x.sequence IS NULL OR x.is_head)
    AND (x.status = 'pending' OR x.lease_until < now.t)
    AND (x.sequence IS NULL OR x.sequence = (SELECT min(h.sequence) FROM o h WHERE h.key = x.key AND h.sequence IS NOT NULL AND h.status IN ('pending','claimed','dlq')))
  ORDER BY x.id LIMIT $2 FOR UPDATE OF x SKIP LOCKED),
budget AS MATERIALIZED (SELECT $2 - count(*) AS n FROM heads),
series AS (
  SELECT n.id, h.id AS head, n.rank,
         bool_and(n.claimable) OVER (PARTITION BY h.id ORDER BY n.rank) AS unbroken
  FROM heads h, budget b, now,
  LATERAL (
    SELECT s.id, row_number() OVER (ORDER BY s.sequence) AS rank,
           (s.status = 'pending' OR (s.status = 'claimed' AND s.lease_until < now.t))
             AND (s.next_attempt_at IS NULL OR s.next_attempt_at <= now.t) AS claimable
    FROM (SELECT id, sequence, status, lease_until, next_attempt_at FROM o
          WHERE key = h.key AND sequence > h.sequence AND sequence IS NOT NULL AND status IN ('pending','claimed','dlq')
          ORDER BY sequence LIMIT least($3 - 1, b.n)) s) n
  WHERE h.sequence IS NOT NULL AND b.n > 0),
ranked AS MATERIALIZED (
  SELECT q.id, q.head, q.rank FROM (
    SELECT id, head, rank, row_number() OVER (ORDER BY rank, head) AS k FROM series WHERE unbroken) q, budget b
  WHERE q.k <= b.n),
locked AS MATERIALIZED (
  SELECT x.id FROM o x, now WHERE x.id IN (SELECT id FROM ranked)
    AND (x.status = 'pending' OR (x.status = 'claimed' AND x.lease_until < now.t))
    AND (x.next_attempt_at IS NULL OR x.next_attempt_at <= now.t)
  FOR UPDATE OF x SKIP LOCKED),
prefix AS (
  SELECT r.id, r.rank, bool_and(l.id IS NOT NULL) OVER (PARTITION BY r.head ORDER BY r.rank) AS unbroken
  FROM ranked r LEFT JOIN locked l ON l.id = r.id),
cut AS (
  SELECT id, 0::bigint AS rank FROM heads
  UNION ALL
  SELECT id, rank FROM prefix WHERE unbroken)
UPDATE o SET status = 'claimed', owner = $1, fence = o.fence + 1, lease_until = now.t + interval '30s'
FROM cut c, now WHERE o.id = c.id
  AND (o.status = 'pending' OR (o.status = 'claimed' AND o.lease_until < now.t))
RETURNING o.id, o.key, o.sequence, c.rank;
