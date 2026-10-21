#!/usr/bin/env bash
# Checks the queues' arguments, creates and pays one invoice, waits for the receipt and for both events on the
# `audit` queue, then sends a regression and waits for it in the dead-letter queue.
# Run from anywhere after `docker compose up --wait` in samples/.
set -euo pipefail
cd "$(dirname "$0")/.."

BILLING=${BILLING:-http://localhost:8080}
RECEIPTS=${RECEIPTS:-http://localhost:8081}

wait_for() { # description, seconds, command...
  local description=$1 seconds=$2; shift 2
  for _ in $(seq "$seconds"); do
    if "$@" >/dev/null 2>&1; then echo "ok: $description"; return 0; fi
    sleep 1
  done
  echo "FAILED: $description (after ${seconds}s)" >&2
  exit 1
}

audit_count() {
  docker compose exec -T rabbitmq rabbitmqctl -q list_queues name messages | awk '$1 == "audit" { print $2 }'
}

# The queues must carry the arguments in rabbitmq/definitions.json. RabbitMQ keeps a queue's arguments: definitions
# imported again over an existing queue change nothing, with no error.
queue_arguments=$(docker compose exec -T rabbitmq rabbitmqctl -q list_queues name arguments | awk '$1 == "receipts.invoice-paid"')
for argument in x-single-active-consumer at-least-once reject-publish; do
  if ! grep -q -- "$argument" <<<"$queue_arguments"; then
    echo "FAILED: receipts.invoice-paid lacks $argument; recreate the broker with 'docker compose down -v' (it keeps old queue arguments)" >&2
    exit 1
  fi
done
echo "ok: receipts.invoice-paid has the arguments of definitions.json"

before=$(audit_count)
invoice=$(curl -fsS -X POST "$BILLING/invoices" -H 'Content-Type: application/json' \
  -d "{\"number\":\"INV-$RANDOM$RANDOM\",\"amount\":42.50}")
id=$(echo "$invoice" | sed -E 's/.*"id":"([^"]+)".*/\1/')
echo "invoice $id"

curl -fsS -X POST "$BILLING/invoices/$id/payments" >/dev/null
echo "paid: billing.invoice-paid.v1 is in the outbox, committed with the payment"

wait_for "receipt issued by the receipts service" 60 \
  bash -c "curl -fsS '$RECEIPTS/receipts?invoiceId=$id' | grep -q '\"invoiceId\":\"$id\"'"
wait_for "invoice-paid and receipt-issued on the audit queue" 60 \
  bash -c "[ \$(docker compose exec -T rabbitmq rabbitmqctl -q list_queues name messages | awk '\$1 == \"audit\" { print \$2 }') -ge $((before + 2)) ]"

# A regression, end to end: another message for the same invoice with the sequence already applied (1), as a producer
# restored from a backup would send it. The inbox refuses it (a reused sequence), logs why, and the consumer sends it to
# the dead-letter queue; no second receipt.
dead_count() {
  docker compose exec -T rabbitmq rabbitmqctl -q list_queues name messages | awk '$1 == "receipts.invoice-paid.dead" { print $2 }'
}
dead_before=$(dead_count)
reused=$(cat /proc/sys/kernel/random/uuid 2>/dev/null || uuidgen)
payload="{\\\"InvoiceId\\\":\\\"$id\\\",\\\"Number\\\":\\\"INV-REUSED\\\",\\\"Amount\\\":42.50,\\\"PaidAt\\\":\\\"2026-01-01T00:00:00+00:00\\\"}"
curl -fsS -u waybill:waybill -H 'Content-Type: application/json' \
  -X POST "http://localhost:${RABBITMQ_UI_PORT:-15672}/api/exchanges/%2F/events/publish" \
  -d "{\"routing_key\":\"billing.invoice-paid.v1\",\"payload\":\"$payload\",\"payload_encoding\":\"string\",
       \"properties\":{\"message_id\":\"$reused\",\"type\":\"billing.invoice-paid.v1\",\"delivery_mode\":2,
                      \"headers\":{\"waybill-key\":\"$id\",\"waybill-sequence\":\"1\"}}}" | grep -q '"routed":true'
echo "published a reused sequence for invoice $id ($reused)"

wait_for "the regression logged by the inbox (EventId 50)" 60 \
  bash -c "docker compose logs receipts | grep -F '[50]' -A1 | grep -q '$reused'"
wait_for "the regression in receipts.invoice-paid.dead" 60 \
  bash -c "[ \$(docker compose exec -T rabbitmq rabbitmqctl -q list_queues name messages | awk '\$1 == \"receipts.invoice-paid.dead\" { print \$2 }') -ge $((dead_before + 1)) ]"
receipts=$(curl -fsS "$RECEIPTS/receipts?invoiceId=$id" | grep -o '"invoiceId"' | wc -l)
if [ "$receipts" -ne 1 ]; then
  echo "FAILED: $receipts receipts for invoice $id after the regression" >&2
  exit 1
fi
echo "ok: still one receipt"
