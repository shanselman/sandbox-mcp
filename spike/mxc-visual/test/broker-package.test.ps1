#Requires -Version 7.0
# Synthetic files only. The local dotnet function below never invokes the SDK.
$ErrorActionPreference = 'Stop'
$spike = Split-Path $PSScriptRoot -Parent
. (Join-Path $spike 'broker-package-validation.ps1')
$root = Join-Path $spike ('bin\package-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$script:passed = 0
$global:brokerPackageTestState = @{ publishes = @(); failPublish = '' }

function Write-Json {
    param([string]$Path, $Value)
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 10))
}
function Write-Pe {
    param([string]$Path, [string]$Rid)
    $bytes = [byte[]]::new(90)
    $bytes[0] = 0x4D; $bytes[1] = 0x5A; $bytes[0x3C] = 64
    $bytes[64] = 0x50; $bytes[65] = 0x45
    $machine = [uint16]$(if ($Rid -eq 'win-x64') { 0x8664 } else { 0xAA64 })
    [BitConverter]::GetBytes($machine).CopyTo($bytes, 68)
    $bytes[88] = 0x0B; $bytes[89] = 0x02
    [IO.File]::WriteAllBytes($Path, $bytes)
}
function New-Payload {
    param([string]$Directory, [string]$Name, [string]$Rid)
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    Write-Pe (Join-Path $Directory "$Name.exe") $Rid
    [IO.File]::WriteAllText((Join-Path $Directory "$Name.dll"), 'synthetic managed assembly')
    Write-Json (Join-Path $Directory "$Name.deps.json") @{ runtimeTarget = @{ name = ".NETCoreApp,Version=v10.0/$Rid" } }
    $runtime = @{ tfm = 'net10.0'; frameworks = @(@{ name = 'Microsoft.NETCore.App'; version = '10.0.0' }) }
    if ($Name -eq 'CaptureHelper') { $runtime.frameworks += @{ name = 'Microsoft.WindowsDesktop.App'; version = '10.0.0' } }
    if ($Name -eq 'HelloWinUI') {
        $runtime.includedFrameworks = $runtime.frameworks
        $runtime.Remove('frameworks')
        foreach ($fileName in @('coreclr.dll', 'hostfxr.dll', 'Microsoft.UI.Xaml.dll', 'Microsoft.WindowsAppRuntime.dll',
                            'vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll')) {
            Write-Pe (Join-Path $Directory $fileName) $Rid
        }
        foreach ($fileName in @('App.xbf', 'MainWindow.xbf', 'resources.pri')) { [IO.File]::WriteAllText((Join-Path $Directory $fileName), 'synthetic resource') }
    }
    Write-Json (Join-Path $Directory "$Name.runtimeconfig.json") @{ runtimeOptions = $runtime }
}
function dotnet {
    if ($args[0] -ne 'publish') { throw 'Tests allow only mocked publish.' }
    $rid = $args[[Array]::IndexOf($args, '-r') + 1]
    $destination = $args[[Array]::IndexOf($args, '-o') + 1]
    if ($args[[Array]::IndexOf($args, '--self-contained') + 1] -ne 'false') { throw 'Expected framework-dependent publish.' }
    $name = switch ([IO.Path]::GetFileName($args[1])) {
        'FullBroker.csproj' { 'SandboxMcp.FullBroker' }
        'HeadlessSpike.csproj' { 'HeadlessSpike' }
        'CaptureHelper.csproj' { 'CaptureHelper' }
        default { throw 'Unexpected publish project.' }
    }
    $global:brokerPackageTestState.publishes += "$name/$rid"
    if ($name -eq $global:brokerPackageTestState.failPublish) { $global:LASTEXITCODE = 1; return }
    New-Payload $destination $name $rid
    $global:LASTEXITCODE = 0
}
function Assert-Throws {
    param([scriptblock]$Action, [string]$Pattern)
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -notmatch $Pattern) { throw "Expected '$Pattern', received: $($_.Exception.Message)" }
        return
    }
    throw "Expected failure matching '$Pattern'."
}
function Test-Case {
    param([string]$Name, [scriptblock]$Action)
    & $Action
    $script:passed++
    Write-Host "PASS $Name"
}

