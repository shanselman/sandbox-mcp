// Stage 1a: lifecycle smoke. Who am I, which session, what does the desktop look like?
import { withSession, log } from './session.mjs';

const rec = await withSession('smoke', async ({ run, metadata }) => {
  const r = {};
  for (const [k, cmd] of Object.entries({
    whoami: 'whoami',
    cmdEcho: 'cmd /c echo hello from %USERNAME% in %USERPROFILE%',
    session: 'powershell -NoProfile -Command "(Get-Process -Id $PID).SessionId; [Environment]::UserInteractive; Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.SystemInformation]::VirtualScreen; [System.Windows.Forms.Screen]::AllScreens | ForEach-Object { $_.DeviceName + \' \' + $_.Bounds }"',
    workspace: `cmd /c dir "${metadata.ephemeralWorkspacePath}"`,
    explorer: 'tasklist /v /fo csv',
  })) {
    try {
      const x = await run(cmd, 60000);
      r[k] = { exitCode: x.exitCode, stdout: x.stdout?.slice(0, 4000), stderr: x.stderr?.slice(0, 2000) };
    } catch (e) { r[k] = { error: e?.code, message: e?.message }; }
    log(k, r[k].exitCode ?? r[k].error);
  }
  return r;
});
console.log(JSON.stringify(rec, null, 2));
