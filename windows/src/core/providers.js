'use strict'
// Usage limits and local job scanners: Sources/Providers.swift, AntigravityScanner.swift, and the Antigravity,
// Claude usage, Codex, and local model parts of the Mac app. Pure Node 20 built-ins, no electron import.
//
// Shapes: LimitWindow {label, used (0-100), resets: Date}; ProviderLimits {kind, name, bundleID, windows, updated};
// Job {id, title, detail, working}. The renderer keys app icons by name, bundleID is kept for parity with the Mac.
//
// Windows notes (uncertain, check on a real PC): Claude Code keeps its config in %USERPROFILE%\.claude (or
// CLAUDE_CONFIG_DIR); Codex in %USERPROFILE%\.codex; Antigravity's daemon folder is assumed to be
// %USERPROFILE%\.gemini\antigravity like the Mac (its app data is under %APPDATA%, not used here).

const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const { execFile, execFileSync } = require('node:child_process');
const { L } = require('./l10n');

const MIN_DATE = new Date(-8.64e15);

// ---- Local machine helpers (also used by asks.js)

const isObj = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
const str = (v) => (typeof v === 'string' ? v : undefined);
const num = (v) => (typeof v === 'number' && Number.isFinite(v) ? v : undefined);
const objArr = (v) => (Array.isArray(v) && v.every(isObj) ? v : []);

/** Last path component for both \ and / (Windows paths arrive with either). */
function lastComponent(p) {
  return String(p).replace(/[\\/]+$/, '').split(/[\\/]/).pop() ?? '';
}

/** process.kill(pid, 0) succeeds: the process exists. */
function pidAlive(pid) {
  try { process.kill(pid, 0); return true; } catch { return false; }
}

/** Gone = ESRCH only, as the Mac checks it (EPERM means it exists but is not ours). */
function pidGone(pid) {
  try { process.kill(pid, 0); return false; } catch (e) { return e.code === 'ESRCH'; }
}

function readTextOrNull(file) {
  try { return fs.readFileSync(file, 'utf8'); } catch { return null; }
}

function readJsonOrNull(file) {
  try { return JSON.parse(fs.readFileSync(file, 'utf8')); } catch { return null; }
}

/** Reads `len` bytes at `pos` from an open file; shorter if the file ends first. */
function readRange(fd, pos, len) {
  const buf = Buffer.alloc(Math.max(0, len));
  const n = fs.readSync(fd, buf, 0, buf.length, pos);
  return buf.subarray(0, n);
}

/** Write to a temp file beside the target, then rename over it (readers never see half a file). */
function writeAtomic(file, text) {
  const tmp = `${file}.${process.pid}.${Date.now()}.tmp`;
  fs.writeFileSync(tmp, text);
  fs.renameSync(tmp, file);
}

function isRunnable(file, win) {
  try {
    if (!fs.statSync(file).isFile()) return false;
    if (!win) fs.accessSync(file, fs.constants.X_OK);
    return true;
  } catch { return false; }
}

/**
 * The CLI's path, or null. Searches PATH (with .exe/.cmd/.bat/.ps1 on Windows), npm's global folder, then the usual
 * per-user folders. Test hooks: opts.env, opts.home, opts.platform.
 */
function findCli(name, { env = process.env, home = os.homedir(), platform = process.platform } = {}) {
  const win = platform === 'win32';
  const exts = win ? ['.exe', '.cmd', '.bat', '.ps1'] : [''];
  const dirs = [
    ...(env.PATH || env.Path || '').split(path.delimiter).filter(Boolean),
    ...(win && env.APPDATA ? [path.join(env.APPDATA, 'npm')] : []),
    path.join(home, '.local', 'bin'),
    path.join(home, '.npm-global', 'bin'),
    path.join(home, '.opencode', 'bin'),
    ...(win ? [] : ['/opt/homebrew/bin', '/usr/local/bin']),
  ];
  for (const d of dirs) {
    for (const e of exts) {
      const p = path.join(d, name + e);
      if (isRunnable(p, win)) return p;
    }
  }
  return null;
}

