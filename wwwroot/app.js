'use strict';

// Launcher UI. Talks to the C# host via window.chrome.webview messages:
//   host -> page: { type: 'state', config, monitors, targetMonitor, audioDevices, running, notices }, { type: 'toast', text }
//   page -> host: ready | launch {id} | saveSettings {...} | openConfig | quit

const webview = window.chrome && window.chrome.webview;
const DEFAULT_ACCENT = '#2a3550';

const ICONS = {
  jellyfin: `<svg viewBox="0 0 100 92" aria-hidden="true">
      <path fill="#fff" fill-rule="evenodd" d="M50 2C42 2 2 74 6 82s84 8 88 0S58 2 50 2Zm0 24c-4 0-25 42-23 46s44 4 46 0S54 26 50 26Z"/>
      <path fill="#fff" d="M50 44c-2.4 0-13 22-12 24.5s23 2.5 24 0S52.4 44 50 44Z"/>
    </svg>`,
  youtube: `<svg viewBox="0 0 100 70" aria-hidden="true">
      <rect width="100" height="70" rx="18" fill="#ff0033"/>
      <path d="M40 20 68 35 40 50Z" fill="#fff"/>
    </svg>`,
  settings: `<svg viewBox="0 0 100 100" aria-hidden="true">
      <mask id="gear-hole"><rect width="100" height="100" fill="#fff"/><circle cx="50" cy="50" r="13" fill="#000"/></mask>
      <g fill="#fff" mask="url(#gear-hole)">
        ${[0, 45, 90, 135].map((a) => `<rect x="42" y="6" width="16" height="88" rx="4" transform="rotate(${a} 50 50)"/>`).join('')}
        <circle cx="50" cy="50" r="31"/>
      </g>
    </svg>`,
};

const els = {
  tiles: document.getElementById('tiles'),
  heroTitle: document.getElementById('hero-title'),
  heroSub: document.getElementById('hero-sub'),
  hint: document.getElementById('hint'),
  notice: document.getElementById('notice'),
  clock: document.getElementById('clock'),
  settings: document.getElementById('settings'),
  settingsList: document.getElementById('settings-list'),
  toast: document.getElementById('toast'),
};

let state = null;
let screen = 'home';
let homeIndex = readStored('homeIndex', 0);
let settingsRows = [];
let settingsIndex = 0;
let draft = null;
let toastTimer = 0;

function readStored(key, fallback) {
  try {
    const v = localStorage.getItem(key);
    return v === null ? fallback : JSON.parse(v);
  } catch {
    return fallback;
  }
}

function writeStored(key, value) {
  try { localStorage.setItem(key, JSON.stringify(value)); } catch { /* ignore */ }
}

function send(msg) {
  if (webview) webview.postMessage(JSON.stringify(msg));
}

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

// ---------------------------------------------------------------- host messages

function onHostMessage(msg) {
  if (msg.type === 'state') {
    state = msg;
    renderHome();
    renderStatus();
    // Don't yank the rows out from under someone typing an address.
    if (screen === 'settings' && document.activeElement?.tagName !== 'INPUT') renderSettings();
  } else if (msg.type === 'toast') {
    toast(msg.text);
  }
}

if (webview) {
  webview.addEventListener('message', (e) => {
    try { onHostMessage(JSON.parse(e.data)); } catch (err) { console.error(err); }
  });
}

function renderStatus() {
  const notices = (state && state.notices) || [];
  els.notice.hidden = notices.length === 0;
  els.notice.textContent = notices.join('  ·  ');

  const hotkeys = state ? [...(state.config.tvHotkeys || []), ...(state.config.homeHotkeys || [])] : [];
  const keyName = (k) => (k === 'BrowserHome' ? 'Home key' : k);
  const homeHelp = [hotkeys[0] && keyName(hotkeys[0]), state && state.config.gamepadHome ? 'hold Back + Start' : null].filter(Boolean).join(' or ');
  els.hint.textContent = `◀ ▶ choose   ·   OK open   ·   ${homeHelp || 'Back'} returns here`;
}

// ---------------------------------------------------------------- home

function homeItems() {
  const apps = state ? state.config.apps : [];
  return [...apps.map((app) => ({ kind: 'app', app })), { kind: 'settings' }];
}

