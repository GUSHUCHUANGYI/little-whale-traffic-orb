[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\TrafficOrb\TrafficOrb.csproj'
$artifacts = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
& dotnet build $project -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
$exe = Join-Path $root 'src\TrafficOrb\bin\Release\net8.0-windows\TrafficOrb.exe'
$report = Join-Path $artifacts 'test-results.json'
$process = Start-Process -FilePath $exe -ArgumentList @('--self-test', ('"' + $report + '"')) -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) { throw 'Self-tests failed; see artifacts/test-results.json' }
$results = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
if (-not $results.ok) { throw 'Self-tests reported a failure' }
Write-Output ('Passed {0} self-tests.' -f $results.passed.Count)