/**
 * execFile for a CLI found by findCli. On Windows an npm shim (.cmd/.bat) cannot be run directly (EINVAL), so it goes
 * through cmd.exe. Only for constant arguments: the args are joined into one command line, never user text.
 */
function execConst(file, args, opts, cb) {
  if (process.platform === 'win32' && /\.(cmd|bat)$/i.test(file)) {
    // cmd /s strips the outer quotes: the command is "<file>" <args>
    return execFile('cmd.exe', ['/d', '/s', '/c', `""${file}" ${args.join(' ')}"`], { ...opts, windowsVerbatimArguments: true }, cb);
  }
  return execFile(file, args, opts, cb);
}

/** "5 giây", "3 phút", "2 giờ": how long ago, in the app's words. */
function ago(s) {
  return s < 60 ? L('{0} sec', Math.trunc(s)) : s < 3600 ? L('{0} min', Math.trunc(s / 60)) : L('{0} hours', Math.trunc(s / 3600));
}

function parseIso(s) {
  const t = Date.parse(s);
  return Number.isNaN(t) ? null : new Date(t);
}

// ---- Usage limits

/** Shortest window first in ProviderLimits.windows; `main` is the one shown where there is room for one. */
function windowLabel(minutes) {
  return minutes < 1440 ? `${Math.floor(minutes / 60)}h` : `${Math.floor(minutes / 1440)}d`;
}

/** When to warn: 5 % steps (84 → 80), then 99 (last call) and 100 (out: you wait for the reset). Never shown. */
function limitStep(used) {
  return used >= 100 ? 100 : used >= 99 ? 99 : Math.floor(used / 5) * 5;
}

const dayKey = (d, timeZone) =>
  new Intl.DateTimeFormat('en-CA', { timeZone, year: 'numeric', month: '2-digit', day: '2-digit' }).format(d);

/**
 * When it resets, as short as it stays clear: the time today, the weekday and time within a week, else the date.
 * opts: {now, locale, timeZone} (locale and timeZone default to the system's).
 */
function resetText(w, { now = new Date(), locale, timeZone } = {}) {
  const hm = { hour: '2-digit', minute: '2-digit' };
  let o = { timeZone };
  if (dayKey(w.resets, timeZone) === dayKey(now, timeZone)) o = { ...o, ...hm };
  else if (w.resets - now < 6 * 86400e3) o = { ...o, weekday: 'short', ...hm };
  else o = { ...o, day: 'numeric', month: 'short' };
  return new Intl.DateTimeFormat(locale, o).format(w.resets);
}

// Antigravity: quota asked from its own language server (port and token in the daemon folder). Models that share a
// quota reset together, so each quota is one window named by its families.

function antigravityDaemonDir(home = os.homedir()) {
  return path.join(home, '.gemini', 'antigravity', 'daemon');
}

/** Parses a GetUserStatus reply. Nil when there are no quotas. */
function parseAntigravityLimits(text, now = new Date()) {
  const root = (() => { try { return JSON.parse(text); } catch { return null; } })();
  const configs = root?.userStatus?.cascadeModelConfigData?.clientModelConfigs;
  const quotas = [];
  for (const c of Array.isArray(configs) ? configs : []) {
    if (!isObj(c) || !isObj(c.quotaInfo)) continue;
    const q = c.quotaInfo;
    const reset = typeof q.resetTime === 'string' ? Date.parse(q.resetTime) : NaN;
    const family = str(c.label)?.split(' ').filter(Boolean)[0];
    if (Number.isNaN(reset) || !family) continue;
    // a fraction left out of the JSON is a zero: nothing remains
    const used = (1 - (num(q.remainingFraction) ?? 0)) * 100;
    const i = quotas.findIndex((x) => Math.abs(x.reset - reset) < 120e3 && Math.abs(x.used - used) < 0.5);
    if (i >= 0) {
      if (!quotas[i].families.includes(family)) quotas[i].families.push(family);
    } else {
      quotas.push({ reset, used, families: [family] });
    }
  }
  // the fullest first: it is the one shown where there is room for one
  const windows = quotas
    .slice()
    .sort((a, b) => b.used - a.used)
    .map((x) => ({ label: x.families.join('/'), used: x.reset > now.getTime() ? x.used : 0, resets: new Date(x.reset) }));
  return windows.length
    ? { kind: 'process', name: 'Antigravity', bundleID: 'com.google.antigravity', windows, updated: now }
    : null;
}