try {
    $fixture = Join-Path $root 'input-fixture'
    New-Payload $fixture 'HelloWinUI' 'win-x64'
    $packageScript = Join-Path $spike 'broker-package.ps1'
    foreach ($rid in @('win-x64', 'win-arm64')) {
        Test-Case "publishes and verifies all three $rid components with the declared fixture" {
            $package = Join-Path $root $rid
            $before = $global:brokerPackageTestState.publishes.Count
            $output = & $packageScript -RuntimeIdentifier $rid -FixtureDirectory $fixture -Output $package -AllowX64FixtureEmulation:($rid -eq 'win-arm64')
            if ($output -ne $package -or $global:brokerPackageTestState.publishes.Count -ne $before + 3 -or
                @($global:brokerPackageTestState.publishes | Select-Object -Last 3 | Where-Object { -not $_.EndsWith("/$rid") }).Count) {
                throw 'Publish did not consistently propagate the requested RID.'
            }
            $result = & $packageScript -VerifyPackage $package | ConvertFrom-Json
            $execution = if ($rid -eq 'win-arm64') { 'x64-emulation' } else { 'native' }
            if (-not $result.ok -or $result.runtimeIdentifier -ne $rid -or
                $result.fixtureExecution -ne $execution -or $result.nativeCompatibilityChecked) {
                throw 'Incorrect verified architecture or native-compatibility claim.'
            }
            $manifest = Get-Content -LiteralPath (Join-Path $package 'manifest.json') -Raw | ConvertFrom-Json -AsHashtable
            if (($manifest.Keys | Sort-Object) -join ',' -ne 'files,schema' -or
                -not $manifest.files.Contains('broker\package-architecture.json')) {
                throw 'Architecture metadata must be hashed without extending the strict installer manifest schema.'
            }
        }
    }
    Test-Case 'native ARM64 fixture is recognized without an emulation waiver' {
        $nativeFixture = Join-Path $root 'arm64-fixture'
        New-Payload $nativeFixture 'HelloWinUI' 'win-arm64'
        $identity = Get-FixtureIdentity $nativeFixture 'win-arm64'
        if ($identity.fixtureExecution -ne 'native') { throw 'Misclassified native fixture.' }
        Assert-Throws { Get-FixtureIdentity $nativeFixture 'win-x64' -AllowX64FixtureEmulation } 'does not match'
    }
    Test-Case 'relative output follows the PowerShell working directory' {
        Push-Location $root
        try {
            $output = & $packageScript -RuntimeIdentifier win-x64 -FixtureDirectory $fixture -Output 'relative-package'
            if ($output -ne (Join-Path $root 'relative-package')) { throw 'Output resolved against the process directory instead of PowerShell location.' }
        } finally { Pop-Location }
    }
    Test-Case 'ARM64 with x64 fixture fails before publish without explicit emulation' {
        $before = $global:brokerPackageTestState.publishes.Count
        $destination = Join-Path $root 'must-not-exist'
        Assert-Throws { & $packageScript -RuntimeIdentifier win-arm64 -FixtureDirectory $fixture -Output $destination } 'requires -AllowX64FixtureEmulation'
        if ((Test-Path $destination) -or $global:brokerPackageTestState.publishes.Count -ne $before) { throw 'Started build before rejecting fixture.' }
    }
    Test-Case 'unsupported RID, reused output and nested output are rejected' {
        Assert-Throws { & $packageScript -RuntimeIdentifier win-x86 } 'ValidateSet|validation|does not belong'
        Assert-Throws { & $packageScript -RuntimeIdentifier win-x64 -Output (Join-Path $root 'win-x64') } 'never overwritten'
        Assert-Throws { & $packageScript -RuntimeIdentifier win-x64 -FixtureDirectory $fixture -Output (Join-Path $fixture 'nested') } 'inside the fixture'
    }
    Test-Case 'a failed publish never leaves a complete package manifest' {
        $global:brokerPackageTestState.failPublish = 'HeadlessSpike'
        $destination = Join-Path $root 'failed-publish'
        try { Assert-Throws { & $packageScript -RuntimeIdentifier win-x64 -FixtureDirectory $fixture -Output $destination } 'worker publish failed' }
        finally { $global:brokerPackageTestState.failPublish = '' }
        if (Test-Path (Join-Path $destination 'manifest.json')) { throw 'Incomplete package was labelled complete.' }
    }
    Test-Case 'mixed worker apphost architecture is rejected' {
        $worker = Join-Path $root 'win-arm64\worker\HeadlessSpike.exe'
        Write-Pe $worker 'win-x64'
        try { Assert-Throws { Test-BrokerPackage (Join-Path $root 'win-arm64') } 'Apphost architecture mismatch' }
        finally { Write-Pe $worker 'win-arm64' }
    }
    Test-Case 'helper dependency RID and missing Desktop runtime are rejected' {
        $helper = Join-Path $root 'win-x64\helper'
        $deps = Join-Path $helper 'CaptureHelper.deps.json'
        $saved = [IO.File]::ReadAllText($deps)
        try {
            Write-Json $deps @{ runtimeTarget = @{ name = '.NETCoreApp,Version=v10.0/win-arm64' } }
            Assert-Throws { Test-BrokerPackage (Join-Path $root 'win-x64') } 'Runtime target mismatch'
        } finally { [IO.File]::WriteAllText($deps, $saved) }
        $config = Join-Path $helper 'CaptureHelper.runtimeconfig.json'
        $saved = [IO.File]::ReadAllText($config)
        try {
            Write-Json $config @{ runtimeOptions = @{ tfm = 'net10.0'; framework = @{ name = 'Microsoft.NETCore.App'; version = '10.0.0' } } }
            Assert-Throws { Test-BrokerPackage (Join-Path $root 'win-x64') } 'Missing Windows Desktop'
        } finally { [IO.File]::WriteAllText($config, $saved) }
    }
    Test-Case 'fixture runtime architecture and incomplete resources are rejected' {
        $core = Join-Path $fixture 'coreclr.dll'
        Write-Pe $core 'win-arm64'
        try { Assert-Throws { Get-FixtureIdentity $fixture 'win-x64' } 'native dependency architecture mismatch' }
        finally { Write-Pe $core 'win-x64' }
        Remove-Item -LiteralPath (Join-Path $fixture 'resources.pri')
        try { Assert-Throws { Get-FixtureIdentity $fixture 'win-x64' } 'Missing fixture resource' }
        finally { [IO.File]::WriteAllText((Join-Path $fixture 'resources.pri'), 'synthetic resource') }
        Remove-Item -LiteralPath (Join-Path $fixture 'vcruntime140.dll')
        try {
            if ((Get-FixtureIdentity $fixture 'win-x64').fixtureVcRuntime -ne 'system-required') {
                throw 'Missing app-local CRT did not declare the system prerequisite.'
            }
        }
        finally { Write-Pe (Join-Path $fixture 'vcruntime140.dll') 'win-x64' }
    }
    Test-Case 'framework-dependent fixture and malformed PE are rejected' {
        $config = Join-Path $fixture 'HelloWinUI.runtimeconfig.json'
        $saved = [IO.File]::ReadAllText($config)
        try {
            Write-Json $config @{ runtimeOptions = @{ tfm = 'net10.0'; framework = @{ name = 'Microsoft.NETCore.App'; version = '10.0.0' } } }
            Assert-Throws { Get-FixtureIdentity $fixture 'win-x64' } 'must be self-contained'
        } finally { [IO.File]::WriteAllText($config, $saved) }
        $bad = Join-Path $root 'bad.exe'
        [IO.File]::WriteAllText($bad, 'not a PE')
        Assert-Throws { Get-PeRuntimeIdentifier $bad } 'Invalid PE'
        Write-Pe $bad 'win-x64'
        $bytes = [IO.File]::ReadAllBytes($bad); $bytes[0x3C] = 255
        [IO.File]::WriteAllBytes($bad, $bytes)
        Assert-Throws { Get-PeRuntimeIdentifier $bad } 'Invalid PE header'
    }
    Test-Case 'metadata lies, changed hashes and unlisted files fail offline verification' {
        $package = Join-Path $root 'win-arm64'
        $metadataPath = Join-Path $package 'broker\package-architecture.json'
        $saved = [IO.File]::ReadAllText($metadataPath)
        try {
            $metadata = $saved | ConvertFrom-Json -AsHashtable
            $metadata.fixtureRuntimeIdentifier = 'win-arm64'
            Write-Json $metadataPath $metadata
            Assert-Throws { Test-BrokerPackage $package } 'metadata mismatch'
        } finally { [IO.File]::WriteAllText($metadataPath, $saved) }
        $resource = Join-Path $package 'fixture\App.xbf'
        $saved = [IO.File]::ReadAllText($resource)
        try {
            [IO.File]::WriteAllText($resource, 'changed')
            Assert-Throws { Test-BrokerPackage $package } 'hash mismatch'
        } finally { [IO.File]::WriteAllText($resource, $saved) }
        $extra = Join-Path $package 'fixture\unlisted.txt'
        [IO.File]::WriteAllText($extra, 'extra')
        try { Assert-Throws { Test-BrokerPackage $package } 'file list mismatch' }
        finally { Remove-Item -LiteralPath $extra }
    }
    Test-Case 'fixture junctions are rejected before traversal' {
        $junction = Join-Path $fixture 'linked'
        New-Item -ItemType Junction -Path $junction -Target (Join-Path $root 'win-x64') | Out-Null
        try { Assert-Throws { Get-FixtureIdentity $fixture 'win-x64' } 'Reparse points' }
        finally { Remove-Item -LiteralPath $junction }
    }
    Write-Host "$script:passed packaging tests passed; no native operations or SDK builds."
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force
    Remove-Variable brokerPackageTestState -Scope Global
}
