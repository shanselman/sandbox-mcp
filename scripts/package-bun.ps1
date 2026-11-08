<#
.SYNOPSIS
Builds the Bun-based Windows x64 portable package without changing repository source.
.EXAMPLE
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/package-bun.ps1
.NOTES
Requires Bun 1.4.4 on PATH and dependencies restored with npm ci --omit=dev.
Downloads the license for the pinned Bun release, as package.ps1 does for Node.
#>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$requiredBunVersion = '1.4.4'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$distRoot = Join-Path $repoRoot 'dist'
$stage = Join-Path $distRoot 'sandbox-mcp-bun'
$package = Get-Content (Join-Path $repoRoot 'package.json') -Raw | ConvertFrom-Json
$bun = Get-Command bun.exe -ErrorAction SilentlyContinue
if (-not $bun) { $bun = Get-Command bun -ErrorAction SilentlyContinue }
if (-not $bun) { throw "Bun is missing. Install Bun $requiredBunVersion and ensure it is on PATH before running this script." }

$bunVersion = ([string](& $bun.Source --version)).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot determine the installed Bun version.' }
if ($bunVersion -ne $requiredBunVersion) {
  throw "Bun $requiredBunVersion is required; found '$bunVersion'. Other versions have not been validated. Install the pinned version before packaging."
}
$platform = ([string](& $bun.Source -p 'process.platform')).Trim()
if ($LASTEXITCODE -ne 0 -or $platform -ne 'win32') {
  throw "Only native Windows builds are currently supported (found $platform)."
}
$architecture = ([string](& $bun.Source -p 'process.arch')).Trim()
if ($LASTEXITCODE -ne 0 -or $architecture -ne 'x64') {
  throw "Only Windows x64 packages are currently supported (found $architecture)."
}
foreach ($dependency in @('@modelcontextprotocol\sdk', 'zod')) {
  if (-not (Test-Path (Join-Path $repoRoot "node_modules\$dependency\package.json"))) {
    throw 'Dependencies are missing. Run npm ci --omit=dev, then run scripts/package-bun.ps1 again.'
  }
}
$sourceCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[a-f0-9]{40}$') {
  throw 'Cannot identify the package source commit.'
}
$channel = if ($env:SANDBOX_MCP_BUILD_CHANNEL) { $env:SANDBOX_MCP_BUILD_CHANNEL } else { 'local-candidate' }
$archive = Join-Path $distRoot "sandbox-mcp-win-x64-v$($package.version)-bun.zip"

