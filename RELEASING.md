# Releasing

A release is a tag plus Windows x64 portable ZIPs: the Node package, the Bun
package, and a companion Bun relinking-source archive. GitHub Actions builds,
checksums, and attaches all three; nothing is uploaded by hand.

## Verify the build before publication

Commit intended source/docs/screenshots and build with `npm run package:win` and
`npm run package:bun:win`. The Bun package also requires Bun 1.4.4 on `PATH`; its
companion source ZIP contains the source snapshot, license texts, provenance, and
relinking guide. Record the full commit and each ZIP SHA256 alongside real
run/report paths. A preview is not a published release; do not imply that
`/releases/latest` contains an unpublished build.

Run `node test\package-smoke.js <zip>` for both the Node and Bun packages, then
`node test\package-source-smoke.js <relink-source.zip> <bun-package.zip>`. These
checks validate runtime layout, shipped resources, source provenance and
manifest, full LGPL texts, and CLI/MCP launchers with host Node and Bun absent
from PATH.
Extract that exact ZIP and run `test\integration.js` using a Node test driver, a fresh isolated
`SANDBOX_MCP_HOME`, `SANDBOX_MCP_TEST_ROOT`, `SANDBOX_MCP_TEST_SHA` and
`SANDBOX_MCP_EXPECT_NO_HOST_NODE=1`. Keep Windows system directories and the WindowsApps `wsb.exe` alias on
PATH but remove host Node and Bun. The driver can be started by its absolute path; application processes
use the extracted package's two `.cmd` launchers. With `SANDBOX_MCP_TEST_ROOT` unset, the integration test
continues to run the source application. Verify owned-guest teardown. Hosted CI cannot provide this real desktop proof.
The compatibility screenshots and observations must describe tested builds, not unmeasured framework promises.
After an authorized feature-branch push, `gh workflow run release.yml --ref <branch>` provides hosted builds
without publishing. The workflow names all three ZIPs as feature previews, includes
`.sha256` sidecars and source commit/build channel in `BUILD-INFO.txt`, and uploads
them as `sandbox-mcp-win-x64`. Download and verify those exact artifacts for the
real extracted-package proof. A source push or successful manual build is not a
release. Published-release workflows upload all three versioned ZIPs and checksums.

## Publishing an authorized release

1. **Bump the version:** `npm version X.Y.Z --no-git-tag-version` (updates `package.json` and the lockfile). Set the
   same version in `plugin.json` and `.github/plugin/marketplace.json`, and the `#vX.Y.Z` tag in `mcp.json`;
   `npm test` fails until they all match. Commit, push to `main` and wait for `ci` to go green.
2. **Dry run:** `gh workflow run release.yml`, then `gh run watch`. It runs the tests, builds both runtime packages and
   the Bun relinking-source archive, smoke-tests them, and keeps all ZIPs/checksums as the
   `sandbox-mcp-win-x64` artifact. It never touches a release. Do this before every release: the
   hosted runner is the only place the packaged CLI is tested on a PC that isn't yours.
3. **Publish:** `gh release create vX.Y.Z --target main --title "..." --notes-file notes.md`. Publishing runs the
   workflow again, which refuses to upload unless the tag equals `v<package.json version>`, then attaches the
   Node package, Bun package, and Bun relinking-source ZIP with checksums.
4. **Check the assets:** download the versioned Node ZIP, Bun ZIP, relinking-source ZIP, and their `.sha256` sidecars.
   Verify each digest, run `node test/package-smoke.js` for both runtime ZIPs, and run
   `node test/package-source-smoke.js <relink-source.zip> <bun-package.zip>`. Then do one real run, because CI can't
   boot a sandbox: extract a package, take Node off PATH, and `sbx run C:\Windows\System32\charmap.exe --open`.
5. **Check the plugin:** run `npm run test:plugin` on Windows. Without an override it starts the server from the new
   tag, exactly as an installed plugin does.

Roll back a bad release with `gh release delete vX.Y.Z --cleanup-tag --yes`.

## Things that have bitten us

- **Publish soon after bumping.** `mcp.json` on `main` points at the new tag as soon as the bump lands, so a plugin
  installed before the release is published can't start its server. Deleting a release's tag breaks plugins pinned to
  it in the same way, so after a rollback, move `main` back to a version whose tag exists.
- **The CI runner is Windows Server, not Windows Pro.** It has no Windows Sandbox and no `wsb`, so `sbx doctor` must
  degrade gracefully there. The first dry run caught `doctor --json` printing nothing on the runner.
- **Keep the readiness probe in `src/core.js` to one PowerShell statement per fact.** It runs under
  `$ErrorActionPreference = 'SilentlyContinue'`, where one failing element inside a single `[ordered]@{...}` literal
  silences the whole output. An optional value written as `$(if (...) { ... })` serializes as `{}` when empty, which
  is truthy in JS; assign it inside the `if` instead.
- Only Windows x64 is tested natively. There is no ARM64 package.