function itemInfo(item) {
  if (item.kind === 'settings') {
    return {
      id: 'settings',
      name: 'Settings',
      description: 'TV display, sound output and apps',
      background: 'linear-gradient(160deg, #50566360 0%, #23262d 100%), #2c3038',
      accent: '#353b47',
      icon: 'settings',
    };
  }
  const a = item.app;
  return {
    id: a.id,
    name: a.name,
    description: a.description || '',
    background: a.background || 'linear-gradient(160deg, #3a3f4b, #1d2027)',
    accent: a.accent || DEFAULT_ACCENT,
    icon: a.icon,
  };
}

function buildIcon(info) {
  if (ICONS[info.icon]) {
    const holder = el('div');
    holder.innerHTML = ICONS[info.icon];
    return holder.firstElementChild;
  }
  if (info.icon && /^(https?:|data:image\/)/.test(info.icon)) {
    const img = new Image();
    img.src = info.icon;
    img.alt = '';
    return img;
  }
  return el('span', 'letter', (info.name || '?').charAt(0).toUpperCase());
}

function renderHome() {
  const items = homeItems();
  homeIndex = Math.min(Math.max(homeIndex, 0), items.length - 1);

  els.tiles.replaceChildren(...items.map((item, i) => {
    const info = itemInfo(item);
    const wrap = el('div', 'tile-wrap');
    const tile = el('div', 'tile');
    tile.style.background = info.background;
    tile.append(buildIcon(info));
    if (state && state.running === info.id) tile.append(el('span', 'badge', 'Running'));
    wrap.append(tile, el('div', 'tile-label', info.name));

    wrap.addEventListener('mouseenter', () => { homeIndex = i; updateHomeFocus(); });
    wrap.addEventListener('click', () => { homeIndex = i; updateHomeFocus(); activateHome(); });
    return wrap;
  }));

  updateHomeFocus();
}

function updateHomeFocus() {
  const item = homeItems()[homeIndex];
  if (!item) return;
  [...els.tiles.children].forEach((node, i) => node.classList.toggle('focused', i === homeIndex));
  const info = itemInfo(item);
  els.heroTitle.textContent = info.name;
  els.heroSub.textContent = info.description;
  document.documentElement.style.setProperty('--glow', info.accent);
  writeStored('homeIndex', homeIndex);
}

function moveHome(delta) {
  const count = homeItems().length;
  const next = Math.min(Math.max(homeIndex + delta, 0), count - 1);
  if (next !== homeIndex) {
    homeIndex = next;
    updateHomeFocus();
  }
}

function activateHome() {
  const item = homeItems()[homeIndex];
  if (!item) return;
  if (item.kind === 'settings') {
    openSettings();
    return;
  }
  toast(`Opening ${item.app.name}…`);
  send({ type: 'launch', id: item.app.id });
}

// ---------------------------------------------------------------- settings

function openSettings() {
  if (!state) return;
  const currentDefault = (state.audioDevices.find((d) => d.isDefault) || {}).id || null;
  draft = {
    monitorDevice: state.targetMonitor,
    audioDeviceId: state.config.audio.deviceId || currentDefault,
    switchAudio: state.config.audio.switchSystemDefault,
    startWithWindows: state.config.startWithWindows,
    urls: Object.fromEntries(
      state.config.apps
        .filter((a) => a.type === 'web' || a.type === 'firefox')
        .map((a) => [a.id, a.url || ''])),
  };
  settingsRows = buildSettingsRows();
  settingsIndex = 0;
  screen = 'settings';
  els.settings.classList.add('active');
  renderSettings();
}

function closeSettings() {
  if (document.activeElement) document.activeElement.blur();
  screen = 'home';
  draft = null;
  els.settings.classList.remove('active');
}

function saveSettings() {
  send({ type: 'saveSettings', ...draft });
  closeSettings();
}

function audioOptions() {
  const options = state.audioDevices.map((d) => ({
    value: d.id,
    label: d.name + (d.isDefault ? '  (current default)' : ''),
  }));
  if (draft.audioDeviceId && !options.some((o) => o.value === draft.audioDeviceId)) {
    options.unshift({ value: draft.audioDeviceId, label: `${state.config.audio.deviceName || 'Saved device'} (not connected)` });
  }
  return options;
}

