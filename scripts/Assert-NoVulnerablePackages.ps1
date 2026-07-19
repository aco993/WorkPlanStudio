$ErrorActionPreference = 'Stop'

$reportLines = & dotnet list WorkPlanStudio.slnx package --vulnerable --include-transitive --format json
if ($LASTEXITCODE -ne 0) {
    throw "dotnet package audit failed with exit code $LASTEXITCODE."
}

$report = ($reportLines -join [Environment]::NewLine) | ConvertFrom-Json
$findings = foreach ($project in $report.projects) {
    foreach ($framework in @($project.frameworks)) {
        foreach ($package in @($framework.topLevelPackages) + @($framework.transitivePackages)) {
            if ($null -eq $package) {
                continue
            }
            foreach ($vulnerability in @($package.vulnerabilities)) {
                if ($null -eq $vulnerability) {
                    continue
                }
                [pscustomobject]@{
                    Project  = $project.path
                    Framework = $framework.framework
                    Package  = $package.id
                    Version  = $package.resolvedVersion
                    Severity = $vulnerability.severity
                    Advisory = $vulnerability.advisoryurl
                }
            }
        }
    }
}

if (@($findings).Count -gt 0) {
    $findings | Format-Table -AutoSize | Out-String | Write-Host
    throw "Dependency audit found $(@($findings).Count) vulnerable package occurrence(s)."
}

Write-Host 'Dependency audit passed: no known vulnerable direct or transitive packages.'
