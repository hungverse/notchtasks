'use strict';
// Process table (Sources/Processes.swift). One snapshot per list() call.
// Windows: one PowerShell call over Win32_Process. macOS/Linux: one `ps`, so the code runs here for development.
const { execFile } = require('child_process');

const WIN = process.platform === 'win32';

// Win32 ConvertTo-Json output: CreationDate is "/Date(ms)/" (PowerShell 5.1) or an ISO string (pwsh 7).
const WIN_SCRIPT = '[Console]::OutputEncoding=[Text.Encoding]::UTF8; ' +
  'Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,Name,CreationDate,CommandLine,KernelModeTime,UserModeTime | ' +
  'ConvertTo-Json -Compress';

/** "C:\\x\\Node.EXE" and "/usr/bin/node" -> "node". Names are matched case-insensitively, without .exe. */
function normName(s) {
  return String(s || '').split(/[\\/]/).pop().replace(/\.exe$/i, '').toLowerCase();
}

/** Command line -> argv, honouring double quotes. */
function splitArgs(cmd) {
  const out = [];
  let cur = '', quoted = false, has = false;
  for (const ch of String(cmd || '')) {
    if (ch === '"') { quoted = !quoted; has = true; continue; }
    if (!quoted && /\s/.test(ch)) { if (has) { out.push(cur); cur = ''; has = false; } continue; }
    cur += ch; has = true;
  }
  if (has) out.push(cur);
  return out;
}

/** "/Date(1731500000000)/" or ISO text -> Date, null when unreadable. */
function parseWinDate(v) {
  if (v == null) return null;
  const m = /\/Date\((-?\d+)/.exec(String(v));
  if (m) return new Date(Number(m[1]));
  const d = new Date(v);
  return isNaN(d) ? null : d;
}

/** ps cumulative CPU time "[dd-][hh:]mm:ss[.cc]" -> ns. */
function parseCpuTime(s) {
  const m = /^(?:(\d+)-)?(.+)$/.exec(String(s).trim());
  if (!m) return 0;
  let sec = 0;
  for (const part of m[2].split(':')) sec = sec * 60 + Number(part);
  return ((Number(m[1]) || 0) * 86400 + sec) * 1e9;
}

/** Win32_Process JSON (array or single object) -> [{pid, ppid, name, start, cmdline, cpuNs}]. */
function parseWin32(text) {
  const t = String(text || '').trim();
  if (!t) return [];
  let rows = JSON.parse(t);
  if (!Array.isArray(rows)) rows = [rows];
  return rows.map((r) => ({
    pid: Number(r.ProcessId),
    ppid: Number(r.ParentProcessId),
    name: normName(r.Name),
    start: parseWinDate(r.CreationDate),
    cmdline: r.CommandLine || '',
    // CIM times are 100 ns units
    cpuNs: ((Number(r.KernelModeTime) || 0) + (Number(r.UserModeTime) || 0)) * 100,
  })).filter((p) => p.pid > 0);
}

/**
 * `ps -axo pid=,ppid=,lstart=,time=,args=` -> same shape.
 * shortcut: name is the first word of args, so an executable path with spaces gives a wrong name on macOS;
 * comm= can't be split from args on one line. Windows (the target) uses the Name field.
 */
function parsePs(text) {
  const out = [];
  for (const line of String(text || '').split(/\r?\n/)) {
    const m = /^\s*(\d+)\s+(\d+)\s+(\w{3}\s+\w{3}\s+\d+\s+[\d:]+\s+\d{4})\s+(\S+)(?:\s+(.*?))?\s*$/.exec(line);
    if (!m) continue;
    const args = m[5] || '';
    const d = new Date(m[3].replace(/\s+/g, ' '));
    out.push({
      pid: Number(m[1]),
      ppid: Number(m[2]),
      name: normName(args.split(/\s+/)[0]),
      start: isNaN(d) ? null : d,
      cmdline: args,
      cpuNs: parseCpuTime(m[4]),
    });
  }
  return out;
}

let inFlight = null;

function run() {
  return new Promise((resolve, reject) => {
    const done = (err, stdout) => (err ? reject(err) : resolve(WIN ? parseWin32(stdout) : parsePs(stdout)));
    if (WIN) {
      execFile('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', WIN_SCRIPT],
        { windowsHide: true, timeout: 15000, maxBuffer: 64 << 20 }, done);
    } else {
      execFile('ps', ['-axo', 'pid=,ppid=,lstart=,time=,args='],
        { env: { ...process.env, LC_ALL: 'C' }, timeout: 15000, maxBuffer: 64 << 20 }, done);
    }
  });
}

/** Process list. At most one call in flight: callers share the pending promise. */
function list() {
  if (!inFlight) inFlight = run().finally(() => { inFlight = null; });
  return inFlight;
}

/** pid -> [child processes] */
function childrenOf(procs) {
  const m = new Map();
  for (const p of procs) {
    if (!m.has(p.ppid)) m.set(p.ppid, []);
    m.get(p.ppid).push(p);
  }
  return m;
}

/** CPU % per pid between two samples (100 = one full core). A pid reused by another process starts at 0. */
class CpuMeter {
  constructor() { this.prev = new Map(); this.pct = new Map(); this.at = null; }

  sample(procs, now = Date.now()) {
    const dt = this.at == null ? 0 : now - this.at;
    const next = new Map(), pct = new Map();
    for (const p of procs) {
      if (p.cpuNs == null) continue;
      const start = p.start ? p.start.getTime() : 0;
      const before = this.prev.get(p.pid);
      next.set(p.pid, { cpu: p.cpuNs, start });
      if (before && before.start === start && dt > 0) {
        pct.set(p.pid, Math.max(0, (p.cpuNs - before.cpu) / (dt * 1e6) * 100));
      }
    }
    this.prev = next;
    this.pct = pct;
    this.at = now;
  }

  percent(pid) { return this.pct.get(pid) || 0; }
}

module.exports = { WIN_SCRIPT, normName, splitArgs, parseWinDate, parseCpuTime, parseWin32, parsePs, list, childrenOf, CpuMeter };
