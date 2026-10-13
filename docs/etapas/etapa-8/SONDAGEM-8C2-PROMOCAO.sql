-- Sondagem da 8c-2 (Oct 13, 2026, PG 18): a promoção do ponteiro em laço, achada pela revisão de concorrência.
-- Duas terminações concorrentes na mesma chave (zumbi e novo dono, DELETE manual) deixavam a chave sem cabeça marcada.
-- Verde nas intercalações: zumbi publica m2 enquanto o novo dono publica m1, nas duas ordens; m2 falso positivo;
-- DELETE de m2; publicação parcial; série de 100 publicada num UPDATE só com uma promoção (trigger só para linha marcada).
CREATE OR REPLACE FUNCTION waybill.outbox_head_promote() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog, waybill AS $$
DECLARE target uuid; tseq bigint;
BEGIN
    IF current_setting('transaction_isolation') <> 'read committed' THEN
        RAISE EXCEPTION 'needs READ COMMITTED' USING ERRCODE = 'object_not_in_prerequisite_state';
    END IF;
    LOOP
        SELECT n.id, n.sequence INTO target, tseq FROM waybill.outbox n
        WHERE n.key = OLD.key AND n.sequence IS NOT NULL AND n.status IN ('pending', 'claimed', 'dlq')
        ORDER BY n.sequence LIMIT 1;
        -- nothing left, or an earlier row is still unfinished: whoever finishes that one promotes
        EXIT WHEN target IS NULL OR tseq < OLD.sequence;
        -- wait for whoever is changing the target (a producer's FOR SHARE, a concurrent finisher); status re-checked
        PERFORM 1 FROM waybill.outbox WHERE id = target AND status IN ('pending', 'claimed', 'dlq') FOR NO KEY UPDATE;
        IF FOUND THEN
            UPDATE waybill.outbox SET is_head = true WHERE id = target AND is_head IS NOT TRUE;
            EXIT;
        END IF;
        -- finished or deleted meanwhile: look again, with a new snapshot
    END LOOP;
    RETURN NULL;
END $$;

CREATE OR REPLACE TRIGGER outbox_head_promote
    AFTER UPDATE OF status ON waybill.outbox
    FOR EACH ROW WHEN (OLD.sequence IS NOT NULL AND OLD.status IN ('pending', 'claimed', 'dlq')
                       AND NEW.status IN ('published', 'released') AND OLD.is_head IS TRUE)
    EXECUTE FUNCTION waybill.outbox_head_promote();
