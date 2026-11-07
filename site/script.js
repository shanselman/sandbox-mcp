// Everything here is an enhancement: without JS the page shows the full agent session and the first tab of each set.
const reduceMotion = matchMedia('(prefers-reduced-motion: reduce)').matches;

function setupTabs(root) {
  const tabs = [...root.querySelectorAll('[role="tab"]')];
  const select = (tab, focus) => {
    for (const t of tabs) {
      const on = t === tab;
      t.setAttribute('aria-selected', String(on));
      t.tabIndex = on ? 0 : -1;
      document.getElementById(t.getAttribute('aria-controls')).hidden = !on;
    }
    if (focus) tab.focus();
  };
  tabs.forEach((tab, i) => {
    tab.tabIndex = i === 0 ? 0 : -1;
    tab.addEventListener('click', () => select(tab));
    tab.addEventListener('keydown', (e) => {
      const next = { ArrowRight: i + 1, ArrowDown: i + 1, ArrowLeft: i - 1, ArrowUp: i - 1, Home: 0, End: tabs.length - 1 }[e.key];
      if (next === undefined) return;
      e.preventDefault();
      select(tabs[(next + tabs.length) % tabs.length], true);
    });
  });
}

function setupCopy(button) {
  const label = button.textContent;
  button.addEventListener('click', async () => {
    const text = button.parentElement.querySelector('code').textContent;
    try {
      await navigator.clipboard.writeText(text);
      button.textContent = 'Copied';
    } catch {
      button.textContent = 'Press Ctrl+C';
      getSelection().selectAllChildren(button.parentElement.querySelector('code'));
    }
    setTimeout(() => { button.textContent = label; }, 1800);
  });
}

// Plays the illustrated session in the hero. The gaps are for reading, not measured timings.
function setupDemo(demo) {
  const sbx = demo.querySelector('#demo-sbx');
  const log = demo.querySelector('#demo-log');
  const wire = demo.querySelector('#demo-wire');
  const status = demo.querySelector('#demo-status');
  const toggle = demo.querySelector('#demo-toggle');
  const prompt = demo.querySelector('#demo-prompt');
  const answer = demo.querySelector('#demo-answer');
  const calls = [...demo.querySelectorAll('.call')];
  const promptText = prompt.textContent;
  const looks = {
    idle: [],
    boot: ['s-boot'],
    copy: ['s-desk', 's-copy'],
    app: ['s-desk', 's-app'],
    tree: ['s-desk', 's-app', 's-tree'],
    typed: ['s-desk', 's-app', 's-typed'],
    greeted: ['s-desk', 's-app', 's-typed', 's-greet'],
    shot: ['s-desk', 's-app', 's-typed', 's-greet', 's-flash'],
    gone: ['s-gone'],
  };
  const show = (state) => { sbx.className = ['sbx', ...looks[state]].join(' '); };
  const follow = () => { log.scrollTop = log.scrollHeight; };

  const steps = [];
  const add = (ms, run) => steps.push({ ms, run });
  add(250, () => {
    demo.classList.add('playing');
    prompt.textContent = '';
    calls.forEach((c) => c.classList.remove('shown', 'running'));
    answer.classList.remove('shown');
    show('idle');
    status.textContent = 'Nothing running yet';
    follow();
  });
  // Type the prompt a few characters at a time.
  for (let n = 3; n < promptText.length + 3; n += 3) add(28, () => { prompt.textContent = promptText.slice(0, n); });
  add(500, () => {});
  for (const call of calls) {
    add(Number(call.dataset.ms), () => {
      call.classList.add('shown', 'running');
      wire.classList.add('on');
      show(call.dataset.state);
      status.textContent = call.dataset.status;
      follow();
    });
    add(500, () => {
      call.classList.remove('running');
      wire.classList.remove('on');
      follow();
    });
  }
  add(0, () => { answer.classList.add('shown'); follow(); });

  let at = 0;
  let timer = null;
  let state = 'ready';
  const label = () => { toggle.textContent = { playing: 'Pause', paused: 'Resume', done: 'Replay' }[state]; };
  const tick = () => {
    if (at >= steps.length) { state = 'done'; label(); return; }
    const step = steps[at++];
    step.run();
    timer = setTimeout(tick, step.ms);
  };
  let started = false;
  const play = (reset = false) => {
    if (reset) at = 0;
    state = 'playing';
    toggle.hidden = false;
    label();
    tick();
  };
  const start = () => {
    if (started) return;
    started = true;
    play(true);
  };
  toggle.addEventListener('click', () => {
    if (state === 'done') { play(true); return; }
    clearTimeout(timer);
    if (state === 'playing') { state = 'paused'; label(); return; }
    play();
  });
  if ('IntersectionObserver' in window) {
    const observer = new IntersectionObserver((entries) => {
      if (entries.some((entry) => entry.isIntersecting)) {
        observer.disconnect();
        start();
      }
    });
    observer.observe(demo);
  } else {
    start();
  }
}

document.querySelectorAll('[data-tabs]').forEach(setupTabs);
document.querySelectorAll('.copy').forEach(setupCopy);
if (!reduceMotion) setupDemo(document.getElementById('demo'));
