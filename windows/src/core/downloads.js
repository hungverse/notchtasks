'use strict';
// Port of Sensors.swift Downloads. Chrome and Edge write "name.crdownload" (Edge also ".partial"), Firefox
// "name.part"; the browser renames the file when it is done. Hidden (dot) files are never reported.

const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const PARTIAL = new Set(['crdownload', 'download', 'part', 'partial', 'tmp', 'opdownload', 'aria2']);
const RECENT_MS = 30 * 60 * 1000;

const ext = (name) => path.extname(name).slice(1).toLowerCase();
const visible = (name) => !name.startsWith('.');

function finishedNames(names) {
  return names.filter((n) => visible(n) && !PARTIAL.has(ext(n))).sort();
}

function inProgressNames(names) {
  return names.filter((n) => visible(n) && PARTIAL.has(ext(n))).sort();
}

// fs.watch wakes the scan; a debounce keeps a burst of writes to one scan. onChange({finished, inProgress, added})
// fires only when the lists change. added = finished files new since the last scan, made in the last 30 minutes.
function createDownloads({ folder = path.join(os.homedir(), 'Downloads'), onChange = () => {}, debounceMs = 400,
  recentMs = RECENT_MS, now = Date.now } = {}) {
  let seen = null; // finished names at the previous scan; null until the baseline scan
  let lastKey = '';
  let watcher = null;
  let timer = null;

  function isRecent(name) {
    try {
      const st = fs.statSync(path.join(folder, name));
      return now() - (st.birthtimeMs || st.mtimeMs) < recentMs;
    } catch {
      return false;
    }
  }

  function scan() {
    let names;
    try {
      names = fs.readdirSync(folder);
    } catch {
      return;
    }
    const finished = finishedNames(names);
    const inProgress = inProgressNames(names);
    const key = JSON.stringify([finished, inProgress]);
    if (key === lastKey) return;
    lastKey = key;
    const before = seen;
    seen = new Set(finished);
    const added = before ? finished.filter((n) => !before.has(n) && isRecent(n)) : [];
    onChange({ finished, inProgress, added });
  }

  function stop() {
    clearTimeout(timer);
    timer = null;
    if (watcher) watcher.close();
    watcher = null;
  }

  function start() {
    if (watcher) return true;
    try {
      watcher = fs.watch(folder, { persistent: false }, () => {
        clearTimeout(timer);
        timer = setTimeout(scan, debounceMs);
      });
    } catch {
      watcher = null;
      return false;
    }
    watcher.on('error', stop);
    scan();
    return true;
  }

  return { start, stop, scan };
}

module.exports = { createDownloads, finishedNames, inProgressNames, PARTIAL };
