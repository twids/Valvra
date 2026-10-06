param([string]$ResultsDirectory = "artifacts/test-results")
$ErrorActionPreference = "Stop"
$report = Get-ChildItem -LiteralPath $ResultsDirectory -Recurse -Filter "*.cobertura.xml" |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $report) { throw "No Cobertura report was produced." }
[xml]$coverage = Get-Content -LiteralPath $report.FullName -Raw
$minimum = [ordered]@{
    "EnvelopeCipher.cs" = 0.90
    "VaultIntegrity.cs" = 0.90
    "AccessService.cs" = 0.90
    "AuditService.cs" = 0.90
    "AuditScopeCapture.cs" = 0.90
    "VaultService.cs" = 0.90
    "ProtectedVaultService.cs" = 0.90
}
foreach ($filename in $minimum.Keys) {
    $lines = @{}
    foreach ($class in $coverage.coverage.packages.package.classes.class |
        Where-Object { $_.filename.Replace("\", "/").EndsWith("/" + $filename, [StringComparison]::Ordinal) }) {
        # Async state machines can map to the same source line. Count a source
        # line once and preserve a hit from any generated representation.
        foreach ($line in $class.lines.line) {
            $number = [int]$line.number
            $lines[$number] = [Math]::Max([int]$lines[$number], [int]$line.hits)
        }
    }
    if ($lines.Count -eq 0) { throw "Missing coverage for $filename." }
    $rate = @($lines.Values | Where-Object { $_ -gt 0 }).Count / $lines.Count
    Write-Output ("{0}: {1:P1}" -f $filename, $rate)
    if ($rate -lt $minimum[$filename]) { throw "$filename fell below its 90% line coverage floor." }
}
