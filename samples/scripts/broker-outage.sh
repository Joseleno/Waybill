#!/usr/bin/env bash
# Stops RabbitMQ, pays a few invoices, and shows the events waiting in the outbox while /health reports Degraded.
# Then starts RabbitMQ again and shows the backlog drain, the receipts arrive and /health go back to Healthy.
# Run from anywhere after `docker compose up --wait` in samples/.
set -euo pipefail
cd "$(dirname "$0")/.."

BILLING=${BILLING:-http://localhost:8080}
RECEIPTS=${RECEIPTS:-http://localhost:8081}
COUNT=${COUNT:-5}

wait_for() { # description, seconds, command...
  local description=$1 seconds=$2; shift 2
  for _ in $(seq "$seconds"); do
    if "$@" >/dev/null 2>&1; then echo "ok: $description"; return 0; fi
    sleep 1
  done
  echo "FAILED: $description (after ${seconds}s)" >&2
  exit 1
}

pending() {
  docker compose exec -T postgres psql -U postgres -d billing -tAc \
    "SELECT count(*) FROM waybill.outbox WHERE status IN ('pending', 'claimed')"
}

health() { curl -sS "$BILLING/health" || true; }

echo "== stopping the broker"
docker compose stop rabbitmq >/dev/null

ids=()
for i in $(seq "$COUNT"); do
  invoice=$(curl -fsS -X POST "$BILLING/invoices" -H 'Content-Type: application/json' \
    -d "{\"number\":\"OUT-$RANDOM$RANDOM\",\"amount\":$i.00}")
  id=$(echo "$invoice" | sed -E 's/.*"id":"([^"]+)".*/\1/')
  curl -fsS -X POST "$BILLING/invoices/$id/payments" >/dev/null
  ids+=("$id")
done
echo "paid $COUNT invoices while the broker is down: the API keeps working"

wait_for "billing /health is Degraded" 60 bash -c "curl -sS '$BILLING/health' | grep -q Degraded"
echo "pending in the outbox: $(pending)  (the events wait in the database, nothing is lost)"
[ "$(pending)" -ge "$COUNT" ] || { echo "FAILED: expected at least $COUNT pending" >&2; exit 1; }

echo "== starting the broker"
docker compose start rabbitmq >/dev/null

wait_for "the outbox drained" 120 bash -c "[ \$(docker compose exec -T postgres psql -U postgres -d billing -tAc \"SELECT count(*) FROM waybill.outbox WHERE status IN ('pending', 'claimed')\") -eq 0 ]"
for id in "${ids[@]}"; do
  wait_for "receipt for $id" 120 bash -c "curl -fsS '$RECEIPTS/receipts?invoiceId=$id' | grep -q '\"invoiceId\":\"$id\"'"
done
wait_for "billing /health is Healthy" 60 bash -c "curl -sS '$BILLING/health' | grep -q Healthy"
echo "pending in the outbox: $(pending); health: $(health)"
