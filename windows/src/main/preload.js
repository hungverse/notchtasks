'use strict';
// Bridge between the pill page and main. contextIsolation is on: the page only sees window.nt.
const fs = require('fs');
const path = require('path');
const { pathToFileURL } = require('url');
const { contextBridge, ipcRenderer, webUtils } = require('electron');
const l10n = require('../core/l10n');
const ICONS = require('../renderer/icons');
const { resourceDir } = require('../core/resources');

const iconCache = new Map();
const iconDir = path.resolve(__dirname, '../../node_modules/lucide-static/icons');
const fallback = 'circle';

// SVG markup for an SF Symbol name; drawn with currentColor by the page.
function icon(sf) {
  if (iconCache.has(sf)) return iconCache.get(sf);
  let name = ICONS[sf] || ICONS[sf.replace(/\.fill$/, '')] || fallback;
  let file = path.join(iconDir, name + '.svg');
  if (!fs.existsSync(file)) file = path.join(iconDir, fallback + '.svg');
  let svg = '';
  try { svg = fs.readFileSync(file, 'utf8'); } catch { svg = ''; }
  iconCache.set(sf, svg);
  return svg;
}

let catalogCache = null;
function catalog() {
  if (!catalogCache) {
    try { catalogCache = JSON.parse(fs.readFileSync(path.join(resourceDir('Sprites'), 'catalog.json'), 'utf8')); }
    catch { catalogCache = []; }
  }
  return catalogCache;
}

function spriteURL(id) {
  return pathToFileURL(path.join(resourceDir('Sprites'), id + '.png')).href;
}

// Dates may arrive as ms, ISO strings or Date objects; the page takes what main sent.
function onState(cb) {
  ipcRenderer.on('state', (_e, s) => {
    if (s && s.lang !== undefined && s.lang !== l10n.getChoice()) l10n.setChoice(s.lang);
    cb(s);
  });
}

// Notification sounds: main sends the file's URL and the volume; the page plays it (no sound file is read here).
ipcRenderer.on('play', (_e, url, volume) => {
  try {
    const a = new Audio(url);
    a.volume = Math.min(1, Math.max(0, Number(volume) || 0));
    a.play().catch(() => {});
  } catch { /* a sound that cannot play is no reason to fail */ }
});

contextBridge.exposeInMainWorld('nt', {
  onState,
  act: (name, ...args) => ipcRenderer.send('act', name, args),
  L: (key, ...args) => l10n.L(key, ...args),
  icon,
  spriteURL,
  catalog,
  reportRect: (rect) => ipcRenderer.send('pill-rect', rect),
  hold: (on) => ipcRenderer.send('pill-hold', !!on),
  poke: () => ipcRenderer.send('pill-poke'),
  pathForFile: (file) => webUtils.getPathForFile(file),
  startDrag: (paths) => ipcRenderer.send('shelfDrag', paths),
});