/** Asks the language server of a running Antigravity. Nil when it is not running (its limits then leave the pill). */
async function fetchAntigravityLimits({ home = os.homedir(), now = new Date(), timeoutMs = 5000 } = {}) {
  const daemon = antigravityDaemonDir(home);
  let files;
  try {
    files = fs.readdirSync(daemon).filter((f) => f.endsWith('.json')).map((f) => {
      const file = path.join(daemon, f);
      let mtime = 0;
      try { mtime = fs.statSync(file).mtimeMs; } catch { /* keep 0 */ }
      return { file, mtime };
    }).sort((a, b) => b.mtime - a.mtime);
  } catch { return null; }
  for (const { file } of files) {
    const hub = readJsonOrNull(file);
    if (!isObj(hub) || !Number.isInteger(hub.pid) || !pidAlive(hub.pid) || !Number.isInteger(hub.httpPort) || typeof hub.csrfToken !== 'string') continue;
    try {
      const res = await fetch(`http://127.0.0.1:${hub.httpPort}/exa.language_server_pb.LanguageServerService/GetUserStatus`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'Connect-Protocol-Version': '1',
          'X-Codeium-Csrf-Token': hub.csrfToken,
        },
        body: '{"metadata":{"ideName":"antigravity","extensionName":"antigravity","locale":"en"}}',
        signal: AbortSignal.timeout(timeoutMs),
      });
      const found = parseAntigravityLimits(await res.text(), now);
      if (found) return found;
    } catch { /* this daemon did not answer: try the next */ }
  }
  return null;
}

/** The tail of a file as text (the last `bytes`); '' when unreadable. */
function readTail(file, bytes = 1_000_000) {
  let fd;
  try { fd = fs.openSync(file, 'r'); } catch { return ''; }
  try {
    const size = fs.fstatSync(fd).size;
    const from = size > bytes ? size - bytes : 0;
    return readRange(fd, from, size - from).toString('utf8');
  } catch { return ''; } finally { fs.closeSync(fd); }
}

// Claude Code: the registry of sessions, and the plan limits

/** CLAUDE_CONFIG_DIR, else ~/.claude (the same on Windows: %USERPROFILE%\.claude). */
function claudeDir(home = os.homedir(), env = process.env) {
  return env.CLAUDE_CONFIG_DIR ? env.CLAUDE_CONFIG_DIR : path.join(home, '.claude');
}

/** <claudeDir>/sessions/<pid>.json, written by Claude Code itself ("busy" while a turn runs). */
function claudeSessions({ home = os.homedir(), dir = claudeDir(home) } = {}) {
  const folder = path.join(dir, 'sessions');
  let files;
  try { files = fs.readdirSync(folder); } catch { return []; }
  const out = [];
  for (const f of files) {
    if (!f.endsWith('.json')) continue;
    const j = readJsonOrNull(path.join(folder, f));
    if (!isObj(j) || !Number.isInteger(j.pid)) continue;
    out.push({
      pid: j.pid,
      sessionId: str(j.sessionId),
      cwd: str(j.cwd),
      name: str(j.name),
      status: str(j.status),
      statusUpdatedAt: num(j.statusUpdatedAt),
    });
  }
  return out;
}

