param(
    [Parameter(Mandatory)][string]$BackupFile,
    [switch]$ConfirmProductionRestore
)
$ErrorActionPreference = "Stop"
if (!$ConfirmProductionRestore) { throw "Restore is destructive. Re-run with -ConfirmProductionRestore." }
$root = Split-Path $PSScriptRoot -Parent
$resolved = (Resolve-Path -LiteralPath $BackupFile).Path
Get-Content -LiteralPath $resolved -AsByteStream -Raw | docker compose --project-directory $root exec -T database sh -c 'exec pg_restore --clean --if-exists --no-owner --username="$POSTGRES_USER" --dbname="$POSTGRES_DB"'
if ($LASTEXITCODE -ne 0) { throw "Restore failed." }
Write-Output "Restore completed from $resolved"
