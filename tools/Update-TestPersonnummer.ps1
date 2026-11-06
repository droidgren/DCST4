<#
.SYNOPSIS
    Downloads Skatteverket's official test personnummer and writes the list embedded in DCST4.Core.

.DESCRIPTION
    Source: https://skatteverket.entryscape.net/rowstore/dataset/b4de7df7-63c0-4e7e-bb59-1f156a591763

    The server returns at most 100 rows per request, whatever _limit says, so this script pages through
    all rows. It fails rather than writing a partial list if the row count does not match resultCount,
    or if any row is not 12 digits. Check digits and dates are validated when the list is loaded, by
    TestPersonnummerRegistry.

    Output: src\DCST4.Core\TestData\testpersonnummer.txt.gz. The first line is a '#' comment with the
    source URL and the fetch date; every other line is one 12-digit number. Rebuild the app afterwards.
#>
param(
    [string]$OutFile = (Join-Path (Split-Path $PSScriptRoot -Parent) 'src\DCST4.Core\TestData\testpersonnummer.txt.gz')
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$dataset = 'https://skatteverket.entryscape.net/rowstore/dataset/b4de7df7-63c0-4e7e-bb59-1f156a591763'
$pageSize = 100
$rows = New-Object System.Collections.Generic.List[string]
$offset = 0
$total = $null

do {
    $page = Invoke-RestMethod -Uri "$dataset/json?_limit=$pageSize&_offset=$offset"
    if ($null -eq $total) {
        $total = [int]$page.resultCount
        Write-Host "The dataset reports $total rows."
    }
    if ([int]$page.resultCount -ne $total) { throw "resultCount changed during download ($total -> $($page.resultCount))." }

    $received = @($page.results)
    if ($received.Count -eq 0) { break }
    foreach ($r in $received) {
        $value = [string]$r.testpersonnummer
        if ($value -notmatch '^\d{12}$') { throw "Unexpected row at offset ${offset}: '$value'" }
        $rows.Add($value)
    }
    $offset += $received.Count
    if ($offset % 5000 -lt $pageSize) { Write-Host "  $offset of $total" }
} while ($offset -lt $total)

if ($rows.Count -ne $total) { throw "Received $($rows.Count) rows, expected $total." }

$unique = $rows | Sort-Object -Unique
# The published dataset really does contain some numbers twice (the API's own filter returned 2 rows for
# them when checked on 2026-11-06: 43,895 rows, 42,939 unique), so this is expected and not a paging fault.
if (@($unique).Count -ne $total) { Write-Host "Removed duplicates: $total rows, $(@($unique).Count) unique." }

$header = "# Skatteverket testpersonnummer, $dataset, fetched $(Get-Date -Format 'yyyy-MM-dd'), $(@($unique).Count) numbers"
$text = (@($header) + $unique) -join "`n"

$dir = Split-Path $OutFile -Parent
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
$bytes = [Text.Encoding]::ASCII.GetBytes($text)
$stream = [IO.File]::Create($OutFile)
try {
    $gzip = New-Object IO.Compression.GZipStream($stream, [IO.Compression.CompressionLevel]::Optimal)
    try { $gzip.Write($bytes, 0, $bytes.Length) } finally { $gzip.Dispose() }
} finally { $stream.Dispose() }

Write-Host "Wrote $(@($unique).Count) numbers to $OutFile ($((Get-Item $OutFile).Length) bytes)."
