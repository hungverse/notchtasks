'use strict';
// Standalone preview of the Control Panel and onboarding with sample state:  npx electron src/main/prefs-demo.js
// NT_SHOT=<dir> captures every page to <dir>/pane-<name>.png, two editors and the first onboarding pages, then quits.
// Settings are kept in a temp folder; nothing here touches the real %APPDATA%.
const fs = require('fs');
const os = require('os');
const path = require('path');
const { app, nativeTheme } = require('electron');
const prefs = require('./prefs-window');
const S = require('../core/settings');
const l10n = require('../core/l10n');
const watch = require('../core/watchitems');

const SHOT = process.env.NT_SHOT || '';
const PRELOAD = path.join(__dirname, 'prefs-preload.js');
const PANES = ['overview', 'modules', 'agents', 'phone', 'appearance', 'badges', 'store', 'sounds', 'behavior'];
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

app.setPath('userData', path.join(os.tmpdir(), 'ntprefs-demo'));
nativeTheme.themeSource = 'dark';

let settings = S.load(path.join(os.tmpdir(), 'ntprefs-demo-settings'));
settings = S.setMany({ onboardingDone: true, seenTools: ['npm', 'Docker', 'Xcode', 'Git'], badges: [
  { id: 'b1', symbol: 'sparkles', shape: 'circle', border: false, right: false, color: '#7C3AED', fg: '#FFFFFF' },
  { id: 'b2', symbol: 'star', shape: 'rounded', border: true, right: false, plain: true, fg: '#FFD60A' },
  { id: 'b3', text: '🔥', shape: 'circle', border: false, right: true },
  { id: 'b4', sprite: 'px-buddy', shape: 'circle', border: false, right: true, plain: true },
] });

let items = watch.presets.slice(0, 3).map((p) => ({ ...p, id: `demo-${p.id}` }));
const agents = [
  { kind: 'claude', name: 'Claude', present: true, installed: true, tested: true },
  { kind: 'codex', name: 'Codex', present: true, installed: true, tested: true,
    setup: { missing: 'Not trusted in Codex yet', button: 'Open Codex', command: 'codex' } },
  { kind: 'gemini', name: 'Gemini', present: true, installed: false, tested: false },
  { kind: 'antigravity', name: 'Antigravity', present: false, installed: false, tested: true },
  { kind: 'cursor', name: 'Cursor', present: true, installed: false, tested: true },
  { kind: 'opencode', name: 'OpenCode', present: false, installed: false, tested: true },
];
const sampleSync = (phones) => ({
  paired: phones.length > 0, code: '482913', url: 'https://notchtasks.web.app/pair#demo',
  phones, lan: true, onlyAway: true, direct: phones.length ? 1 : 0,
  proUntil: Date.now() + 30 * 86400000, problem: null, busy: false,
});
let sync = sampleSync([{ uid: 'u1', name: 'My iPhone', pro: true }]);
let choice = '';
let report = { busy: false, code: null, problem: null };

function buildState() {
  return {
    settings: S.get(), choice, device: l10n.device(), version: '2.2.0', agents, items,
    running: [{ name: 'Visual Studio Code', bundleID: 'Code.exe' }, { name: 'Spotify', bundleID: 'Spotify.exe' }],
    processNames: ['node', 'python', 'java', 'Code'], matches: null, agentError: null,
    sync, update: { available: true, version: '2.3.0', notes: 'Pairing fixes and new animated icons.', busy: false, error: null, progress: null },
    live: { working: 2, alerts: 1 }, analytics: true, report,
  };
}

function push() { prefs.pushState(buildState()); }

function onAction(name, args) {
  const [a, b] = args;
  try {
    switch (name) {
      case 'setSetting': settings = S.set(a, b); break;
      case 'setItems': items = a; break;
      case 'applyTheme': settings = S.applyTheme(a); break;
      case 'setLanguage': choice = a; l10n.setChoice(a); break;
      case 'pair': sync = { ...sync, url: 'https://notchtasks.web.app/pair#demo' }; break;
      case 'unpairAll': sync = sampleSync([]); break;
      case 'unpair': sync = { ...sync, phones: sync.phones.filter((p) => p.uid !== a) }; break;
      case 'setSyncOption': sync = { ...sync, [a]: b }; break;
      case 'sendReport': report = { busy: false, code: 'NT-2026-0425', problem: null }; break;
      case 'checkItem': break;
      case 'openOnboarding': openGuide(); break;
      case 'finishOnboarding': if (guide) guide.close(); break;
      default: console.log('demo action', name, args.length ? JSON.stringify(args).slice(0, 80) : '');
    }
  } catch (e) {
    console.error('demo', name, e.message);
  }
  push();
}

const onClosed = () => {};
let guide = null;
function openGuide(pane) {
  guide = prefs.openOnboarding({ preload: PRELOAD, onAction, onClosed: () => { guide = null; } });
  if (pane) guide.webContents.send('prefs:pane', pane);
  return guide;
}

async function capture(win, file) {
  const img = await win.webContents.capturePage();
  fs.writeFileSync(path.join(SHOT, file), img.toPNG());
  console.log('captured', path.join(SHOT, file));
}

async function shots() {
  fs.mkdirSync(SHOT, { recursive: true });
  const win = prefs.openPrefs({ pane: 'overview', preload: PRELOAD, onAction });
  win.webContents.on('console-message', (e, level, message) => { if (level >= 2) console.log('[renderer]', message); });
  await wait(1200);
  for (const p of PANES) {
    win.webContents.send('prefs:pane', p);
    await wait(500);
    await capture(win, `pane-${p}.png`);
  }
  // the phone page before any phone is paired: the QR code and the three steps
  sync = sampleSync([]);
  push();
  win.webContents.send('prefs:pane', 'phone');
  await wait(700);
  await capture(win, 'pane-phone-unpaired.png');
  sync = sampleSync([{ uid: 'u1', name: 'My iPhone', pro: true }]);
  push();
  // editors: the icon editor from a fixed icon, the watch-item editor from the list
  win.webContents.send('prefs:pane', 'badges');
  await wait(500);
  await win.webContents.executeJavaScript("document.querySelector('.bt').click()");
  await wait(700);
  await capture(win, 'editor-badge.png');
  await win.webContents.executeJavaScript("document.querySelector('.scrim') && document.querySelector('.scrim').remove()");
  win.webContents.send('prefs:pane', 'agents');
  await wait(500);
  await win.webContents.executeJavaScript("document.querySelector('.irow .btn').click()");
  await wait(700);
  await capture(win, 'editor-item.png');
  await win.webContents.executeJavaScript("document.querySelector('.scrim') && document.querySelector('.scrim').remove()");
  // onboarding: first two pages (the guide is the second window)
  const g = openGuide('guide-0');
  await wait(1200);
  await capture(g, 'onboarding-1.png');
  g.webContents.send('prefs:pane', 'guide-1');
  await wait(500);
  await capture(g, 'onboarding-2.png');
  app.exit(0);
}

app.whenReady().then(() => {
  if (SHOT) shots().catch((e) => { console.error(e); app.exit(1); });
  else prefs.openPrefs({ pane: 'overview', preload: PRELOAD, onAction });
  setTimeout(push, 300);
});
app.on('window-all-closed', () => app.quit());
