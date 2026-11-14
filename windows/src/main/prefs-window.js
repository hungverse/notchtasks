'use strict';
// The Control Panel (Sources/Preferences.swift) and onboarding (Sources/Onboarding.swift) as framed Windows 11 windows.
// One of each at a time: opening again focuses the open one and switches its pane. Renderer code: src/renderer/prefs.*,
// onboarding.*. The state goes out with pushState(); the renderer's actions come back through onAction(name, args).
const path = require('path');
const { app, BrowserWindow, ipcMain, nativeTheme } = require('electron');

const RENDER_DIR = path.join(__dirname, '..', 'renderer');
const views = { prefs: null, onboarding: null };  // kind → { win, onAction, pane }
let lastState = null;
let wired = false;

function wire() {
  if (wired) return;
  wired = true;
  // where the renderer copies pictures and sounds the user picks (every pick makes its own copy)
  ipcMain.on('prefs:dirs', (event) => {
    event.returnValue = { badges: path.join(app.getPath('userData'), 'Badges'), sounds: path.join(app.getPath('userData'), 'Sounds') };
  });
  ipcMain.on('prefs:act', (event, name, args) => {
    for (const v of Object.values(views)) {
      if (!v || v.win.isDestroyed() || v.win.webContents !== event.sender) continue;
      try { v.onAction(name, Array.isArray(args) ? args : []); } catch (e) { console.error('prefs action', name, e); }
    }
  });
}

function open(kind, o) {
  wire();
  nativeTheme.themeSource = 'dark';  // the windows are dark whatever the system says; mica follows the theme
  const existing = views[kind];
  if (existing && !existing.win.isDestroyed()) {
    existing.onAction = o.onAction;
    if (existing.win.isMinimized()) existing.win.restore();
    existing.win.show();
    existing.win.focus();
    if (o.pane) existing.win.webContents.send('prefs:pane', o.pane);
    return existing.win;
  }
  const win = new BrowserWindow({
    width: o.width, height: o.height, minWidth: o.minWidth, minHeight: o.minHeight, resizable: o.resizable,
    title: 'NotchTasks', show: false, autoHideMenuBar: true, backgroundColor: '#202020',
    ...(process.platform === 'win32' ? { backgroundMaterial: 'mica' } : {}),
    webPreferences: { preload: o.preload, contextIsolation: true, sandbox: false, nodeIntegration: false, spellcheck: false },
  });
  const entry = { win, onAction: o.onAction, pane: o.pane };
  views[kind] = entry;
  win.once('ready-to-show', () => win.show());
  win.webContents.on('did-finish-load', () => {
    if (lastState) win.webContents.send('prefs:state', lastState);
    if (entry.pane) win.webContents.send('prefs:pane', entry.pane);
  });
  win.on('closed', () => {
    if (views[kind] === entry) views[kind] = null;
    if (o.onClosed) o.onClosed();
  });
  win.loadFile(path.join(RENDER_DIR, o.file));
  return win;
}

/** The Control Panel. `pane` is a page name from prefs.js (overview, modules, agents, phone, appearance, badges, store, sounds, behavior). */
function openPrefs({ pane, preload, onAction, onClosed }) {
  return open('prefs', { file: 'prefs.html', width: 820, height: 620, minWidth: 720, minHeight: 500, resizable: true,
    pane, preload, onAction, onClosed });
}

/** The first-run guide. It ends with an action ('finishOnboarding', 'close' | 'phone' | 'notch'). */
function openOnboarding({ preload, onAction, onClosed }) {
  return open('onboarding', { file: 'onboarding.html', width: 740, height: 640, minWidth: 740, minHeight: 640, resizable: false,
    preload, onAction, onClosed });
}

/** Sends the whole state to every open window, and to any opened later. */
function pushState(state) {
  lastState = state;
  for (const v of Object.values(views)) {
    if (v && !v.win.isDestroyed()) v.win.webContents.send('prefs:state', state);
  }
}

function isOpen(kind) { return !!views[kind] && !views[kind].win.isDestroyed(); }

function close(kind) { if (isOpen(kind)) views[kind].win.close(); }

module.exports = { openPrefs, openOnboarding, pushState, isOpen, close };
