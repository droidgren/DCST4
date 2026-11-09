<#
.SYNOPSIS
    Builds DCST4 and runs the test harness. Exit code 0 = all checks passed.

.PARAMETER Render
    Instead of the tests, write sample images (.dcm and .png) to this folder for a visual check.
#>
param(
    [int]$TimeoutSeconds = 300,
    [string]$Render
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $PSScriptRoot 'DCST4.Tests\DCST4.Tests.csproj'

Write-Host '== Building tests'
& dotnet build $project -c Release --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$exe = Join-Path $PSScriptRoot 'DCST4.Tests\bin\Release\net48\DCST4.Tests.exe'
$arguments = if ($Render) { @('render', "`"$Render`"") } else { @() }

Write-Host '== Running'
$userConfig = Join-Path $env:LOCALAPPDATA 'DCST4\user.config'
$hashBefore = (Get-FileHash $userConfig -ErrorAction SilentlyContinue).Hash

$start = @{ FilePath = $exe; NoNewWindow = $true; PassThru = $true }
if ($arguments.Count -gt 0) { $start.ArgumentList = $arguments }
$p = Start-Process @start
$null = $p.Handle  # needed so ExitCode is available afterwards
if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
    Stop-Process -Id $p.Id -Force -Confirm:$false
    throw "Tests timed out after $TimeoutSeconds s."
}

if ($hashBefore -ne (Get-FileHash $userConfig -ErrorAction SilentlyContinue).Hash) {
    Write-Host "FAIL  $userConfig was modified by the test run." -ForegroundColor Red
    exit 1
}

exit $p.ExitCode
