#!/usr/bin/env bash
# Creates and pays one invoice, then waits for the receipt and for both events on the `audit` queue.
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