# Keep build inputs under the repository so imports resolve its restored node_modules.
$scratchRoot = Join-Path $repoRoot '.scratch'
$buildRoot = Join-Path $scratchRoot ("package-bun-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $scratchRoot, $distRoot | Out-Null
if ((Get-Item -LiteralPath $scratchRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) {
  throw 'The scratch directory must not be a link.'
}
if ((Get-Item -LiteralPath $distRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) {
  throw 'The distribution directory must not be a link.'
}
New-Item -ItemType Directory -Path $buildRoot | Out-Null

try {
  Copy-Item -LiteralPath (Join-Path $repoRoot 'src') -Destination $buildRoot -Recurse
  $corePath = Join-Path $buildRoot 'src\core.js'
  $core = [IO.File]::ReadAllText($corePath)
  $sourceRootDeclaration = "export const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');"
  if ([regex]::Matches($core, [regex]::Escape($sourceRootDeclaration)).Count -ne 1) {
    throw 'The resource ROOT declaration has changed. Review the compiled resource-path adjustment before packaging.'
  }
  # Compiled import.meta.url points into Bun's virtual filesystem. Resources stay beside sbx.exe.
  $core = $core.Replace($sourceRootDeclaration, 'export const ROOT = path.dirname(process.execPath);')
  [IO.File]::WriteAllText($corePath, $core, [Text.UTF8Encoding]::new($false))

  $compiled = Join-Path $buildRoot 'sbx.exe'
  & $bun.Source build (Join-Path $buildRoot 'src\cli.js') --compile --target=bun-windows-x64 --outfile $compiled --sourcemap --no-compile-autoload-dotenv --no-compile-autoload-bunfig
  if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $compiled)) {
    throw 'Bun compilation failed.'
  }

  # Prepare the full package before replacing an earlier successful output.
  $prepared = Join-Path $buildRoot 'package'
  New-Item -ItemType Directory -Path $prepared | Out-Null
  Copy-Item -LiteralPath $compiled -Destination $prepared
  foreach ($item in @('agent', 'skills', 'docs')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $item) -Destination $prepared -Recurse
  }
  foreach ($item in @('README.md', 'LICENSE', 'package.json')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $item) -Destination $prepared
  }
  Invoke-WebRequest -UseBasicParsing -Uri "https://raw.githubusercontent.com/oven-sh/bun/bun-v$bunVersion/LICENSE.md" -OutFile (Join-Path $prepared 'BUN-LICENSE.md')
  $dependencyRoot = (Resolve-Path (Join-Path $repoRoot 'node_modules')).Path
  $notices = Join-Path $prepared 'DEPENDENCY-LICENSES'
  Get-ChildItem -LiteralPath $dependencyRoot -Recurse -File | Where-Object {
    $_.Name -match '^(LICENSE|LICENCE|COPYING|NOTICE)([.\-].*)?$'
  } | ForEach-Object {
    $relative = $_.FullName.Substring($dependencyRoot.Length + 1)
    $destination = Join-Path $notices $relative
    New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $destination
  }
  $utf8 = [Text.UTF8Encoding]::new($false)
  [IO.File]::WriteAllText((Join-Path $prepared 'sbx.cmd'), "@echo off`r`n`"%~dp0sbx.exe`" %*`r`nexit /b %errorlevel%`r`n", $utf8)
  [IO.File]::WriteAllText((Join-Path $prepared 'sandbox-mcp.cmd'), "@echo off`r`n`"%~dp0sbx.exe`" mcp %*`r`nexit /b %errorlevel%`r`n", $utf8)
  @(
    "sandbox-mcp $($package.version)"
    'Platform: Windows x64'
    "Bundled runtime: Bun $bunVersion (license: BUN-LICENSE.md)"
    "Source commit: $sourceCommit"
    "Build channel: $channel"
    'Resource ROOT: executable directory (build-copy adjustment)'
  ) | Set-Content -LiteralPath (Join-Path $prepared 'BUILD-INFO.txt') -Encoding UTF8

  $version = ([string](& (Join-Path $prepared 'sbx.exe') version)).Trim()
  if ($LASTEXITCODE -ne 0 -or $version -ne $package.version) {
    throw 'The compiled application failed its version check.'
  }
  $builtArchive = Join-Path $buildRoot 'package.zip'
  Compress-Archive -Path (Join-Path $prepared '*') -DestinationPath $builtArchive -CompressionLevel Optimal

  $stagePath = [IO.Path]::GetFullPath($stage)
  if ($stagePath -ne [IO.Path]::GetFullPath((Join-Path $repoRoot 'dist\sandbox-mcp-bun'))) {
    throw 'Unexpected package replacement path.'
  }
  if (Test-Path -LiteralPath $stagePath) {
    if ((Get-Item -LiteralPath $stagePath).Attributes -band [IO.FileAttributes]::ReparsePoint) {
      throw 'Refusing to replace a linked package directory.'
    }
    Remove-Item -LiteralPath $stagePath -Recurse -Force
  }
  Move-Item -LiteralPath $prepared -Destination $stagePath
  Copy-Item -LiteralPath $builtArchive -Destination $archive -Force
  $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
  [IO.File]::WriteAllText("$archive.sha256", "$hash  $([IO.Path]::GetFileName($archive))`n", $utf8)
  Write-Output "Executable: $(Join-Path $stagePath 'sbx.exe')"
  Write-Output "Created $archive"
}
finally {
  $cleanupPath = [IO.Path]::GetFullPath($buildRoot)
  if (-not $cleanupPath.StartsWith(([IO.Path]::GetFullPath($scratchRoot) + '\'), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to clean a build directory outside the repository scratch directory.'
  }
  if (Test-Path -LiteralPath $cleanupPath) { Remove-Item -LiteralPath $cleanupPath -Recurse -Force }
}
