param(
    [Parameter(Mandatory)]
    [string] $FileName,
    [double] $MinimumLineRate = 1.0,
    [double] $MinimumBranchRate = 1.0
)

$ErrorActionPreference = 'Stop'

$coverageFile = Get-ChildItem -Path . -Recurse -File -Filter $FileName |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if ($null -eq $coverageFile) {
    throw "Coverage file '$FileName' was not produced."
}

[xml] $coverage = Get-Content -LiteralPath $coverageFile.FullName
$lineRate = [double]::Parse(
    $coverage.coverage.'line-rate',
    [Globalization.CultureInfo]::InvariantCulture)
$branchRate = [double]::Parse(
    $coverage.coverage.'branch-rate',
    [Globalization.CultureInfo]::InvariantCulture)

if ($lineRate -lt $MinimumLineRate -or $branchRate -lt $MinimumBranchRate) {
    throw ("Coverage gate failed: lines {0:P2} (minimum {1:P2}), branches {2:P2} (minimum {3:P2})." -f
        $lineRate, $MinimumLineRate, $branchRate, $MinimumBranchRate)
}

Write-Host ("Coverage gate passed: {0}/{1} lines and {2}/{3} branches ({4:P2} / {5:P2})." -f
    $coverage.coverage.'lines-covered',
    $coverage.coverage.'lines-valid',
    $coverage.coverage.'branches-covered',
    $coverage.coverage.'branches-valid',
    $lineRate,
    $branchRate)
