using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Waybill.EntityFrameworkCore.Schema.Migrations
{
    /// <summary>
    /// The v0.2 schema: the wait after a basic.return (ADR 0006), partition ownership (ADR 0007) and ordering by key
    /// (ADR 0008). Written as plain SQL, every step idempotent: the indexes are built CONCURRENTLY, outside the
    /// migration's transaction, so a failure there leaves the earlier steps committed and the history unwritten, and the
    /// next run must finish the job. Nothing rewrites a pending row.
    /// </summary>
    internal partial class SchemaV0_2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The ALTER TABLE takes an exclusive lock: behind a long transaction it would hold every producer waiting.
            // is_head (the head pointer) has a constant default, so adding it touches only the catalog.
            migrationBuilder.Sql("""
                SET LOCAL lock_timeout = '10s';

                ALTER TABLE waybill.outbox
                    ADD COLUMN IF NOT EXISTS next_attempt_at timestamp with time zone,
                    ADD COLUMN IF NOT EXISTS lock_keys text[],
                    ADD COLUMN IF NOT EXISTS released_at timestamp with time zone,
                    ADD COLUMN IF NOT EXISTS released_by text,
                    ADD COLUMN IF NOT EXISTS is_head boolean DEFAULT true;

                CREATE TABLE IF NOT EXISTS waybill.settings (
                    id integer NOT NULL,
                    partitions integer NOT NULL,
                    partition_lease interval NOT NULL,
                    created_at timestamp with time zone NOT NULL DEFAULT (clock_timestamp()),
                    CONSTRAINT pk_settings PRIMARY KEY (id),
                    CONSTRAINT ck_settings_single_row CHECK (id = 1));

                CREATE TABLE IF NOT EXISTS waybill.outbox_partitions (
                    partition integer NOT NULL,
                    owner text,
                    epoch bigint NOT NULL DEFAULT 0,
                    lease_until timestamp with time zone,
                    CONSTRAINT pk_outbox_partitions PRIMARY KEY (partition));

                CREATE TABLE IF NOT EXISTS waybill.outbox_instances (
                    owner text NOT NULL,
                    heartbeat_at timestamp with time zone NOT NULL,
                    CONSTRAINT pk_outbox_instances PRIMARY KEY (owner));

                CREATE TABLE IF NOT EXISTS waybill.outbox_keys (
                    key text NOT NULL,
                    seq bigint NOT NULL,
                    CONSTRAINT pk_outbox_keys PRIMARY KEY (key))
                WITH (fillfactor = 80);
                """);

            // 'released' joins the statuses. Replacing a CHECK takes an exclusive lock, so the new one is added NOT VALID
            // (no scan under that lock) and validated below, in a transaction of its own that lets writes through.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint
                                   WHERE conrelid = 'waybill.outbox'::regclass AND conname = 'ck_outbox_status'
                                     AND pg_get_constraintdef(oid) LIKE '%released%') THEN
                        ALTER TABLE waybill.outbox DROP CONSTRAINT IF EXISTS ck_outbox_status;
                        ALTER TABLE waybill.outbox ADD CONSTRAINT ck_outbox_status
                            CHECK (status IN ('pending', 'claimed', 'published', 'dlq', 'released')) NOT VALID;
                    END IF;
                END $$;
                """);

            // Ordering by key (ADR 0008). While the settings row exists, every keyed row is numbered here, in the
            // statement that inserts it: the key's counter row stays locked until the application's transaction commits,
            // so sequences follow commit order and roll back with it. lock_keys carries the distinct keys of the
            // SaveChanges when there are two or more; they are locked once per transaction, in one order for everyone,
            // at the first outbox row, so two transactions never wait on each other's keys. New keys first (inserted, in
            // order), then all of them FOR UPDATE, in order; the stage 8c-1 prototype measured why (PROTOTIPO-CONTADOR.md).
            // SECURITY DEFINER: an application role allowed only to insert into the outbox still enqueues.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION waybill.outbox_sequence() RETURNS trigger
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, waybill AS $$
                BEGIN
                    IF NEW.key IS NULL OR NOT EXISTS (SELECT 1 FROM waybill.settings) THEN
                        NEW.lock_keys := NULL;
                        RETURN NEW;
                    END IF;
                    IF NEW.lock_keys IS NOT NULL
                       AND current_setting('waybill.locked_keys', true) IS DISTINCT FROM NEW.lock_keys::text THEN
                        INSERT INTO waybill.outbox_keys (key, seq)
                        SELECT key, 0 FROM (SELECT DISTINCT k AS key FROM unnest(NEW.lock_keys) k) s
                        ORDER BY key COLLATE "C"
                        ON CONFLICT (key) DO NOTHING;
                        PERFORM 1 FROM waybill.outbox_keys WHERE key = ANY (NEW.lock_keys) ORDER BY key COLLATE "C" FOR UPDATE;
                        PERFORM set_config('waybill.locked_keys', NEW.lock_keys::text, true);
                    END IF;
                    NEW.lock_keys := NULL;
                    INSERT INTO waybill.outbox_keys AS k (key, seq) VALUES (NEW.key, 1)
                    ON CONFLICT (key) DO UPDATE SET seq = k.seq + 1
                    RETURNING seq INTO NEW.sequence;
                    -- The head pointer. Behind a row of this transaction (the last one numbered), never a head before the
                    -- commit, and that row is invisible to the dispatcher: false, and nothing to check. Outside READ
                    -- COMMITTED the check at commit would fail on a predecessor published meanwhile (40001): true, a
                    -- harmless false positive. Behind an unfinished row of another transaction: decided at commit (null).
                    IF current_setting('waybill.last_keyed', true) = NEW.key || ' ' || (NEW.sequence - 1) THEN
                        NEW.is_head := false;
                    ELSIF current_setting('transaction_isolation') <> 'read committed' THEN
                        NEW.is_head := true;
                    ELSIF (SELECT min(h.sequence) FROM waybill.outbox h
                           WHERE h.key = NEW.key AND h.sequence IS NOT NULL AND h.status IN ('pending', 'claimed', 'dlq')) < NEW.sequence THEN
                        NEW.is_head := NULL;
                    ELSE
                        NEW.is_head := true;
                    END IF;
                    PERFORM set_config('waybill.last_keyed', NEW.key || ' ' || NEW.sequence, true);
                    RETURN NEW;
                END $$;

                CREATE OR REPLACE TRIGGER outbox_sequence
                    BEFORE INSERT ON waybill.outbox
                    FOR EACH ROW WHEN (NEW.key IS NOT NULL OR NEW.lock_keys IS NOT NULL)
                    EXECUTE FUNCTION waybill.outbox_sequence();
                """);

            // published and released are terminal. The ordered claim relies on it: a key's head is decided from the
            // statement's snapshot, and a row that went back to pending would go out after its successors.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION waybill.outbox_terminal_guard() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'waybill.outbox row % is %, a terminal status, and cannot become %', OLD.id, OLD.status, NEW.status
                        USING ERRCODE = 'check_violation';
                END $$;

                CREATE OR REPLACE TRIGGER outbox_terminal_guard
                    BEFORE UPDATE OF status ON waybill.outbox
                    FOR EACH ROW WHEN (OLD.status IN ('published', 'released') AND NEW.status IS DISTINCT FROM OLD.status)
                    EXECUTE FUNCTION waybill.outbox_terminal_guard();
                """);

            // The head pointer: when a key's row finishes (published, released) or is deleted unfinished, its key's first
            // unfinished row becomes claimable. Not tied to the settings row, so a key whose rows were numbered keeps
            // moving across turning ordering off and on. VOLATILE: each query takes a new snapshot, so a promotion that
            // waited for a producer's commit sees that producer's row. SECURITY DEFINER: whoever finishes the row (the
            // dispatcher, an operator's release) needs no right on the mark.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION waybill.outbox_head_promote() RETURNS trigger
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog, waybill AS $$
                BEGIN
                    UPDATE waybill.outbox s SET is_head = true
                    WHERE s.id = (SELECT n.id FROM waybill.outbox n
                                  WHERE n.key = OLD.key AND n.sequence IS NOT NULL AND n.status IN ('pending', 'claimed', 'dlq')
                                  ORDER BY n.sequence
                                  LIMIT 1)
                      AND s.is_head IS NOT TRUE;
                    RETURN NULL;
                END $$;

                CREATE OR REPLACE TRIGGER outbox_head_promote
                    AFTER UPDATE OF status ON waybill.outbox
                    FOR EACH ROW WHEN (OLD.sequence IS NOT NULL AND OLD.status IN ('pending', 'claimed', 'dlq')
                                       AND NEW.status IN ('published', 'released'))
                    EXECUTE FUNCTION waybill.outbox_head_promote();

                CREATE OR REPLACE TRIGGER outbox_head_promote_on_delete
                    AFTER DELETE ON waybill.outbox
                    FOR EACH ROW WHEN (OLD.sequence IS NOT NULL AND OLD.status IN ('pending', 'claimed', 'dlq'))
                    EXECUTE FUNCTION waybill.outbox_head_promote();
                """);

            migrationBuilder.Sql("ALTER TABLE waybill.outbox VALIDATE CONSTRAINT ck_outbox_status;", suppressTransaction: true);

            // An index left invalid by an interrupted CONCURRENTLY build would be kept by IF NOT EXISTS; drop it first.
            // A key's head is its first row not yet published or released; with ordering off no row has a sequence, so
            // neither index holds anything.
            CreateIndexConcurrently(migrationBuilder, "ix_outbox_key_sequence",
                "(key, sequence) WHERE sequence IS NOT NULL AND status IN ('pending', 'claimed', 'dlq')");
            CreateIndexConcurrently(migrationBuilder, "ix_outbox_blocked_keys",
                "(key) WHERE status = 'dlq' AND sequence IS NOT NULL");

            // The head pointer: the claim index keeps only rows a claim can take. Built beside the v0.1 one under a
            // temporary name, swapped in by renames (a short lock), the old one dropped CONCURRENTLY. Every step can run
            // again: a temporary index left behind by a run that had already swapped is dropped at the end.
            CreateIndexConcurrently(migrationBuilder, "ix_outbox_claimable_head", $"(id) WHERE {ClaimablePredicate}");
            migrationBuilder.Sql("""
                SET LOCAL lock_timeout = '10s';
                DO $$
                BEGIN
                    IF pg_get_indexdef('waybill.ix_outbox_claimable'::regclass) NOT LIKE '%is_head%' THEN
                        ALTER INDEX waybill.ix_outbox_claimable RENAME TO ix_outbox_claimable_v01;
                        ALTER INDEX waybill.ix_outbox_claimable_head RENAME TO ix_outbox_claimable;
                    END IF;
                END $$;
                """);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS waybill.ix_outbox_claimable_v01;", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS waybill.ix_outbox_claimable_head;", suppressTransaction: true);
        }

        // Repeated word for word by every SQL that walks the index (OutboxStore), so a generic plan can prove it applies.
        private const string ClaimablePredicate = "status IN ('pending', 'claimed') AND (sequence IS NULL OR is_head)";

        private static void CreateIndexConcurrently(MigrationBuilder migrationBuilder, string name, string definition)
        {
            migrationBuilder.Sql($"""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
                               JOIN pg_namespace n ON n.oid = c.relnamespace
                               WHERE n.nspname = 'waybill' AND c.relname = '{name}' AND NOT i.indisvalid) THEN
                        DROP INDEX waybill.{name};
                    END IF;
                END $$;
                """);
            migrationBuilder.Sql($"CREATE INDEX CONCURRENTLY IF NOT EXISTS {name} ON waybill.outbox {definition};", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM waybill.outbox_keys) OR EXISTS (SELECT 1 FROM waybill.outbox WHERE status = 'released') THEN
                        RAISE EXCEPTION 'The Waybill schema only moves forward once ordering by key has numbered a key or released a row: going back would drop the per-key counters, and a key starting again at 1 looks like a regression to its consumers. Turn ordering off and keep the v0.2 schema (OPERATIONS.md, Rollback).';
                    END IF;
                END $$;

                DROP TRIGGER IF EXISTS outbox_sequence ON waybill.outbox;
                DROP TRIGGER IF EXISTS outbox_terminal_guard ON waybill.outbox;
                DROP TRIGGER IF EXISTS outbox_head_promote ON waybill.outbox;
                DROP TRIGGER IF EXISTS outbox_head_promote_on_delete ON waybill.outbox;
                DROP FUNCTION IF EXISTS waybill.outbox_head_promote();
                DROP FUNCTION IF EXISTS waybill.outbox_sequence();
                DROP FUNCTION IF EXISTS waybill.outbox_terminal_guard();
                DROP INDEX IF EXISTS waybill.ix_outbox_key_sequence;
                DROP INDEX IF EXISTS waybill.ix_outbox_blocked_keys;
                DROP INDEX IF EXISTS waybill.ix_outbox_claimable;
                CREATE INDEX ix_outbox_claimable ON waybill.outbox (id) WHERE status IN ('pending', 'claimed');

                ALTER TABLE waybill.outbox DROP CONSTRAINT IF EXISTS ck_outbox_status;
                ALTER TABLE waybill.outbox ADD CONSTRAINT ck_outbox_status CHECK (status IN ('pending', 'claimed', 'published', 'dlq'));
                ALTER TABLE waybill.outbox
                    DROP COLUMN IF EXISTS next_attempt_at,
                    DROP COLUMN IF EXISTS lock_keys,
                    DROP COLUMN IF EXISTS released_at,
                    DROP COLUMN IF EXISTS released_by,
                    DROP COLUMN IF EXISTS is_head;

                DROP TABLE IF EXISTS waybill.outbox_keys;
                DROP TABLE IF EXISTS waybill.settings;
                DROP TABLE IF EXISTS waybill.outbox_partitions;
                DROP TABLE IF EXISTS waybill.outbox_instances;
                """);
        }
    }
}
