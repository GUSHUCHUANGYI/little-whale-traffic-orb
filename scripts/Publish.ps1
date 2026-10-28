[CmdletBinding()]
param([ValidatePattern('^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')][string]$Version = '1.3.0', [switch]$SkipTests)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $SkipTests) { & (Join-Path $PSScriptRoot 'Test.ps1') }
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish\win-x64'
& dotnet publish (Join-Path $root 'src\TrafficOrb\TrafficOrb.csproj') -c Release -r win-x64 --self-contained true "-p:Version=$Version" -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
$packageName = "little-whale-traffic-orb-v$Version-win-x64"
$stage = Join-Path $artifacts ('package-' + $Version + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item -LiteralPath (Join-Path $publish 'TrafficOrb.exe') -Destination (Join-Path $stage '小鲸鱼流量悬浮球.exe')
foreach ($name in @('README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $root $name) -Destination (Join-Path $stage $name)
}
Copy-Item -LiteralPath (Join-Path $root 'licenses') -Destination (Join-Path $stage 'licenses') -Recurse
if (Test-Path -LiteralPath (Join-Path $root 'docs\images')) {
    $docs = Join-Path $stage 'docs'
    New-Item -ItemType Directory -Path $docs | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'docs\images') -Destination (Join-Path $docs 'images') -Recurse
}
$zip = Join-Path $artifacts ($packageName + '.zip')
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($zip + '.sha256'), ($hash + '  ' + [IO.Path]::GetFileName($zip) + "`n"), [Text.UTF8Encoding]::new($false))
Write-Output $zip
Write-Output ($zip + '.sha256')
