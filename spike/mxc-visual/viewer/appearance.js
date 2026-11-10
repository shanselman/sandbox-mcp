// Read back only the public canvas theme contract, never parent-window state.
const appearanceLabel = document.getElementById('appearance');
let lastAppearance = '';
const cspDirectives = new Set();
document.addEventListener('securitypolicyviolation', event => {
  if (typeof event.violatedDirective === 'string') cspDirectives.add(event.violatedDirective.slice(0, 80));
});
async function reportAppearance() {
  const rootMode = document.documentElement.getAttribute('data-color-mode');
  const bodyMode = document.body.getAttribute('data-color-mode');
  const hostMode = [rootMode, bodyMode].find(mode => mode === 'light' || mode === 'dark');
  const style = getComputedStyle(document.body);
  const backgroundToken = style.getPropertyValue('--background-color-default').trim();
  const hasHostTheme = Boolean(hostMode || backgroundToken);
  const cssMode = ['light', 'dark'].includes(style.colorScheme) ? style.colorScheme : null;
  const snapshot = {
    source: hasHostTheme ? 'host' : 'system',
    mode: hostMode ?? cssMode ?? (hasHostTheme ? null : matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'),
    background: style.backgroundColor, foreground: style.color,
    backgroundToken,
    font: style.fontFamily,
    cspDirectives: [...cspDirectives].slice(0, 8),
  };
  appearanceLabel.textContent = `${hasHostTheme ? 'App' : 'System'} theme${snapshot.mode ? `: ${snapshot.mode}` : ' (host colors)'}`;
  const serialized = JSON.stringify(snapshot);
  if (serialized === lastAppearance) return;
  try {
    const response = await fetch('appearance', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: serialized,
    });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    lastAppearance = serialized;
  } catch (error) {
    appearanceLabel.textContent += ` (theme readback unavailable: ${error.message})`;
  }
}
reportAppearance();
setInterval(reportAppearance, 1000);
