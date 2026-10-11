'use strict';

// Launcher UI. Talks to the C# host via window.chrome.webview messages:
//   host -> page: { type: 'state', config, monitors, targetMonitor, audioDevices, running, notices }, { type: 'toast', text }
//   page -> host: ready | launch {id} | saveSettings {...} | openConfig | quit

const webview = window.chrome && window.chrome.webview;
const DEFAULT_ACCENT = '#2a3550';

const ICONS = {
  jellyfin: `<svg class="jellyfin-logo" viewBox="0 0 100 92" aria-hidden="true">
      <defs><linearGradient id="jellyfin-gradient" x1="0" y1="0" x2="1" y2="1">
        <stop offset="0" stop-color="#aa5cc3"/><stop offset="1" stop-color="#00a4dc"/>
      </linearGradient></defs>
      <path fill="#fff" fill-rule="evenodd" d="M50 2C42 2 2 74 6 82s84 8 88 0S58 2 50 2Zm0 24c-4 0-25 42-23 46s44 4 46 0S54 26 50 26Z"/>
      <path fill="#fff" d="M50 44c-2.4 0-13 22-12 24.5s23 2.5 24 0S52.4 44 50 44Z"/>
    </svg>`,
  youtube: `<svg viewBox="0 0 100 70" aria-hidden="true">
      <rect width="100" height="70" rx="18" fill="#ff0033"/>
      <path d="M40 20 68 35 40 50Z" fill="#fff"/>
    </svg>`,
  spotify: `<svg viewBox="0 0 100 100" aria-hidden="true">
      <circle cx="50" cy="50" r="48" fill="#1ed760"/>
      <g fill="none" stroke="#0b0b0b" stroke-linecap="round">
        <path d="M24 38c17-5.5 36-4.5 52 4" stroke-width="7.5"/>
        <path d="M28 53c14-4.2 29-3.2 42 3.5" stroke-width="6.5"/>
        <path d="M31.5 67c11-3.2 22-2.4 32 2.8" stroke-width="5.5"/>
      </g>
    </svg>`,
  f1tv: `<svg class="f1tv-logo" viewBox="0 0 120 60" aria-hidden="true">
      <text x="60" y="44" text-anchor="middle" fill="#fff" font-family="Segoe UI, sans-serif" font-size="40"
        font-weight="900" font-style="italic" letter-spacing="-1">F1 TV</text>
    </svg>`,
};

const POWER_ICON = `<svg viewBox="0 0 100 100" aria-hidden="true">
    <g fill="none" stroke="currentColor" stroke-width="11" stroke-linecap="round">
      <path d="M31 27a33 33 0 1 0 38 0"/>
      <path d="M50 12v36"/>
    </g>
  </svg>`;

// Gear in the text colour; each copy needs its own mask id.
function gearIcon(maskId) {
  return `<svg viewBox="0 0 100 100" aria-hidden="true">
      <mask id="${maskId}"><rect width="100" height="100" fill="#fff"/><circle cx="50" cy="50" r="13" fill="#000"/></mask>
      <g fill="currentColor" mask="url(#${maskId})">
        ${[0, 45, 90, 135].map((a) => `<rect x="42" y="6" width="16" height="88" rx="4" transform="rotate(${a} 50 50)"/>`).join('')}
        <circle cx="50" cy="50" r="31"/>
      </g>
    </svg>`;
}

const els = {
  tiles: document.getElementById('tiles'),
  heroTitle: document.getElementById('hero-title'),
  heroSub: document.getElementById('hero-sub'),
  hint: document.getElementById('hint'),
  notice: document.getElementById('notice'),
  clock: document.getElementById('clock'),
  settingsButton: document.getElementById('settings-button'),
  powerButton: document.getElementById('power-button'),
  settings: document.getElementById('settings'),
  settingsList: document.getElementById('settings-list'),
  toast: document.getElementById('toast'),
};

let state = null;
let screen = 'home';
let homeIndex = readStored('homeIndex', 0);
let topFocus = null; // 'settings' / 'power' when home focus is on a top-right button instead of a tile
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
  } else if (msg.type === 'slideshow') {
    startSlideshow(msg);
  } else if (msg.type === 'folderPicked') {
    if (draft) {
      draft.slideshowFolder = msg.path;
      renderSettings();
    }
  }
}

// ---------------------------------------------------------------- background slideshow

