<#
.SYNOPSIS
Builds the Bun-based Windows x64 portable package without changing repository source.
.EXAMPLE
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/package-bun.ps1
.NOTES
Requires Bun 1.4.4 on PATH and dependencies restored with npm ci --omit=dev.
Downloads the license for the pinned Bun release, as package.ps1 does for Node.
#>
param(
  [switch]$AllowModifiedBun,
  [string]$BunSourceCommit,
  [string]$WebKitCommit
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$requiredBunVersion = '1.4.4'
$requiredBunSourceCommit = '38afee6c84fcf48c0edb2466aa4b27d287a59c30'
$requiredWebKitCommit = 'd6375c72affe0f70222cdcb45012b10f0bf3a9d5'

function Get-Sha256 {
  param([Parameter(Mandatory = $true)][string]$Path)
  $algorithm = [Security.Cryptography.SHA256]::Create()
  $stream = [IO.File]::OpenRead($Path)
  try {
    ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
  } finally {
    $stream.Dispose()
    $algorithm.Dispose()
  }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$distRoot = Join-Path $repoRoot 'dist'
$stage = Join-Path $distRoot 'sandbox-mcp-bun'
$package = Get-Content (Join-Path $repoRoot 'package.json') -Raw | ConvertFrom-Json
$bun = Get-Command bun.exe -ErrorAction SilentlyContinue
if (-not $bun) { $bun = Get-Command bun -ErrorAction SilentlyContinue }
if (-not $bun) { throw "Bun is missing. Install Bun $requiredBunVersion and ensure it is on PATH before running this script." }

$bunVersion = ([string](& $bun.Source --version)).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot determine the installed Bun version.' }
if ($bunVersion -ne $requiredBunVersion -and -not $AllowModifiedBun) {
  throw "Bun $requiredBunVersion is required; found '$bunVersion'. Other versions have not been validated. Install the pinned version before packaging."
}
if ($AllowModifiedBun) {
  if (-not $BunSourceCommit -or -not $WebKitCommit) {
    throw 'Modified Bun builds require -BunSourceCommit and -WebKitCommit so the corresponding source can be identified.'
  }
} else {
  if (($BunSourceCommit -and $BunSourceCommit -ne $requiredBunSourceCommit) -or
      ($WebKitCommit -and $WebKitCommit -ne $requiredWebKitCommit)) {
    throw 'The source commits do not match the pinned Bun 1.4.4 build. Use -AllowModifiedBun to identify a modified runtime.'
  }
  $BunSourceCommit = $requiredBunSourceCommit
  $WebKitCommit = $requiredWebKitCommit
}
foreach ($commit in @($BunSourceCommit, $WebKitCommit)) {
  if ($commit -notmatch '^[a-f0-9]{40}$') { throw "Invalid source commit: '$commit'." }
}
$BunSourceCommit = $BunSourceCommit.ToLowerInvariant()
$WebKitCommit = $WebKitCommit.ToLowerInvariant()
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
$sourceArchive = Join-Path $distRoot "sandbox-mcp-win-x64-v$($package.version)-bun-relink-source.zip"

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
  Invoke-WebRequest -UseBasicParsing -Uri "https://raw.githubusercontent.com/oven-sh/bun/bun-v$requiredBunVersion/LICENSE.md" -OutFile (Join-Path $prepared 'BUN-LICENSE.md')
  Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSES') -Destination $prepared -Recurse
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
  $relinking = @'
# Relinking the Bun package

The executable bundles Bun {{BUN_VERSION}}, which statically links JavaScriptCore and WebCore (LGPL-2) and TinyCC (LGPL-2.1). The complete LGPL texts are in `LICENSES/`. The sandbox-mcp application remains MIT-licensed.

## Exact source revisions

- sandbox-mcp application: Git HEAD {{APP_COMMIT}}, at https://github.com/shanselman/sandbox-mcp/tree/{{APP_COMMIT}}. The companion `-bun-relink-source.zip` contains the exact application snapshot, including any uncommitted changes.
- Bun source: commit {{BUN_COMMIT}}; upstream tree, when available: https://github.com/oven-sh/bun/tree/{{BUN_COMMIT}}
- WebKit/JavaScriptCore source: commit {{WEBKIT_COMMIT}}; upstream tree, when available: https://github.com/oven-sh/WebKit/tree/{{WEBKIT_COMMIT}}

The bundled `BUN-LICENSE.md` describes Bun's relinking process. For Windows build prerequisites, follow Bun's [Windows build guide](https://bun.com/docs/project/building-windows) for this source revision.

## Rebuild with a modified JavaScriptCore

1. Check out the Bun source revision above, clone `https://github.com/oven-sh/WebKit` into `vendor/WebKit`, and run `bun sync-webkit-source`. Verify that `vendor/WebKit` is at the commit above, make the desired library changes, and run `bun run build:local`.
2. Check out the sandbox-mcp application commit above and overlay the companion source archive on that checkout to include the exact build snapshot. Restore dependencies with `npm ci --omit=dev` and put the modified Bun executable on `PATH`.
3. For any modified Bun build, even one that reports Bun {{PINNED_VERSION}}, identify both source commits when rebuilding:

   ```powershell
   .\scripts\package-bun.ps1 -AllowModifiedBun -BunSourceCommit <40-hex-commit> -WebKitCommit <40-hex-commit>
   ```

For modified builds, the script records the supplied source commit identifiers but does not independently verify them against the executable or ensure they exist at the upstream URLs. Make forked sources and patches available with the build.

The script applies the documented resource-root adjustment to a temporary source copy, then invokes Bun's `build --compile` for Windows x64. The companion source archive contains the application source, build script, dependency lockfile, notices, and this guide.
'@
  $relinking = $relinking.Replace('{{BUN_VERSION}}', $bunVersion)
  $relinking = $relinking.Replace('{{APP_COMMIT}}', $sourceCommit)
  $relinking = $relinking.Replace('{{BUN_COMMIT}}', $BunSourceCommit)
  $relinking = $relinking.Replace('{{WEBKIT_COMMIT}}', $WebKitCommit)
  $relinking = $relinking.Replace('{{PINNED_VERSION}}', $requiredBunVersion)
  [IO.File]::WriteAllText((Join-Path $prepared 'RELINKING.md'), $relinking, [Text.UTF8Encoding]::new($false))
  $utf8 = [Text.UTF8Encoding]::new($false)
  [IO.File]::WriteAllText((Join-Path $prepared 'sbx.cmd'), "@echo off`r`n`"%~dp0sbx.exe`" %*`r`nexit /b %errorlevel%`r`n", $utf8)
  [IO.File]::WriteAllText((Join-Path $prepared 'sandbox-mcp.cmd'), "@echo off`r`n`"%~dp0sbx.exe`" mcp %*`r`nexit /b %errorlevel%`r`n", $utf8)
  @(
    "sandbox-mcp $($package.version)"
    'Platform: Windows x64'
    "Bundled runtime: Bun $bunVersion (license: BUN-LICENSE.md)"
    "Source commit: $sourceCommit"
    "Bun source commit: $BunSourceCommit"
    "WebKit source commit: $WebKitCommit"
    "Build channel: $channel"
    'Resource ROOT: executable directory (build-copy adjustment)'
  ) | Set-Content -LiteralPath (Join-Path $prepared 'BUILD-INFO.txt') -Encoding UTF8

  $version = ([string](& (Join-Path $prepared 'sbx.exe') version)).Trim()
  if ($LASTEXITCODE -ne 0 -or $version -ne $package.version) {
    throw 'The compiled application failed its version check.'
  }
  $builtArchive = Join-Path $buildRoot 'package.zip'
  Compress-Archive -Path (Join-Path $prepared '*') -DestinationPath $builtArchive -CompressionLevel Optimal

  $relinkSource = Join-Path $buildRoot 'relink-source'
  New-Item -ItemType Directory -Path $relinkSource | Out-Null
  foreach ($item in @('src', 'agent', 'skills', 'docs', 'test', 'scripts')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $item) -Destination $relinkSource -Recurse
  }
  foreach ($item in @('README.md', 'RELEASING.md', 'LICENSE', 'package.json', 'package-lock.json')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $item) -Destination $relinkSource
  }
  foreach ($item in @('BUN-LICENSE.md', 'RELINKING.md', 'LICENSES', 'DEPENDENCY-LICENSES')) {
    Copy-Item -LiteralPath (Join-Path $prepared $item) -Destination $relinkSource -Recurse
  }
  @(
    "sandbox-mcp $($package.version)"
    "Application source commit: $sourceCommit"
    "Bun version: $bunVersion"
    "Bun source commit: $BunSourceCommit"
    "WebKit source commit: $WebKitCommit"
    'Application source in this archive is the exact working-tree snapshot used by the package build.'
  ) | Set-Content -LiteralPath (Join-Path $relinkSource 'SOURCE-INFO.txt') -Encoding UTF8
  $manifest = Get-ChildItem -LiteralPath $relinkSource -Recurse -File | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($relinkSource.Length + 1).Replace('\', '/')
    $hash = Get-Sha256 -Path $_.FullName
    "$hash  $relative"
  }
  [IO.File]::WriteAllLines((Join-Path $relinkSource 'SOURCE-MANIFEST.sha256'), $manifest, $utf8)
  $builtSourceArchive = Join-Path $buildRoot 'relink-source.zip'
  Compress-Archive -Path (Join-Path $relinkSource '*') -DestinationPath $builtSourceArchive -CompressionLevel Optimal

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
  Copy-Item -LiteralPath $builtSourceArchive -Destination $sourceArchive -Force
  $hash = Get-Sha256 -Path $archive
  [IO.File]::WriteAllText("$archive.sha256", "$hash  $([IO.Path]::GetFileName($archive))`n", $utf8)
  $sourceHash = Get-Sha256 -Path $sourceArchive
  [IO.File]::WriteAllText("$sourceArchive.sha256", "$sourceHash  $([IO.Path]::GetFileName($sourceArchive))`n", $utf8)
  Write-Output "Executable: $(Join-Path $stagePath 'sbx.exe')"
  Write-Output "Created $archive"
  Write-Output "Created $sourceArchive"
}
finally {
  $cleanupPath = [IO.Path]::GetFullPath($buildRoot)
  if (-not $cleanupPath.StartsWith(([IO.Path]::GetFullPath($scratchRoot) + '\'), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to clean a build directory outside the repository scratch directory.'
  }
  if (Test-Path -LiteralPath $cleanupPath) { Remove-Item -LiteralPath $cleanupPath -Recurse -Force }
}
