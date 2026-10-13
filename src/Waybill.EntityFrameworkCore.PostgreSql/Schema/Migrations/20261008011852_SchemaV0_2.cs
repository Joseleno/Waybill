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
            migrationBuilder.Sql("""
                ALTER TABLE waybill.outbox
                    ADD COLUMN IF NOT EXISTS next_attempt_at timestamp with time zone,
                    ADD COLUMN IF NOT EXISTS lock_keys text[],
                    ADD COLUMN IF NOT EXISTS released_at timestamp with time zone,
                    ADD COLUMN IF NOT EXISTS released_by text;

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

            migrationBuilder.Sql("ALTER TABLE waybill.outbox VALIDATE CONSTRAINT ck_outbox_status;", suppressTransaction: true);

            // An index left invalid by an interrupted CONCURRENTLY build would be kept by IF NOT EXISTS; drop it first.
            // A key's head is its first row not yet published or released; with ordering off no row has a sequence, so
            // neither index holds anything.
            CreateIndexConcurrently(migrationBuilder, "ix_outbox_key_sequence",
                "(key, sequence) WHERE sequence IS NOT NULL AND status IN ('pending', 'claimed', 'dlq')");
            CreateIndexConcurrently(migrationBuilder, "ix_outbox_blocked_keys",
                "(key) WHERE status = 'dlq' AND sequence IS NOT NULL");
        }

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
                DROP FUNCTION IF EXISTS waybill.outbox_sequence();
                DROP FUNCTION IF EXISTS waybill.outbox_terminal_guard();
                DROP INDEX IF EXISTS waybill.ix_outbox_key_sequence;
                DROP INDEX IF EXISTS waybill.ix_outbox_blocked_keys;

                ALTER TABLE waybill.outbox DROP CONSTRAINT IF EXISTS ck_outbox_status;
                ALTER TABLE waybill.outbox ADD CONSTRAINT ck_outbox_status CHECK (status IN ('pending', 'claimed', 'published', 'dlq'));
                ALTER TABLE waybill.outbox
                    DROP COLUMN IF EXISTS next_attempt_at,
                    DROP COLUMN IF EXISTS lock_keys,
                    DROP COLUMN IF EXISTS released_at,
                    DROP COLUMN IF EXISTS released_by;

                DROP TABLE IF EXISTS waybill.outbox_keys;
                DROP TABLE IF EXISTS waybill.settings;
                DROP TABLE IF EXISTS waybill.outbox_partitions;
                DROP TABLE IF EXISTS waybill.outbox_instances;
                """);
        }
    }
}