const slideshow = { images: [], order: [], pos: 0, intervalMs: 30000, timer: 0, front: 0 };
const slideEls = [...document.querySelectorAll('.slide')];

function startSlideshow(msg) {
  clearTimeout(slideshow.timer);
  slideshow.images = msg.images || [];
  slideshow.intervalMs = (msg.intervalSeconds || 30) * 1000;
  slideshow.order = slideshow.images.map((_, i) => i);
  if (msg.shuffle) {
    for (let i = slideshow.order.length - 1; i > 0; i--) {
      const j = Math.floor(Math.random() * (i + 1));
      [slideshow.order[i], slideshow.order[j]] = [slideshow.order[j], slideshow.order[i]];
    }
  }
  slideshow.pos = 0;

  const root = document.documentElement;
  document.getElementById('shade').style.setProperty('--dim', msg.dim ?? 0.45);
  // Zoom lasts a little longer than the photo so it never visibly stops before the crossfade.
  root.style.setProperty('--slide-duration', `${slideshow.intervalMs / 1000 + 4}s`);

  const active = slideshow.images.length > 0;
  document.body.classList.toggle('has-slides', active);
  if (!active) {
    slideEls.forEach((el) => el.classList.remove('visible'));
    return;
  }
  showNextSlide();
}

function showNextSlide() {
  clearTimeout(slideshow.timer);
  if (slideshow.images.length === 0) return;

  // Don't churn through photos while an app covers the launcher; pick up again when we're back.
  if (!document.hasFocus() && slideEls.some((el) => el.classList.contains('visible'))) {
    slideshow.timer = setTimeout(showNextSlide, 2000);
    return;
  }

  const url = slideshow.images[slideshow.order[slideshow.pos]];
  slideshow.pos = (slideshow.pos + 1) % slideshow.order.length;

  // Decode before swapping so the crossfade never shows a half-loaded photo.
  const img = new Image();
  img.src = url;
  img.decode()
    .then(() => {
      const next = slideEls[1 - slideshow.front];
      next.style.backgroundImage = `url("${url}")`;
      next.classList.remove('visible');
      void next.offsetWidth; // restart the zoom animation
      next.classList.add('visible');
      slideEls[slideshow.front].classList.remove('visible');
      slideshow.front = 1 - slideshow.front;
      slideshow.timer = setTimeout(showNextSlide, slideshow.intervalMs);
    })
    .catch(() => {
      slideshow.timer = setTimeout(showNextSlide, 500); // unreadable file: skip it
    });
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
  els.hint.textContent = `◀ ▶ choose   ·   OK open   ·   ▲ settings   ·   ${homeHelp || 'Back'} returns here`;
}

// ---------------------------------------------------------------- home

const SETTINGS_INFO = { name: 'Settings', description: 'TV display, sound output, background and apps', accent: '#353b47' };
const POWER_INFO = { name: 'Turn off', description: 'Close Jens-TV and put the PC back the way it was', accent: '#5c1f24' };

function homeItems() {
  return (state ? state.config.apps : []).map((app) => ({ kind: 'app', app }));
}

function itemInfo(item) {
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
  document.body.classList.toggle('transparent-tiles', !!(state && state.config.transparentTiles));

  els.tiles.replaceChildren(...items.map((item, i) => {
    const info = itemInfo(item);
    const wrap = el('div', 'tile-wrap');
    const tile = el('div', 'tile');
    tile.style.setProperty('--tile-bg', info.background);
    tile.append(buildIcon(info));
    if (state && state.running === info.id) tile.append(el('span', 'badge', 'Running'));
    tile.setAttribute('aria-label', info.name);
    wrap.append(tile);

    wrap.addEventListener('mouseenter', () => { homeIndex = i; topFocus = null; updateHomeFocus(); });
    wrap.addEventListener('click', () => { homeIndex = i; topFocus = null; updateHomeFocus(); activateHome(); });
    return wrap;
  }));

  updateHomeFocus();
}

