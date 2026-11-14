'use strict';
// Now Playing on Windows through the System Media Transport Controls (media-smtc.ps1). Port of NowPlaying.swift.
// Not ported, macOS only: the AppleScript player and browser-tab paths and the Apple Events JavaScript switch.

const path = require('node:path');
const readline = require('node:readline');
const { spawn: realSpawn } = require('node:child_process');

// PowerShell cannot read a file inside app.asar: the build unpacks *.ps1 (package.json asarUnpack).
const SCRIPT = path.join(__dirname, 'media-smtc.ps1').replace('app.asar', 'app.asar.unpacked');
const COMMANDS = new Set(['toggle', 'next', 'previous']);
const MAX_BACKOFF_MS = 60000;
const HEALTHY_MS = 30000;

// One stdout line -> NowPlaying; null when nothing plays; undefined for a line that is not media output (ignored).
// The script sends artwork only when the track changes, so the previous artwork is kept for the same track.
function parseSmtcLine(line, previous = null) {
  let o;
  try {
    o = JSON.parse(line);
  } catch {
    return undefined;
  }
  if (!o || typeof o !== 'object' || Array.isArray(o)) return undefined;
  if (o.none) return null;
  const source = o.app || '';
  const title = o.title || null;
  const artist = o.artist || null;
  const album = o.album || null;
  const sameTrack = previous && previous.title === title && previous.artist === artist
    && previous.album === album && previous.source === source;
  return {
    source,
    title,
    artist,
    album,
    playing: o.playing === true,
    position: typeof o.position === 'number' ? o.position : null,
    duration: typeof o.duration === 'number' && o.duration > 0 ? o.duration : null,
    artwork: o.artBase64 ? `data:${o.artMime || 'image/jpeg'};base64,${o.artBase64}` : sameTrack ? previous.artwork : null,
  };
}

// The stdin line for a command: 'toggle' | 'next' | 'previous' | { seek: seconds }. null for anything else.
function commandLine(cmd) {
  if (COMMANDS.has(cmd)) return cmd;
  if (cmd && typeof cmd === 'object' && Number.isFinite(cmd.seek) && cmd.seek >= 0) return `seek ${cmd.seek}`;
  return null;
}

// One long-lived PowerShell child. Off Windows it does nothing and current() stays null.
function createMedia({ platform = process.platform, spawn = realSpawn, script = SCRIPT, onChange = () => {},
  log = () => {} } = {}) {
  const enabled = platform === 'win32';
  let running = false;
  let child = null;
  let current = null;
  let backoff = 1000;
  let startedAt = 0;
  let restartTimer = null;
  let slow = false;

  function setCurrent(np) {
    current = np;
    onChange(np);
  }

  function launch() {
    startedAt = Date.now();
    const proc = spawn('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', script],
      { windowsHide: true, stdio: ['pipe', 'pipe', 'ignore'] });
    child = proc;
    proc.stdin.on('error', () => {}); // the child may already be gone
    if (slow) proc.stdin.write('slow\n');
    const rl = readline.createInterface({ input: proc.stdout });
    rl.on('line', (line) => {
      if (proc !== child) return;
      const np = parseSmtcLine(line, current);
      if (np !== undefined) setCurrent(np);
    });
    const gone = () => {
      rl.close();
      if (proc !== child) return;
      child = null;
      if (current) setCurrent(null);
      if (!running) return;
      // a child that ran for a while was healthy: restart at once after that
      if (Date.now() - startedAt > HEALTHY_MS) backoff = 1000;
      restartTimer = setTimeout(launch, backoff);
      backoff = Math.min(backoff * 2, MAX_BACKOFF_MS);
    };
    proc.on('error', (e) => { log(`media: ${e.message}`); gone(); });
    proc.on('exit', gone);
  }

  function send(line) {
    if (!child || child.stdin.destroyed) return false;
    child.stdin.write(`${line}\n`);
    return true;
  }

  function start() {
    if (!enabled || running) return;
    running = true;
    launch();
  }

  function stop() {
    running = false;
    clearTimeout(restartTimer);
    const proc = child;
    child = null;
    if (proc) {
      proc.stdin.end(); // the script exits when its stdin closes
      proc.kill();
    }
    if (current) setCurrent(null);
  }

  return {
    enabled,
    start,
    stop,
    command: (cmd) => { const line = commandLine(cmd); return line ? send(line) : false; },
    setSlow: (on) => { slow = !!on; return send(slow ? 'slow' : 'fast'); },
    current: () => current,
  };
}

module.exports = { createMedia, parseSmtcLine, commandLine, SCRIPT };