/** Claude Code only exposes plan limits to the statusline; notch-statusline.sh saves that JSON in the config dir. */
function claudeLimits(now = new Date(), { home = os.homedir(), dir = claudeDir(home) } = {}) {
  const file = path.join(dir, 'notchtasks-status.json');
  const json = readJsonOrNull(file);
  const rl = isObj(json) && isObj(json.rate_limits) ? json.rate_limits : null;
  if (!rl) return null;
  const windows = [['five_hour', '5h'], ['seven_day', '7d']].flatMap(([key, label]) => {
    const w = rl[key];
    if (!isObj(w) || typeof w.used_percentage !== 'number' || typeof w.resets_at !== 'number' || w.resets_at <= now.getTime() / 1000) return [];
    return [{ label, used: w.used_percentage, resets: new Date(w.resets_at * 1000) }];
  });
  if (!windows.length) return null;
  let updated = now;
  try { updated = fs.statSync(file).mtime; } catch { /* keep now */ }
  return { kind: 'claude', name: 'Claude', bundleID: 'com.anthropic.claudefordesktop', windows, updated };
}

// `claude -p /usage` answers with the plan's real usage without sending a prompt.

function claudeUsageCli() {
  return findCli('claude');
}

const USED_RE = /(\d+(?:\.\d+)?)% used/;
const RESETS_RE = /resets (.+?) \(([^)]+)\)/;
const MONTHS = ['JAN', 'FEB', 'MAR', 'APR', 'MAY', 'JUN', 'JUL', 'AUG', 'SEP', 'OCT', 'NOV', 'DEC'];

/** Offset of `tz` from UTC at instant `ms`, in ms. */
function tzOffsetMs(ms, tz) {
  const parts = new Intl.DateTimeFormat('en-US', {
    timeZone: tz, hourCycle: 'h23', year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit', second: '2-digit',
  }).formatToParts(new Date(ms));
  const v = {};
  for (const p of parts) if (p.type !== 'literal') v[p.type] = Number(p.value);
  const asUtc = Date.UTC(v.year, v.month - 1, v.day, v.hour % 24, v.minute, v.second);
  return asUtc - Math.floor(ms / 1000) * 1000;
}

/** A wall-clock time in `tz` (IANA name; the system zone when unknown) as an instant. */
function zonedToDate(y, mo, d, h, mi, tz) {
  const guess = Date.UTC(y, mo, d, h, mi);
  let t = guess - tzOffsetMs(guess, tz);
  const off2 = tzOffsetMs(t, tz);
  if (off2 !== tzOffsetMs(guess, tz)) t = guess - off2;
  return new Date(t);
}

function validZone(name) {
  try { new Intl.DateTimeFormat('en-US', { timeZone: name }); return name; } catch { return undefined; }
}

/**
 * "Nov 3 at 8:09pm (Asia/Saigon)" → instant. Missing year = this year (in that zone), missing minutes = 0, and a
 * time more than a day in the past means next year. Nil when the text has no known shape.
 */
function parseResetWhen(when, zoneName, now) {
  const m = /^([A-Z]{3}) (\d{1,2})(?: AT (\d{1,2})(?::(\d{2}))?(AM|PM))?$/.exec(when.toUpperCase());
  const month = m ? MONTHS.indexOf(m[1]) : -1;
  if (!m || month < 0) return null;
  const tz = validZone(zoneName);
  const yearNow = Number(new Intl.DateTimeFormat('en-US', { timeZone: tz, year: 'numeric' }).format(now));
  let h = 0;
  if (m[3]) h = (Number(m[3]) % 12) + (m[5] === 'PM' ? 12 : 0);
  const mi = m[4] ? Number(m[4]) : 0;
  let d = zonedToDate(yearNow, month, Number(m[2]), h, mi, tz);
  if (d.getTime() < now.getTime() - 86400e3) d = zonedToDate(yearNow + 1, month, Number(m[2]), h, mi, tz);
  return d;
}

/** "Current session: 34% used · resets …" → 5h window; "Current week (all models)…" → 7d. */
function parseClaudeUsage(text, now = new Date()) {
  const out = [];
  for (const line of text.split('\n')) {
    const label = line.startsWith('Current session') ? '5h' : line.startsWith('Current week (all models)') ? '7d' : null;
    if (!label) continue;
    const m = USED_RE.exec(line);
    if (!m) continue;
    let resets = new Date(now.getTime() + (label === '5h' ? 5 * 3600e3 : 7 * 86400e3));
    const r = RESETS_RE.exec(line);
    if (r) {
      const when = parseResetWhen(r[1], r[2], now);
      if (when) resets = when;
    }
    out.push({ label, used: Number(m[1]), resets });
  }
  return out;
}