function updateHomeFocus() {
  const item = homeItems()[homeIndex];
  if (!item && !topFocus) topFocus = 'settings'; // no apps configured: only the top buttons are left
  [...els.tiles.children].forEach((node, i) => node.classList.toggle('focused', !topFocus && i === homeIndex));
  els.settingsButton.classList.toggle('focused', topFocus === 'settings');
  els.powerButton.classList.toggle('focused', topFocus === 'power');

  const info = topFocus === 'settings' ? SETTINGS_INFO : topFocus === 'power' ? POWER_INFO : itemInfo(item);
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

/** Moves home focus to a top-right button ('settings' / 'power') or back to the tiles (null). */
function focusTop(target) {
  if (topFocus === target) return;
  topFocus = target;
  updateHomeFocus();
}

function quit() {
  toast('Closing Jens-TV…');
  send({ type: 'quit' });
}

function activateHome() {
  if (topFocus === 'settings') {
    openSettings();
    return;
  }
  if (topFocus === 'power') {
    quit();
    return;
  }
  const item = homeItems()[homeIndex];
  if (!item) return;
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
    transparentTiles: state.config.transparentTiles,
    turnOffOtherScreens: state.config.display.turnOffOtherScreens,
    tvWakeOnStart: !!(state.config.samsungTv && state.config.samsungTv.wakeOnStart),
    tvTurnOffOnExit: !!(state.config.samsungTv && state.config.samsungTv.turnOffOnExit),
    slideshowEnabled: state.config.slideshow.enabled,
    slideshowFolder: state.config.slideshow.folder || null,
    slideshowInterval: state.config.slideshow.intervalSeconds,
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

function intervalOptions(current) {
  const seconds = [10, 20, 30, 60, 120, 300];
  if (current && !seconds.includes(current)) seconds.push(current); // keep a custom value from config.json
  return seconds.sort((a, b) => a - b).map((s) => ({
    value: s,
    label: s < 60 ? `${s} seconds` : s === 60 ? '1 minute' : `${s / 60} minutes`,
  }));
}

function buildSettingsRows() {
  const rows = [
    {
      kind: 'select', label: 'TV display', key: 'monitorDevice',
      options: () => state.monitors.map((m) => ({ value: m.device, label: m.label })),
    },
    { kind: 'toggle', label: 'Turn off other screens while Jens-TV runs', key: 'turnOffOtherScreens' },
    ...(state.config.samsungTv ? [
      { kind: 'toggle', label: 'Wake the TV when Jens-TV starts', key: 'tvWakeOnStart' },
      { kind: 'toggle', label: 'Put the TV in standby when Jens-TV quits', key: 'tvTurnOffOnExit' },
    ] : []),
    { kind: 'select', label: 'Sound output', key: 'audioDeviceId', options: audioOptions },
    { kind: 'toggle', label: 'Use this sound output while Jens-TV runs', key: 'switchAudio' },
    { kind: 'toggle', label: 'Start with Windows', key: 'startWithWindows' },
    { kind: 'toggle', label: 'Transparent app tiles', key: 'transparentTiles', gap: true },
    { kind: 'toggle', label: 'Background slideshow', key: 'slideshowEnabled' },
    { kind: 'folder', label: 'Photo folder', key: 'slideshowFolder' },
    {
      kind: 'select', label: 'Change photo every', key: 'slideshowInterval',
      options: () => intervalOptions(draft.slideshowInterval),
    },
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
    } else if (row.kind === 'folder') {
      node.append(el('span', 'value', draft[row.key] || 'Choose… (opens a picker, use the mouse)'));
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
  } else if (row.kind === 'folder') {
    send({ type: 'pickFolder', current: draft[row.key] });
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
    if (topFocus) {
      // Top row: [settings] [power]
      if (action === 'right' && topFocus === 'settings') focusTop('power');
      else if (action === 'left' && topFocus === 'power') focusTop('settings');
      else if (action === 'down' || action === 'back' || action === 'left') focusTop(null);
      else if (action === 'ok') activateHome();
      else if (action === 'menu') openSettings();
      return;
    }
    if (action === 'left') moveHome(-1);
    else if (action === 'right') moveHome(1);
    else if (action === 'up') focusTop('settings');
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

document.getElementById('settings-icon').innerHTML = gearIcon('gear-hole-panel');
els.settingsButton.innerHTML = gearIcon('gear-hole-button');
els.settingsButton.addEventListener('mouseenter', () => focusTop('settings'));
els.settingsButton.addEventListener('click', () => { focusTop('settings'); openSettings(); });
els.powerButton.innerHTML = POWER_ICON;
els.powerButton.addEventListener('mouseenter', () => focusTop('power'));
els.powerButton.addEventListener('click', () => { focusTop('power'); quit(); });
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
      display: { turnOffOtherScreens: false },
      slideshow: { enabled: true, folder: null, intervalSeconds: 30 },
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
