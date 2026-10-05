# Stops RabbitMQ, pays a few invoices, and shows the events waiting in the outbox while /health reports Degraded.
# Then starts RabbitMQ again and shows the backlog drain, the receipts arrive and /health go back to Healthy.
# Run from anywhere after `docker compose up --wait` in samples/. Same steps as broker-outage.sh.
param(
    [string] $Billing = 'http://localhost:8080',
    [string] $Receipts = 'http://localhost:8081',
    [int] $Count = 5
)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

function Wait-For([string] $Description, [int] $Seconds, [scriptblock] $Condition) {
    for ($i = 0; $i -lt $Seconds; $i++) {
        try { if (& $Condition) { Write-Host "ok: $Description"; return } } catch { }
        Start-Sleep -Seconds 1
    }
    throw "FAILED: $Description (after ${Seconds}s)"
}

function Get-Pending {
    [int](docker compose exec -T postgres psql -U postgres -d billing -tAc "SELECT count(*) FROM waybill.outbox WHERE status IN ('pending', 'claimed')")
}

function Get-Health {
    try { (Invoke-WebRequest "$Billing/health" -SkipHttpErrorCheck).Content } catch { 'unreachable' }
}

Write-Host '== stopping the broker'
docker compose stop rabbitmq 2>&1 | Out-Null

$ids = foreach ($i in 1..$Count) {
    $invoice = Invoke-RestMethod -Method Post "$Billing/invoices" -ContentType 'application/json' `
        -Body (@{ number = "OUT-$(Get-Random)"; amount = $i } | ConvertTo-Json)
    Invoke-RestMethod -Method Post "$Billing/invoices/$($invoice.id)/payments" | Out-Null
    $invoice.id
}
Write-Host "paid $Count invoices while the broker is down: the API keeps working"

Wait-For 'billing /health is Degraded' 60 { (Get-Health) -eq 'Degraded' }
$pending = Get-Pending
Write-Host "pending in the outbox: $pending  (the events wait in the database, nothing is lost)"
if ($pending -lt $Count) { throw "FAILED: expected at least $Count pending" }

Write-Host '== starting the broker'
docker compose start rabbitmq 2>&1 | Out-Null

Wait-For 'the outbox drained' 120 { (Get-Pending) -eq 0 }
foreach ($id in $ids) {
    Wait-For "receipt for $id" 120 { @(Invoke-RestMethod "$Receipts/receipts?invoiceId=$id").Count -eq 1 }
}
Wait-For 'billing /health is Healthy' 60 { (Get-Health) -eq 'Healthy' }
Write-Host "pending in the outbox: $(Get-Pending); health: $(Get-Health)"
