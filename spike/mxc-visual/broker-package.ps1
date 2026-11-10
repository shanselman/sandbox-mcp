param(
    [string]$FixtureDirectory = (Join-Path $PSScriptRoot '..\..\samples\WinUI\bin\unpackaged'),
    [string]$Output = (Join-Path $PSScriptRoot ('bin\broker-package-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')))
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $Output) { throw 'Use a new package directory; existing payloads are never overwritten.' }
if (-not (Test-Path -LiteralPath (Join-Path $FixtureDirectory 'HelloWinUI.exe'))) { throw 'Build the existing WinUI fixture first.' }
New-Item -ItemType Directory -Path $Output | Out-Null
dotnet publish (Join-Path $PSScriptRoot 'broker') -c Release -r win-arm64 --self-contained false -o (Join-Path $Output 'broker') -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Broker publish failed.' }
dotnet publish (Join-Path $PSScriptRoot 'headless') -c Release -r win-arm64 --self-contained false -o (Join-Path $Output 'worker') -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Worker publish failed.' }
dotnet publish (Join-Path $PSScriptRoot 'helper') -c Release -r win-arm64 --self-contained false -o (Join-Path $Output 'helper') -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Capture helper publish failed.' }
Copy-Item -LiteralPath $FixtureDirectory -Destination (Join-Path $Output 'fixture') -Recurse
$root = (Resolve-Path -LiteralPath $Output).Path
$files = [ordered]@{}
Get-ChildItem -LiteralPath $root -File -Recurse | Sort-Object FullName | ForEach-Object {
    if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse points are not valid package contents.' }
    $relative = [IO.Path]::GetRelativePath($root, $_.FullName)
    $files[$relative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
}
$manifest = @{ schema = 1; files = $files } | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText((Join-Path $root 'manifest.json'), $manifest, [Text.UTF8Encoding]::new($false))
Write-Output $root