/** Runs `claude -p /usage` and parses it. Resolves [] when the CLI is missing or prints nothing useful. */
function runClaudeUsage(now = new Date(), cli = claudeUsageCli()) {
  if (!cli) return Promise.resolve([]);
  return new Promise((resolve) => {
    execConst(cli, ['-p', '/usage', '--no-session-persistence'], { cwd: os.tmpdir(), timeout: 60000, windowsHide: true, encoding: 'utf8' },
      (err, stdout) => resolve(parseClaudeUsage(String(stdout || ''), now)));
  });
}

// Codex: real task status and limits from the rollout files

const CODEX_MARK = '"timestamp":"';

/** The last task_started / task_complete / turn_aborted event decides the status. */
function parseCodexStatus(text) {
  const lines = text.split('\n');
  for (let i = lines.length - 1; i >= 0; i--) {
    const line = lines[i];
    if (!line.includes('"type":"event_msg"')) continue;
    const started = line.includes('"payload":{"type":"task_started"');
    if (!(started || line.includes('"payload":{"type":"task_complete"') || line.includes('"payload":{"type":"turn_aborted"'))) continue;
    const r = line.indexOf(CODEX_MARK);
    if (r < 0) return null;
    const at = parseIso(line.slice(r + CODEX_MARK.length, r + CODEX_MARK.length + 24));
    return at ? { working: started, at } : null;
  }
  return null;
}

/** Newest rate_limits event: {at, windows}. A window already reset since that event has nothing used yet. */
function parseCodexLimits(text, now = new Date()) {
  const lines = text.split('\n');
  for (let i = lines.length - 1; i >= 0; i--) {
    const line = lines[i];
    if (!line.includes('"rate_limits":{')) continue;
    const json = (() => { try { return JSON.parse(line); } catch { return null; } })();
    const rl = isObj(json?.payload) && isObj(json.payload.rate_limits) ? json.payload.rate_limits : null;
    if (!rl) continue;
    const at = (str(json.timestamp) && parseIso(json.timestamp)) || MIN_DATE;
    const windows = ['primary', 'secondary'].flatMap((key) => {
      const w = rl[key];
      if (!isObj(w) || typeof w.used_percent !== 'number' || !Number.isInteger(w.window_minutes) || typeof w.resets_at !== 'number') return [];
      return [{
        label: windowLabel(w.window_minutes),
        used: w.resets_at > now.getTime() / 1000 ? w.used_percent : 0,
        resets: new Date(w.resets_at * 1000),
      }];
    });
    return { at, windows };
  }
  return null;
}

/** Option labels: plain strings (the desktop app) or [{label}] (the terminal one). */
function optionList(v) {
  if (!Array.isArray(v)) return [];
  if (v.every((s) => typeof s === 'string')) return v;
  if (v.every(isObj)) return v.map((o) => o.label).filter((s) => typeof s === 'string');
  return [];
}

/**
 * A question Codex left for you (request_user_input). It stays open until you write back in that thread.
 * Returns {thread, callID, questions, at} for the one still open, else `open` as it was.
 */
