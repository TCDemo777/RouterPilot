[CmdletBinding()]
param(
    [string] $BaseRevision = "HEAD"
)

$ErrorActionPreference = "Stop"
$repoRoot = (git rev-parse --show-toplevel).Trim()
Set-Location $repoRoot

$changed = @(git diff --name-only $BaseRevision -- '*.cs')
$untracked = @(git ls-files --others --exclude-standard -- '*.cs')
$files = @($changed + $untracked | Sort-Object -Unique | Where-Object { $_ -and (Test-Path $_) })
if ($files.Count -eq 0) {
    Write-Output "Quality gates: no changed C# files."
    exit 0
}

$failures = [System.Collections.Generic.List[string]]::new()
foreach ($file in $files) {
    $text = Get-Content -LiteralPath $file -Raw

    if ($text -match '(?m)^\s*catch\s*\(\s*Exception\s*\)\s*\{\s*\}') {
        $failures.Add("$file contains an empty broad catch(Exception) block.")
    }

    if ($text -match '(?m)\.Result\s*(\.|;|\))' -or $text -match '(?m)\.Wait\s*\(\s*\)') {
        $failures.Add("$file contains blocking async access (.Result/.Wait()).")
    }

    foreach ($match in [regex]::Matches($text, '(?m)^\s*(?:public|private|internal|protected)?\s*async\s+void\s+(\w+)\s*\(')) {
        $methodName = $match.Groups[1].Value
        $isFrameworkCallback = $methodName -match '(Click|Loaded|Unloaded|SelectionChanged|IsVisibleChanged|Tick|Closed|Startup|Closing|Changed|Requested)$'
        if (-not $isFrameworkCallback) {
            $failures.Add("$file contains non-event async void method '$methodName'.")
        }
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Output ("Quality gates passed for {0} changed C# file(s)." -f $files.Count)
