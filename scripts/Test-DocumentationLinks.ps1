$ErrorActionPreference = 'Stop'

$repositoryRoot = (git rev-parse --show-toplevel).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repositoryRoot)) {
    throw 'Run this script from inside the Git repository.'
}

$markdownFiles = @(
    git -C $repositoryRoot ls-files '*.md'
    git -C $repositoryRoot ls-files --others --exclude-standard '*.md'
) | Sort-Object -Unique

$broken = [System.Collections.Generic.List[object]]::new()
foreach ($relativeFile in $markdownFiles) {
    $absoluteFile = Join-Path $repositoryRoot $relativeFile
    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $absoluteFile) {
        $lineNumber++
        $matches = @(
            [regex]::Matches($line, '\]\((?<target><[^>]+>|[^\s\)]+)')
            [regex]::Matches($line, '^\s*\[[^\]]+\]:\s*(?<target><[^>]+>|\S+)')
        )
        foreach ($match in $matches) {
            $target = $match.Groups['target'].Value.Trim('<', '>')
            if ($target.StartsWith('#') -or $target.StartsWith('//') -or
                $target -match '^[a-zA-Z][a-zA-Z0-9+.-]*:') {
                continue
            }

            $pathPart = ($target -split '[?#]', 2)[0]
            if ([string]::IsNullOrWhiteSpace($pathPart)) {
                continue
            }
            $pathPart = [Uri]::UnescapeDataString($pathPart)
            $candidate = if ($pathPart.StartsWith('/')) {
                Join-Path $repositoryRoot $pathPart.TrimStart('/')
            } else {
                Join-Path (Split-Path -Parent $absoluteFile) $pathPart
            }

            if (-not (Test-Path -LiteralPath $candidate)) {
                $broken.Add([pscustomobject]@{
                    File = $relativeFile
                    Line = $lineNumber
                    Target = $target
                })
            }
        }
    }
}

if ($broken.Count -gt 0) {
    $broken | Format-Table -AutoSize | Out-String | Write-Host
    throw "Documentation contains $($broken.Count) broken local link(s)."
}

Write-Host "Documentation link audit passed for $($markdownFiles.Count) Markdown file(s)."