function parseCodexQuestion(text, thread, open = null) {
  let current = open;
  for (const line of text.split('\n')) {
    if (!line.includes('"type":"response_item"')) continue;
    if (line.includes('"role":"user"')) { current = null; continue; } // you wrote back: answered
    if (!line.includes('request_user_input') || !line.includes('"type":"function_call"')) continue;
    const json = (() => { try { return JSON.parse(line); } catch { return null; } })();
    const p = isObj(json?.payload) ? json.payload : null;
    if (!p || !str(p.name)?.startsWith('request_user_input') || typeof p.arguments !== 'string') continue;
    const args = (() => { try { return JSON.parse(p.arguments); } catch { return null; } })();
    if (!isObj(args)) continue;
    // the desktop app sends {title, options: [String]}; the terminal one {question, header, options: [{label}]}
    const qs = objArr(args.questions).map((q) => ({
      question: str(q.title) ?? str(q.question) ?? '',
      header: str(q.header),
      options: optionList(q.options),
      multiSelect: typeof q.multiSelect === 'boolean' ? q.multiSelect : false,
    }));
    const at = (str(json.timestamp) && parseIso(json.timestamp)) || new Date();
    if (qs.length) current = { thread, callID: str(p.call_id) ?? crypto.randomUUID(), questions: qs, at };
  }
  return current;
}

function codexTitleAndThread(head) {
  const cwdRe = /"cwd":"((?:[^"\\]|\\.)*)"/;
  const cwd = cwdRe.exec(head);
  let title = 'Codex';
  if (cwd) {
    let value = cwd[1];
    try { value = JSON.parse(`"${cwd[1]}"`); } catch { /* keep raw */ }
    title = lastComponent(value) || 'Codex';
  }
  const sid = /"session_id":"((?:[^"\\]|\\.)*)"/.exec(head);
  return { title, thread: sid ? sid[1] : '' };
}

function walkJsonl(root) {
  const out = [];
  const visit = (dir) => {
    let entries;
    try { entries = fs.readdirSync(dir, { withFileTypes: true }); } catch { return; }
    for (const e of entries) {
      const p = path.join(dir, e.name);
      if (e.isDirectory()) visit(p);
      else if (e.name.endsWith('.jsonl')) {
        try { out.push({ path: p, mtime: fs.statSync(p).mtime }); } catch { /* vanished */ }
      }
    }
  };
  visit(root);
  return out;
}

/** Scans ~/.codex/sessions. Rollouts grow to tens of MB, so each file is read only from the last offset on. */
class CodexScanner {
  constructor({ root = path.join(os.homedir(), '.codex', 'sessions') } = {}) {
    this.root = root;
    this.cache = new Map();
  }

  /** Tasks running or finished in the last 10 min. Rollouts with no writes for 20 min are dead. */
  jobs(now = new Date()) {
    const out = [];
    for (const f of walkJsonl(this.root)) {
      if (now - f.mtime >= 3 * 3600e3) continue;
      const cur = this.cache.get(f.path);
      if (!cur || cur.mtime.getTime() !== f.mtime.getTime()) this.update(f.path, f.mtime);
      const e = this.cache.get(f.path);
      if (!(now - f.mtime < 20 * 60e3) || !e || e.child || !e.status) continue;
      const age = (now - e.status.at) / 1000;
      const id = path.basename(f.path);
      if (e.status.working) {
        out.push({ id, title: e.title, detail: L('Running · {0}', ago(age)), working: true });
      } else if (age < 600) {
        out.push({ id, title: e.title, detail: L('Done · {0} ago', ago(age)), working: false });
      }
    }
    return out;
  }

  /** Newest rate_limits event across rollouts written in the last 7 days (the newest file may have none). */
  limits(now = new Date()) {
    let best = null;
    for (const f of walkJsonl(this.root)) {
      if (now - f.mtime >= 7 * 86400e3) continue;
      if (best && !(f.mtime > best.at)) continue;
      const found = parseCodexLimits(readTail(f.path), now);
      if (!found || (best && !(found.at > best.at))) continue;
      best = found;
    }
    return best ? { kind: 'codex', name: 'Codex', bundleID: 'com.openai.codex', windows: best.windows, updated: best.at } : null;
  }

