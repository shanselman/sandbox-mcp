# Bun Windows x64 package

[package-bun.ps1](package-bun.ps1) builds a portable Windows x64 distribution in
which `sbx.exe` contains the JavaScript application, its imported dependencies,
and the Bun runtime. It replaces the shipped `src/`, `node_modules/`, and
`node.exe` while keeping supporting resources beside the executable.

The executable supports both CLI and MCP operation. This is an additional build
option; [package.ps1](package.ps1) still produces the original Node-based package.
The release workflow builds and publishes the Node package, Bun package, and
companion Bun relinking-source archive.

## Build

Requirements:

- Native Windows x64 and PowerShell.
- Bun **1.4.4** on `PATH`. The script requires this exact validated release and
  rejects other versions unless `-AllowModifiedBun` is explicitly supplied with
  the exact Bun and WebKit source commits. It does not install or upgrade Bun.
  Changing the pin requires explicit revalidation.
- Git and a checkout with an identifiable `HEAD` commit.
- Node/npm to restore the project dependencies.
- Internet access to download the pinned Bun release's license from GitHub.
  The full LGPL texts are checked in, and dependency notices come from
  restored `node_modules/`.

From the repository root:

```powershell
npm ci --omit=dev
npm run package:bun:win
```

Windows Sandbox does not need to be enabled to build the package. Running sandbox
workflows requires the host prerequisites described in the [README](../README.md).
Recipients do not need a separate Node or Bun installation.

## Outputs and usage

The script creates:

```text
dist/
  sandbox-mcp-bun/
    sbx.exe
    sbx.cmd
    sandbox-mcp.cmd
    agent/
    skills/
    docs/
    DEPENDENCY-LICENSES/
    LICENSES/
    BUN-LICENSE.md
    RELINKING.md
    BUILD-INFO.txt
    package.json
    README.md
    LICENSE
  sandbox-mcp-win-x64-v<version>-bun.zip
  sandbox-mcp-win-x64-v<version>-bun.zip.sha256
  sandbox-mcp-win-x64-v<version>-bun-relink-source.zip
  sandbox-mcp-win-x64-v<version>-bun-relink-source.zip.sha256
```

The version comes from the repository's `package.json`. Rebuilding replaces the
Bun output folder and the ZIP/checksum for that version. The Node package and the
earlier experimental distribution in `dist/sandbox-mcp/` are separate outputs.
The runtime ZIP contains the package files directly, without an enclosing
directory. The companion source archive contains the application source snapshot
used for packaging, build script, dependency lockfile, notices, provenance, and a
SHA-256 manifest. `RELINKING.md` in both archives identifies the exact Bun and
WebKit commits and gives the upstream documented rebuild procedure. It also
explains how to build with an identified modified Bun runtime.

Extract the ZIP into its own directory and keep the supporting files with the
executable. From that directory:

```powershell
.\sbx.exe help
.\sbx.cmd doctor
.\sandbox-mcp.cmd
```

`sbx.cmd` forwards arguments to `sbx.exe`; `sandbox-mcp.cmd` invokes `sbx.exe mcp`
for MCP over stdio. Both launchers forward the executable's exit code.

`BUILD-INFO.txt` records the application and Bun versions, application/Bun/WebKit
source commits, build channel, and resource-path adjustment. The build channel
defaults to `local-candidate`; set `SANDBOX_MCP_BUILD_CHANNEL` to override it. The
script builds working-tree files; the companion source archive is the exact
application snapshot used, even when it contains uncommitted changes not captured
by the recorded Git HEAD.

The Bun 1.4.4 source commit is
`38afee6c84fcf48c0edb2466aa4b27d287a59c30`; its pinned WebKit commit is
`d6375c72affe0f70222cdcb45012b10f0bf3a9d5`. For a modified Bun build, supply
both source commits explicitly. The script records those supplied identifiers
but does not independently verify them against the executable:

```powershell
.\scripts\package-bun.ps1 -AllowModifiedBun `
  -BunSourceCommit <40-hex-commit> -WebKitCommit <40-hex-commit>