function buildSettingsRows() {
  const rows = [
    {
      kind: 'select', label: 'TV display', key: 'monitorDevice',
      options: () => state.monitors.map((m) => ({ value: m.device, label: m.label })),
    },
    { kind: 'select', label: 'Sound output', key: 'audioDeviceId', options: audioOptions },
    { kind: 'toggle', label: 'Use this sound output while Jens-TV runs', key: 'switchAudio' },
    { kind: 'toggle', label: 'Start with Windows', key: 'startWithWindows' },
  ];

  state.config.apps
    .filter((a) => a.id in draft.urls)
    .forEach((a, i) => rows.push({ kind: 'text', label: `${a.name} address`, appId: a.id, gap: i === 0 }));

  rows.push(
    { kind: 'button', label: 'Save', action: saveSettings, gap: true },
    { kind: 'button', label: 'Cancel', action: closeSettings },
    { kind: 'button', label: 'Edit config file (opens on the PC)', action: () => send({ type: 'openConfig' }) },
    { kind: 'button', label: 'Quit Jens-TV', action: () => send({ type: 'quit' }), danger: true },
  );
  return rows;
}

function renderSettings() {
  if (!draft) return;
  els.settingsList.replaceChildren(...settingsRows.map((row, i) => {
    const node = el('div', `row ${row.kind}${row.gap ? ' gap' : ''}${row.danger ? ' danger' : ''}`);
    node.append(el('span', 'label', row.label));

    if (row.kind === 'select') {
      const option = row.options().find((o) => o.value === draft[row.key]);
      node.append(el('span', 'value', option ? option.label : 'None'));
    } else if (row.kind === 'toggle') {
      node.append(el('span', 'value', draft[row.key] ? 'On' : 'Off'));
    } else if (row.kind === 'text') {
      const input = el('input');
      input.type = 'text';
      input.spellcheck = false;
      input.value = draft.urls[row.appId];
      input.addEventListener('input', () => { draft.urls[row.appId] = input.value; });
      input.addEventListener('focus', () => { settingsIndex = i; updateSettingsFocus(); });
      node.append(input);
    }

    node.addEventListener('mouseenter', () => { settingsIndex = i; updateSettingsFocus(); });
    node.addEventListener('click', (e) => {
      settingsIndex = i;
      updateSettingsFocus();
      if (e.target.tagName !== 'INPUT') activateRow();
    });
    return node;
  }));
  updateSettingsFocus();
}

function updateSettingsFocus() {
  [...els.settingsList.children].forEach((node, i) => {
    node.classList.toggle('focused', i === settingsIndex);
    if (i === settingsIndex) node.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
  });
}

function moveSettings(delta) {
  settingsIndex = Math.min(Math.max(settingsIndex + delta, 0), settingsRows.length - 1);
  updateSettingsFocus();
}

function changeRow(delta) {
  const row = settingsRows[settingsIndex];
  if (row.kind === 'select') {
    const options = row.options();
    if (options.length === 0) return;
    const current = options.findIndex((o) => o.value === draft[row.key]);
    draft[row.key] = options[(current + delta + options.length) % options.length].value;
  } else if (row.kind === 'toggle') {
    draft[row.key] = !draft[row.key];
  } else {
    return;
  }
  renderSettings();
}

function activateRow() {
  const row = settingsRows[settingsIndex];
  if (row.kind === 'select' || row.kind === 'toggle') {
    changeRow(1);
  } else if (row.kind === 'text') {
    const input = els.settingsList.children[settingsIndex].querySelector('input');
    input.focus();
    input.select();
  } else if (row.kind === 'button') {
    row.action();
  }
}

// ---------------------------------------------------------------- input

function handleAction(action) {
  if (screen === 'home') {
    if (action === 'left') moveHome(-1);
    else if (action === 'right') moveHome(1);
    else if (action === 'ok') activateHome();
    else if (action === 'menu') openSettings();
  } else {
    if (action === 'up') moveSettings(-1);
    else if (action === 'down') moveSettings(1);
    else if (action === 'left') changeRow(-1);
    else if (action === 'right') changeRow(1);
    else if (action === 'ok') activateRow();
    else if (action === 'back' || action === 'menu') closeSettings();
  }
}

const KEY_ACTIONS = {
  ArrowLeft: 'left', ArrowRight: 'right', ArrowUp: 'up', ArrowDown: 'down',
  Enter: 'ok', ' ': 'ok', MediaPlayPause: 'ok',
  Escape: 'back', Backspace: 'back', BrowserBack: 'back',
  ContextMenu: 'menu',
};