  update(file, mtime) {
    let fd;
    try { fd = fs.openSync(file, 'r'); } catch { return; }
    try {
      let entry = this.cache.get(file);
      if (!entry) {
        const head = readRange(fd, 0, 16384).toString('utf8');
        // guardian/subagent threads start and finish every few seconds while the parent works: not tasks of their own
        const { title, thread } = codexTitleAndThread(head);
        entry = { mtime, offset: 0, status: null, title, child: head.includes('"parent_thread_id"'), thread, question: null };
      }
      if (!entry.child) {
        const size = fs.fstatSync(fd).size;
        const from = entry.offset <= size ? entry.offset : 0; // file replaced: start over
        const buf = readRange(fd, from, size - from);
        const text = buf.toString('utf8');
        entry.status = parseCodexStatus(text) ?? entry.status;
        entry.question = parseCodexQuestion(text, entry.thread, entry.question);
        // resume at the last full line so an event split by a write in progress is read next time
        const nl = buf.lastIndexOf(10);
        entry.offset = from + (nl >= 0 ? nl + 1 : 0);
      }
      entry.mtime = mtime;
      this.cache.set(file, entry);
    } finally {
      fs.closeSync(fd);
    }
  }

  /** Questions still waiting on you, from threads written in the last 3 hours. */
  questions(now = new Date()) {
    const out = [];
    for (const e of this.cache.values()) {
      if (e.question && now - e.question.at < 3 * 3600e3) out.push({ question: e.question, title: e.title });
    }
    return out;
  }
}

// Antigravity: each conversation is a SQLite file; its last step says what the agent is doing right now.

/** Mid-turn: a step still in progress (pending, running, generating, waiting, queued), or a finished tool step the model
 *  has yet to answer. A turn ends on a finished reply (type 15, rarely 17); 6 and 7 appear after a stop or an error. */
function antigravityMidTurn(type, status) {
  return [1, 2, 8, 9, 11].includes(status) || (status === 3 && ![15, 17].includes(type));
}

/** The workspace folder in the conversation's protobuf metadata. Its strings carry their length, and the byte after one can be printable. */
function antigravityProject(blob) {
  const at = blob.indexOf(Buffer.from('file:///'));
  if (at <= 0) return null;
  const low = blob[at - 1];
  const high = at - 2 >= 0 ? blob[at - 2] : 0;
  const count = high >= 0x80 ? (high & 0x7f) | (low << 7) : low; // 128+ takes two varint bytes
  if (at + count > blob.length) return null;
  try {
    const last = decodeURIComponent(new URL(blob.subarray(at, at + count).toString('utf8')).pathname).split('/').filter(Boolean).pop();
    return last || null;
  } catch { return null; }
}

/**
 * Reads one conversation: {active, title} or null. Node 20 has no built-in SQLite, so this uses the sqlite3 command
 * line tool when it is on PATH (not installed by default on Windows). Without it the scanner shows no Antigravity jobs.
 * shortcut: needs sqlite3 on PATH, upgrade to a bundled SQLite binding when one is added to the app.
 */
function readAntigravityConversation(file) {
  const sqlite = findCli('sqlite3');
  if (!sqlite) return null;
  const query = (sql) => {
    const out = execFileSync(sqlite, ['-readonly', '-json', file, sql], { encoding: 'utf8', timeout: 2000, windowsHide: true });
    return out.trim() ? JSON.parse(out) : [];
  };
  try {
    const last = query('select step_type, status from steps order by idx desc limit 1')[0];
    if (!last) return null;
    const hex = query('select hex(data) as h from trajectory_metadata_blob limit 1')[0]?.h;
    const title = hex ? antigravityProject(Buffer.from(hex, 'hex')) : null;
    return { active: antigravityMidTurn(last.step_type, last.status), title: title ?? 'Antigravity' };
  } catch { return null; }
}

function antigravityStamp(db) {
  const stamps = [];
  try { stamps.push(fs.statSync(db).mtime); } catch { /* none */ }
  try {
    const wal = fs.statSync(`${db}-wal`);
    if (wal.size > 0) stamps.push(wal.mtime);
  } catch { /* no wal */ }
  return stamps.length ? new Date(Math.max(...stamps.map((d) => d.getTime()))) : null;
}

