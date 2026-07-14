param(
    [string]$OutputDirectory = "backups"
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$target = Join-Path $root $OutputDirectory
New-Item -ItemType Directory -Force -Path $target | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$file = Join-Path $target "workplanstudio-$stamp.dump"
docker compose --project-directory $root exec -T database sh -c 'exec pg_dump --format=custom --no-owner --username="$POSTGRES_USER" "$POSTGRES_DB"' > $file
if ($LASTEXITCODE -ne 0 -or !(Test-Path $file) -or (Get-Item $file).Length -eq 0) { throw "Backup failed." }
Write-Output $file