document.addEventListener('keydown', (e) => {
  const active = document.activeElement;
  if (active && active.tagName === 'INPUT') {
    // Typing in an address field: Enter/Esc/Up/Down leave the field, everything else edits text.
    if (['Enter', 'Escape', 'ArrowUp', 'ArrowDown'].includes(e.key)) {
      e.preventDefault();
      active.blur();
      if (e.key === 'ArrowUp') handleAction('up');
      if (e.key === 'ArrowDown') handleAction('down');
    }
    return;
  }
  const action = KEY_ACTIONS[e.key];
  if (!action) return;
  e.preventDefault();
  handleAction(action);
});

// Gamepad (standard mapping): D-pad / left stick, A = OK, B = back, Start = settings.
const REPEATING = new Set(['up', 'down', 'left', 'right']);
const pad = { held: {}, nextRepeat: {} };

function readPads() {
  const pressed = {};
  for (const gp of navigator.getGamepads ? navigator.getGamepads() : []) {
    if (!gp || !gp.connected) continue;
    const b = (i) => gp.buttons[i] && gp.buttons[i].pressed;
    const [x = 0, y = 0] = gp.axes;
    if (b(12) || y < -0.6) pressed.up = true;
    if (b(13) || y > 0.6) pressed.down = true;
    if (b(14) || x < -0.6) pressed.left = true;
    if (b(15) || x > 0.6) pressed.right = true;
    if (b(0)) pressed.ok = true;
    if (b(1)) pressed.back = true;
    if (b(9) && !b(8)) pressed.menu = true; // Back+Start is the host's "home" chord
  }
  return pressed;
}

function pollGamepads(now) {
  const pressed = readPads();
  // When another app (Firefox) has focus, swallow input so we don't act on presses meant for it.
  const listening = document.hasFocus();
  for (const action of ['up', 'down', 'left', 'right', 'ok', 'back', 'menu']) {
    if (!pressed[action]) {
      pad.held[action] = false;
      continue;
    }
    if (!pad.held[action]) {
      pad.held[action] = true;
      pad.nextRepeat[action] = now + 420;
      if (listening) handleAction(action);
    } else if (listening && REPEATING.has(action) && now >= pad.nextRepeat[action]) {
      pad.nextRepeat[action] = now + 130;
      handleAction(action);
    }
  }
  requestAnimationFrame(pollGamepads);
}

// Treat buttons already held at load (e.g. the A press that came back from an app) as consumed.
Object.keys(readPads()).forEach((a) => { pad.held[a] = true; });
requestAnimationFrame(pollGamepads);

// Mouse: cursor appears on movement and hides again when idle.
let cursorTimer = 0;
document.addEventListener('mousemove', () => {
  document.body.classList.remove('hide-cursor');
  clearTimeout(cursorTimer);
  cursorTimer = setTimeout(() => document.body.classList.add('hide-cursor'), 2500);
});

// ---------------------------------------------------------------- misc

function toast(text) {
  els.toast.textContent = text;
  els.toast.classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => els.toast.classList.remove('show'), 3500);
}

function tickClock() {
  els.clock.textContent = new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

document.getElementById('settings-icon').innerHTML = ICONS.settings;
tickClock();
setInterval(tickClock, 5000);

if (webview) {
  send({ type: 'ready' });
} else {
  // Opened directly in a browser: render demo data so the UI can be styled without the host.
  onHostMessage({
    type: 'state',
    config: {
      homeHotkeys: ['BrowserHome'], gamepadHome: true, startWithWindows: false,
      audio: { deviceId: 'a', switchSystemDefault: true },
      apps: [
        { id: 'jellyfin', name: 'Jellyfin', type: 'web', url: 'http://localhost:8096', icon: 'jellyfin',
          description: 'Movies, shows and music from your library',
          background: 'linear-gradient(135deg, #aa5cc3 0%, #00a4dc 100%)', accent: '#4b3a8c' },
        { id: 'youtube', name: 'YouTube', type: 'firefox', url: 'https://www.youtube.com/tv', icon: 'youtube',
          description: "YouTube's TV interface, running in Firefox",
          background: 'linear-gradient(160deg, #ffffff 0%, #e6e6ea 100%)', accent: '#7a1020' },
      ],
    },
    monitors: [{ device: '\\\\.\\DISPLAY1', label: 'Desk monitor · 3440×1440 · primary' }, { device: '\\\\.\\DISPLAY2', label: 'LG TV · 3840×2160' }],
    targetMonitor: '\\\\.\\DISPLAY2',
    audioDevices: [{ id: 'a', name: 'LG TV (NVIDIA High Definition Audio)' }, { id: 'b', name: 'Speakers (Realtek USB Audio)', isDefault: true }],
    running: null,
    notices: [],
  });
}
