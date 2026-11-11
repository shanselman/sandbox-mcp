# File-only checks: never load a payload or Windows experimental API.
function Assert-PackageTree {
    param([string]$Path)
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse points are not valid package contents: $Path" }
    if ($item.PSIsContainer) {
        Get-ChildItem -LiteralPath $Path -Force | ForEach-Object { Assert-PackageTree $_.FullName }
    }
}

function Get-PeRuntimeIdentifier {
    param([string]$Path)
    $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($Path))
    try {
        if ($reader.BaseStream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D) { throw "Invalid PE file: $Path" }
        $reader.BaseStream.Position = 0x3C
        $offset = $reader.ReadUInt32()
        if ($offset -lt 64 -or $offset -gt $reader.BaseStream.Length - 26) { throw "Invalid PE header: $Path" }
        $reader.BaseStream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x4550) { throw "Invalid PE signature: $Path" }
        $machine = $reader.ReadUInt16()
        $reader.BaseStream.Position = $offset + 24
        if ($reader.ReadUInt16() -ne 0x20B) { throw "Expected a 64-bit PE: $Path" }
        switch ($machine) {
            0x8664 { return 'win-x64' }
            0xAA64 { return 'win-arm64' }
            default { throw "Unsupported PE machine 0x$($machine.ToString('X4')): $Path" }
        }
    }
    finally { $reader.Dispose() }
}

function Assert-DotNetPayload {
    param([string]$Directory, [string]$Name, [string]$RuntimeIdentifier, [switch]$SelfContained, [switch]$Desktop)
    if ((Get-PeRuntimeIdentifier (Join-Path $Directory "$Name.exe")) -ne $RuntimeIdentifier) {
        throw "Apphost architecture mismatch: $Name must be $RuntimeIdentifier"
    }
    if (-not (Test-Path -LiteralPath (Join-Path $Directory "$Name.dll") -PathType Leaf)) { throw "Missing managed payload: $Name.dll" }
    $deps = Get-Content -LiteralPath (Join-Path $Directory "$Name.deps.json") -Raw | ConvertFrom-Json -AsHashtable
    if ($deps.runtimeTarget.name -ne ".NETCoreApp,Version=v10.0/$RuntimeIdentifier") {
        throw "Runtime target mismatch: $Name must be $RuntimeIdentifier"
    }
    $runtime = (Get-Content -LiteralPath (Join-Path $Directory "$Name.runtimeconfig.json") -Raw | ConvertFrom-Json -AsHashtable).runtimeOptions
    if ($runtime.tfm -ne 'net10.0') { throw "Expected .NET 10 runtime configuration: $Name" }
    if ($SelfContained) {
        if (-not $runtime.includedFrameworks -or $runtime.framework -or $runtime.frameworks) {
            throw "Fixture must be self-contained: $Name"
        }
    } else {
        $frameworks = @($runtime.framework) + @($runtime.frameworks) | Where-Object { $_ }
        if ($runtime.includedFrameworks -or 'Microsoft.NETCore.App' -notin $frameworks.name) {
            throw "Expected framework-dependent .NET payload: $Name"
        }
        if ($Desktop -and 'Microsoft.WindowsDesktop.App' -notin $frameworks.name) {
            throw "Missing Windows Desktop runtime dependency: $Name"
        }
    }
}