```

## How compilation works

The script copies `src/` to a unique temporary directory under the repository's
`.scratch/`, where imports can resolve the restored repository dependencies. In
that copy of `core.js`, it changes resource discovery from:

```javascript
export const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
```

to:

```javascript
export const ROOT = path.dirname(process.execPath);
```

This adjustment is necessary because compiled `import.meta.url` points into Bun's
virtual filesystem. `package.json` and the guest agent files must instead be read
from the real directory containing `sbx.exe`. The script fails if the expected
source declaration changes, so that adjustment can be reviewed before packaging.
Repository application source is not modified.

The copied `src/cli.js` is compiled with:

```text
--compile --target=bun-windows-x64 --sourcemap
--no-compile-autoload-dotenv --no-compile-autoload-bunfig
```

The existing CLI `mcp` command includes the server in the same executable. The
script copies external resources and the checked-in LGPL-2 and LGPL-2.1 texts,
downloads Bun's license, and retains dependency license/notice files found under
`node_modules/`. It checks the executable's version before preparing the runtime
ZIP and companion source ZIP with SHA256 sidecars. Temporary build files,
including the emitted source-map sidecar, are removed afterward.

## Experiment results

The implementation follows the local experiment recorded on November 8, 2026 in
`.scratch/bun-packaging-20261108/report.md`. That Git-ignored report and its test
artifacts are local evidence, not prerequisites for running this build script.

The experiment used sandbox-mcp 1.2.0 at commit
`1c9adfc59488e7977d4fcb817f17bd347f21c3bb`, Node 24.19.0, and Bun 1.4.4:

| Measurement | Node baseline | Bun experiment |
| --- | ---: | ---: |
| ZIP size, bytes | 40,867,114 | 57,767,016 |
| Extracted file size, bytes | 111,784,271 | 118,310,725 |
| File count | 3,727 | 148 |

The experimental `sbx.exe` was 116,963,328 bytes (117.0 MB, or 111.5 MiB). The
distribution had fewer files, but its ZIP was about 41.4% larger and its extracted
payload about 5.8% larger. These measurements describe the original experiment,
not guaranteed sizes for future builds. Startup speed and memory use were not
benchmarked.

## Validation and limits

The experiment passed CLI/MCP package checks, including both launchers, paths
containing spaces, external resource byte equality, and operation without Node or
Bun on `PATH`. Complete MCP initialization data and all 27 tool definitions were
compared with the Node application, along with host-only dependency diagnosis.

After enabling Windows Sandbox, the candidate passed all 16 checks in an adapted
offline integration test. Coverage included fresh guest startup, shared CLI/MCP
state, protocol authentication and rejection checks, concurrent commands and file
copies, GUI automation, screenshots, six-frame HTML recording, incremental
deployment and restart, desktop lease transfer, and owned-guest teardown. The final
sandbox list was empty. The tested executable matched the copy in the experimental
ZIP.

The new packaging script was subsequently run under Windows PowerShell, and its
ZIP passed the adapted CLI/MCP package smoke test. The script itself runs only a
version check; it does not automatically run smoke or lifecycle tests.

The tracked package tests support both distribution layouts through the same
public launchers and independently validate the relinking-source archive. From
the repository root, run:

```powershell
node test/package-smoke.js dist/sandbox-mcp-win-x64-v1.2.0.zip
node test/package-smoke.js dist/sandbox-mcp-win-x64-v1.2.0-bun.zip
node test/package-source-smoke.js dist/sandbox-mcp-win-x64-v1.2.0-bun-relink-source.zip dist/sandbox-mcp-win-x64-v1.2.0-bun.zip
```

Substitute the current application version in the filenames. The runtime smoke
test extracts each ZIP into a path containing spaces, checks its runtime-specific
layout and common resources, and exercises both `.cmd` launchers without host
Node or Bun on `PATH`. The source-package smoke test validates required files,
all three source revisions, manifest hashes, and the full LGPL texts in both
archives. A Node test driver and the repository dependencies are required to run
the tests.

For real guest validation, extract the ZIP, acquire the host's Sandbox coordination
lock, and set `SANDBOX_MCP_TEST_ROOT` to that extracted directory and
`SANDBOX_MCP_HOME` to a fresh test-state directory. Run
`node test/integration.js off` from the repository. The same integration test uses
`sbx.cmd` and `sandbox-mcp.cmd` for either package; with `SANDBOX_MCP_TEST_ROOT`
unset, it retains source-tree operation. See [release validation](../RELEASING.md)
for runtime isolation and evidence requirements. The original experimental
harnesses remain local records; they are not needed for these tracked tests.

The shared smoke test and all 16 offline integration checks were run successfully
against both the Node and Bun 1.2.0 ZIPs. Integration ran sequentially from extracted
paths containing spaces, with host Node and Bun absent from the application's
`PATH`; both owned guests were destroyed afterward.

Validation covers the tested offline lifecycle, not every command or network mode.
MP4 output was not exercised because ffmpeg was absent from the isolated test
PATH. `doctor` still labels Bun's Node compatibility version as Node.js. This
packaging process supplies technical source and license materials; it is not a
legal determination of license compliance.

The unit suite's native file-symlink capture check requires permission to create
Windows symbolic links. Without that permission, only this check is explicitly
skipped on Windows `EPERM`; the independent path-confinement and junction checks
still run. For full native symlink coverage, run `npm test` in an elevated shell
or with Developer Mode enabled. See Microsoft's
[symbolic-link creation requirements](https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-createsymboliclinkw#parameters).