/** Conversations written in the last 10 min that are mid-turn (or were, and the reply may still be coming). */
class AntigravityScanner {
  constructor({ root = path.join(os.homedir(), '.gemini', 'antigravity', 'conversations'), readDb = readAntigravityConversation } = {}) {
    this.root = root;
    this.readDb = readDb;
    this.cache = new Map();
    this.runs = new Map();
  }

  jobs(now = new Date()) {
    let files;
    try { files = fs.readdirSync(this.root); } catch { files = []; }
    const out = [];
    const live = new Map();
    for (const name of files) {
      if (!name.endsWith('.db')) continue;
      const db = path.join(this.root, name);
      // a step left "generating" by a crash stays that way: only a conversation written lately counts
      const stamp = antigravityStamp(db);
      if (!stamp || now - stamp >= 10 * 60e3) continue;
      const cached = this.cache.get(name);
      if (cached?.stamp !== stamp.getTime()) {
        const s = this.readDb(db);
        if (s) this.cache.set(name, { stamp: stamp.getTime(), active: s.active, title: s.title });
      }
      const e = this.cache.get(name);
      if (!e) continue;
      const run = this.runs.get(name);
      // a reply that calls a tool looks finished until the tool's step is written, sometimes a minute later;
      // only a conversation already seen working is held, so opening an old one doesn't count
      const held = run ? now - Math.max(run.seen.getTime(), stamp.getTime()) < 45e3 : false;
      if (!(e.active || held)) continue;
      const since = run?.since ?? now;
      live.set(name, { since, seen: e.active ? now : run?.seen ?? now });
      out.push({ id: name, title: e.title, detail: L('Running · {0}', ago((now - since) / 1000)), working: true });
    }
    this.runs = live;
    return out;
  }
}

// Local model runners: which models LM Studio and Ollama have loaded

/** `lms ps --json`: [{displayName, identifier…}]. Talks to the app itself. Call it only when LM Studio runs. */
function lmStudioLoaded({ home = os.homedir() } = {}) {
  // shortcut: lms.exe under %USERPROFILE%\.lmstudio\bin on Windows (assumed, not checked on a PC)
  const lms = findCli('lms', { home }) ?? path.join(home, '.lmstudio', 'bin', process.platform === 'win32' ? 'lms.exe' : 'lms');
  if (!isRunnable(lms, process.platform === 'win32')) return [];
  try {
    return parseLMStudio(execFileSync(lms, ['ps', '--json'], { encoding: 'utf8', timeout: 5000, windowsHide: true, stdio: ['ignore', 'pipe', 'ignore'] }));
  } catch { return []; }
}

function parseLMStudio(text) {
  let list;
  try { list = JSON.parse(text); } catch { return []; }
  return objArr(list).map((m) => str(m.displayName) ?? str(m.identifier) ?? str(m.modelKey)).filter((s) => s !== undefined);
}

/** Ollama's /api/ps: {"models": [{"name": "qwen3:1.7b", …}]}; nothing when it isn't running. */
async function ollamaLoaded() {
  try {
    const res = await fetch('http://127.0.0.1:11434/api/ps', { signal: AbortSignal.timeout(1500) });
    return parseOllama(await res.text());
  } catch { return []; }
}

function parseOllama(text) {
  let json;
  try { json = JSON.parse(text); } catch { return []; }
  return objArr(json?.models).map((m) => str(m.name)).filter((s) => s !== undefined);
}

module.exports = {
  // local helpers
  isObj, str, lastComponent, pidAlive, pidGone, readTextOrNull, readJsonOrNull, readRange, readTail, writeAtomic,
  findCli, execConst, ago,
  // limits
  windowLabel, limitStep, resetText,
  parseAntigravityLimits, fetchAntigravityLimits, antigravityDaemonDir,
  claudeDir, claudeSessions, claudeLimits, claudeUsageCli, parseClaudeUsage, runClaudeUsage,
  parseCodexStatus, parseCodexLimits, parseCodexQuestion, CodexScanner,
  AntigravityScanner, antigravityMidTurn, antigravityProject, readAntigravityConversation,
  lmStudioLoaded, parseLMStudio, ollamaLoaded, parseOllama,
};