function Get-FixtureIdentity {
    param([string]$Directory, [string]$RuntimeIdentifier, [switch]$AllowX64FixtureEmulation)
    Assert-PackageTree $Directory
    $fixtureRid = Get-PeRuntimeIdentifier (Join-Path $Directory 'HelloWinUI.exe')
    $emulated = $RuntimeIdentifier -eq 'win-arm64' -and $fixtureRid -eq 'win-x64'
    if ($fixtureRid -ne $RuntimeIdentifier -and -not ($emulated -and $AllowX64FixtureEmulation)) {
        throw "Fixture architecture $fixtureRid does not match $RuntimeIdentifier. ARM64 with the x64 fixture requires -AllowX64FixtureEmulation."
    }
    Assert-DotNetPayload $Directory 'HelloWinUI' $fixtureRid -SelfContained
    foreach ($name in @('coreclr.dll', 'hostfxr.dll', 'Microsoft.UI.Xaml.dll', 'Microsoft.WindowsAppRuntime.dll')) {
        if ((Get-PeRuntimeIdentifier (Join-Path $Directory $name)) -ne $fixtureRid) { throw "Fixture native dependency architecture mismatch: $name" }
    }
    $appLocalCrt = $true
    foreach ($name in @('vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll')) {
        $path = Join-Path $Directory $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $appLocalCrt = $false; continue }
        if ((Get-PeRuntimeIdentifier $path) -ne $fixtureRid) { throw "Fixture native dependency architecture mismatch: $name" }
    }
    foreach ($name in @('App.xbf', 'MainWindow.xbf', 'resources.pri')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Directory $name) -PathType Leaf)) { throw "Missing fixture resource: $name" }
    }
    return [ordered]@{
        runtimeIdentifier = $RuntimeIdentifier
        fixtureRuntimeIdentifier = $fixtureRid
        fixtureExecution = $(if ($emulated) { 'x64-emulation' } else { 'native' })
        fixtureVcRuntime = $(if ($appLocalCrt) { 'app-local' } else { 'system-required' })
    }
}

function Get-PackageIdentity {
    param([string]$Directory, [string]$RuntimeIdentifier, [switch]$AllowX64FixtureEmulation)
    Assert-PackageTree $Directory
    foreach ($component in @(@('broker', 'SandboxMcp.FullBroker'), @('worker', 'HeadlessSpike'), @('helper', 'CaptureHelper'))) {
        Assert-DotNetPayload (Join-Path $Directory $component[0]) $component[1] $RuntimeIdentifier -Desktop:($component[0] -eq 'helper')
    }
    return Get-FixtureIdentity (Join-Path $Directory 'fixture') $RuntimeIdentifier -AllowX64FixtureEmulation:$AllowX64FixtureEmulation
}

function Get-PackageFileHashes {
    param([string]$Directory)
    $files = [ordered]@{}
    Get-ChildItem -LiteralPath $Directory -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($Directory, $_.FullName)
        if ($relative -ne 'manifest.json') {
            $files[$relative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    }
    return $files
}

function Test-BrokerPackage {
    param([string]$Directory)
    Assert-PackageTree $Directory
    $manifest = Get-Content -LiteralPath (Join-Path $Directory 'manifest.json') -Raw | ConvertFrom-Json -AsHashtable
    $metadata = Get-Content -LiteralPath (Join-Path $Directory 'broker\package-architecture.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($manifest.schema -ne 1 -or $manifest.Count -ne 2 -or $metadata.runtimeIdentifier -notin @('win-x64', 'win-arm64') -or -not $manifest.files) {
        throw 'Expected an architecture-labelled broker package manifest.'
    }
    $identity = Get-PackageIdentity $Directory $metadata.runtimeIdentifier -AllowX64FixtureEmulation:($metadata.fixtureExecution -eq 'x64-emulation')
    foreach ($key in $identity.Keys) {
        if ($metadata[$key] -ne $identity[$key]) { throw "Package architecture metadata mismatch: $key" }
    }
    $files = Get-PackageFileHashes $Directory
    if ($files.Count -ne $manifest.files.Count) { throw 'Package file list mismatch.' }
    foreach ($key in $files.Keys) {
        if ($files[$key] -cne $manifest.files[$key]) { throw "Package hash mismatch: $key" }
    }
    return [ordered]@{ ok = $true; runtimeIdentifier = $identity.runtimeIdentifier
        fixtureRuntimeIdentifier = $identity.fixtureRuntimeIdentifier; fixtureExecution = $identity.fixtureExecution
        fixtureVcRuntime = $identity.fixtureVcRuntime
        files = $files.Count; nativeCompatibilityChecked = $false }
}
