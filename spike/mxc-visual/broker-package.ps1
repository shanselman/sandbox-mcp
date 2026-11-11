#Requires -Version 7.0
[CmdletBinding(DefaultParameterSetName = 'Build')]
param(
    [Parameter(ParameterSetName = 'Build')]
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier = ('win-' + [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()),
    [Parameter(ParameterSetName = 'Build')]
    [string]$FixtureDirectory = (Join-Path $PSScriptRoot '..\..\samples\WinUI\bin\unpackaged'),
    [Parameter(ParameterSetName = 'Build')]
    [string]$Output = (Join-Path $PSScriptRoot ("bin\broker-package-$RuntimeIdentifier-" + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))),
    [Parameter(ParameterSetName = 'Build')]
    [switch]$AllowX64FixtureEmulation,
    [Parameter(Mandatory, ParameterSetName = 'Verify')]
    [string]$VerifyPackage
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'broker-package-validation.ps1')
if ($PSCmdlet.ParameterSetName -eq 'Verify') {
    Test-BrokerPackage (Resolve-Path -LiteralPath $VerifyPackage).Path | ConvertTo-Json
    return
}
if (Test-Path -LiteralPath $Output) { throw 'Use a new package directory; existing payloads are never overwritten.' }
$FixtureDirectory = (Resolve-Path -LiteralPath $FixtureDirectory).Path
$Output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
if ($Output.StartsWith($FixtureDirectory.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Package output must not be inside the fixture directory.'
}
$fixtureIdentity = Get-FixtureIdentity $FixtureDirectory $RuntimeIdentifier -AllowX64FixtureEmulation:$AllowX64FixtureEmulation
if ($fixtureIdentity.fixtureVcRuntime -eq 'system-required') {
    Write-Warning "Fixture has no complete app-local VC runtime. The target host needs the VC++ v14 runtime for $($fixtureIdentity.fixtureRuntimeIdentifier), including under emulation."
}
New-Item -ItemType Directory -Path $Output | Out-Null
foreach ($component in @(@('broker\FullBroker.csproj', 'broker'), @('headless\HeadlessSpike.csproj', 'worker'), @('helper\CaptureHelper.csproj', 'helper'))) {
    dotnet publish (Join-Path $PSScriptRoot $component[0]) -c Release -r $RuntimeIdentifier --self-contained false -o (Join-Path $Output $component[1]) -v quiet
    if ($LASTEXITCODE -ne 0) { throw "$($component[1]) publish failed. The incomplete output has no manifest; use a new directory when retrying." }
}
Copy-Item -LiteralPath $FixtureDirectory -Destination (Join-Path $Output 'fixture') -Recurse
$identity = Get-PackageIdentity $Output $RuntimeIdentifier -AllowX64FixtureEmulation:$AllowX64FixtureEmulation
[IO.File]::WriteAllText((Join-Path $Output 'broker\package-architecture.json'), ($identity | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
$manifest = [ordered]@{ schema = 1; files = (Get-PackageFileHashes $Output) } | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText((Join-Path $Output 'manifest.json'), $manifest, [Text.UTF8Encoding]::new($false))
Write-Output $Output
