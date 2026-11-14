'use strict'
// Answering coding agents from the pill (Sources/AgentAsks.swift). `NotchTasks.exe --hook <agent>` runs from each
// agent's hook system whenever it is about to ask. It writes the ask to Asks/<id>.json and waits for Asks/<id>.reply.
// The app watches the folder and writes the reply. No reply (app closed, or the wait runs out) = the agent asks as usual.
//
// Skipped, macOS-only: focusHost (brings the host window forward through NSRunningApplication and the process tree,
// no Windows equivalent here), the distributed notification the hook posts (the app watches the folder instead), and
// the parent-process folder fallback for the session name (the hook falls back to the agent's name).
//
// Windows adaptations: the Asks folder is %APPDATA%\NotchTasks\Asks (~/.notchtasks/Asks when APPDATA is unset, so
// tests run anywhere). The app writes <NotchTasks folder>\app.pid while it runs; the hook reads it as "app running".
// Replies and asks are written as JSON with ISO dates and {kind, answers} replies (only our own code reads them).

const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const { execFile } = require('node:child_process');
const { L } = require('./l10n');
const P = require('./providers');

const WAIT = 590;                      // seconds an agent waits for a hook; the hook's timeout is WAIT + 10
const POLL_MS = 250;
const HOOK_NAME = 'notchtasks';        // Antigravity keys hooks by name
const DENIED = 'The user denied this from the NotchTasks notch.';
const REPLY_KINDS = ['allow', 'deny', 'pass', 'answers'];
const PASS = { kind: 'pass' };

const { isObj, str } = P;
const arr = (v) => (Array.isArray(v) ? v : []);

/** Per agent: display name, CLI, the hook event, the tool matcher, and where its settings live. */
const AGENTS = {
  claude: {
    name: 'Claude', cli: 'claude', event: 'PermissionRequest', matcher: '*', tested: true,
    settingsFile: (home) => path.join(P.claudeDir(home), 'settings.json'),
  },
  codex: {
    name: 'Codex', cli: 'codex', event: 'PermissionRequest', matcher: '*', tested: true,
    settingsFile: (home) => path.join(home, '.codex', 'hooks.json'),
  },
  gemini: {
    name: 'Gemini', cli: 'gemini', event: 'BeforeTool', matcher: 'run_shell_command|write_file|replace|web_fetch|ask_user', tested: false,
    settingsFile: (home) => path.join(home, '.gemini', 'settings.json'),
  },
  antigravity: {
    name: 'Antigravity', cli: 'agy', event: 'PreToolUse',
    matcher: 'run_command|write_to_file|replace_file_content|multi_replace_file_content|ask_question', tested: true,
    settingsFile: (home) => path.join(home, '.gemini', 'config', 'hooks.json'),
  },
  cursor: {
    name: 'Cursor', cli: 'cursor-agent', event: 'beforeShellExecution', matcher: '*', tested: true,
    settingsFile: (home) => path.join(home, '.cursor', 'hooks.json'),
  },
  opencode: {
    // OpenCode has no hook config: a small plugin of ours asks the pill
    name: 'OpenCode', cli: 'opencode', event: 'permission.ask', matcher: '*', tested: true,
    settingsFile: (home) => path.join(home, '.config', 'opencode', 'plugins', 'notchtasks.js'),
  },
};
const AGENT_KINDS = Object.keys(AGENTS);

/** Installed here: the CLI is on one of the usual paths. Claude is always present (the desktop app reads ~/.claude too). */
function present(kind, { home = os.homedir() } = {}) {
  if (kind === 'claude') return true;
  return P.findCli(AGENTS[kind].cli, { home }) !== null;
}

// ---- Asks folder

/** %APPDATA%\NotchTasks\Asks, else ~/.notchtasks/Asks. */
function askDir({ env = process.env, home = os.homedir() } = {}) {
  return env.APPDATA ? path.join(env.APPDATA, 'NotchTasks', 'Asks') : path.join(home, '.notchtasks', 'Asks');
}

const askFile = (dir, id) => path.join(dir, `${id}.json`);
const replyFile = (dir, id) => path.join(dir, `${id}.reply`);
const appPidFile = (dir) => path.join(path.dirname(dir), 'app.pid');

/** The app writes this while it runs, so hooks know to wait for it. Call it at start-up. */
function markAppRunning(dir = askDir(), pid = process.pid) {
  fs.mkdirSync(path.dirname(dir), { recursive: true });
  P.writeAtomic(appPidFile(dir), String(pid));
}

/** The app is running = its pid file names a live process other than this one. */
function appRunning(dir = askDir()) {
  const text = P.readTextOrNull(appPidFile(dir));
  const pid = text === null ? NaN : Number(text);
  return Number.isInteger(pid) && pid > 0 && pid !== process.pid && P.pidAlive(pid);
}

/** What the agent reads when its question was answered on the pill instead of in its own dialog. */
function answered(a = {}) {
  return 'The user already answered this on the NotchTasks notch, so the dialog was not shown. Their answers: '
    + Object.entries(a).map(([k, v]) => `"${k}" → "${v}"`).sort().join('; ')
    + '. Continue with these answers; do not ask again.';
}

/** What an ask's tool does, in words a non-developer reads. */
const RUN_TOOLS = ['Bash', 'run_shell_command', 'run_command', 'shell', 'exec_command', 'local_shell', 'Execute', 'bash'];
const EDIT_TOOLS = ['Edit', 'Write', 'MultiEdit', 'NotebookEdit', 'write_file', 'replace', 'write_to_file', 'replace_file_content',
  'multi_replace_file_content', 'apply_patch', 'Create', 'ApplyPatch', 'edit', 'write'];
const READ_TOOLS = ['Read', 'read_file', 'view_file'];
const WEB_TOOLS = ['WebFetch', 'WebSearch', 'web_fetch', 'FetchUrl', 'webfetch'];

function actionLabel(tool) {
  if (RUN_TOOLS.includes(tool)) return L('Chạy lệnh');
  if (EDIT_TOOLS.includes(tool)) return L('Sửa file');
  if (READ_TOOLS.includes(tool)) return L('Đọc file');
  if (WEB_TOOLS.includes(tool)) return L('Truy cập web');
  if (tool.startsWith('mcp__')) return L('Dùng công cụ ') + (tool.split('_').filter(Boolean).pop() ?? '');
  return tool;
}

// ---- The hook side

/** Shape of an ask: {id, pid, session, tool, summary, questions:[{question, header, options, multiSelect}], at, agent}. */
function askFrom(input, kind, { id, pid, session, now = new Date() }) {
  // Antigravity speaks camelCase: {"toolCall": {"name", "args"}}; the others {"tool_name", "tool_input"}
  // Cursor sends the shell command itself at the top level
  const call = isObj(input.toolCall) ? input.toolCall : undefined;
  const tool = str(input.tool_name) ?? str(call?.name)
    ?? (kind === 'cursor' ? 'shell' : kind === 'opencode' ? (str(input.action) ?? str(input.type) ?? '?') : '?');
  let ti = isObj(input.tool_input) ? input.tool_input : isObj(call?.args) ? call.args : (kind === 'cursor' ? input : {});
  if (kind === 'opencode') {
    // OpenCode sends its permission evaluation: {action: "bash"|"edit"|"webfetch", resources, metadata: {command…}}
    ti = { ...(isObj(input.metadata) ? input.metadata : {}) };
    if (ti.command === undefined) {
      const p = (Array.isArray(input.resources) && input.resources.every((s) => typeof s === 'string')
        ? input.resources.join(' ') : undefined) ?? str(input.pattern) ?? str(input.title);
      if (p !== undefined) ti.description = p;
    }
  }
  // options come as [{label}] (Claude, Gemini) or plain strings (Antigravity, Codex app)
  const questions = arr(ti.questions).every(isObj) ? arr(ti.questions) : [];
  const qs = questions.map((q) => ({
    question: str(q.question) ?? str(q.title) ?? '',
    header: str(q.header),
    options: optionList(q.options),
    multiSelect: typeof q.multiSelect === 'boolean' ? q.multiSelect : typeof q.is_multi_select === 'boolean' ? q.is_multi_select : false,
  }));
  const keys = ['command', 'CommandLine', 'file_path', 'TargetFile', 'AbsolutePath', 'url', 'Url', 'path', 'pattern', 'query', 'description'];
  let field;
  for (const k of keys) {
    const v = ti[k];
    if (typeof v === 'string') { field = v; break; }
    if (Array.isArray(v) && v.every((s) => typeof s === 'string')) { field = v.join(' '); break; } // Codex: ["bash", "-lc", "…"]
  }
  let compact;
  try { compact = JSON.stringify(ti); } catch { compact = undefined; }
  const summary = qs.length ? qs[0].question : (field ?? compact ?? '');
  return { id, pid, session, tool, summary: Array.from(summary).slice(0, 600).join(''), questions: qs, at: now, agent: kind };
}

function optionList(v) {
  if (!Array.isArray(v)) return [];
  if (v.every((s) => typeof s === 'string')) return v;
  if (v.every(isObj)) return v.map((o) => o.label).filter((s) => typeof s === 'string');
  return [];
}

/** The hook's stdout for a reply, or null = print nothing, so the agent asks as usual. */
function output(reply, input, kind) {
  const r = reply?.kind ?? 'pass';
  const ans = reply?.answers;
  let out;
  switch (kind) {
    case 'claude':
    case 'codex': {
      if (r === 'pass') return null;
      let decision;
      if (r === 'allow') decision = { behavior: 'allow' };
      else if (r === 'deny') decision = { behavior: 'deny', message: DENIED };
      else decision = { behavior: 'allow', updatedInput: { ...(isObj(input.tool_input) ? input.tool_input : {}), answers: ans } };
      out = { hookSpecificOutput: { hookEventName: 'PermissionRequest', decision } };
      break;
    }
    case 'gemini':
    case 'antigravity': {
      if (r === 'answers') {
        // their question tools take no answer from a hook: block the dialog and hand the answer over in its place
        out = { decision: 'deny', reason: answered(ans) };
      } else if (r === 'allow') {
        // Antigravity's own permission check still runs after an allow: grant this one call
        const args = isObj(input.toolCall?.args) ? input.toolCall.args : {};
        const grant = str(args.CommandLine) !== undefined ? `command(${args.CommandLine})`
          : str(args.TargetFile) !== undefined ? `write_file(${args.TargetFile})` : null;
        out = kind === 'antigravity' && grant ? { decision: 'allow', permissionOverrides: [grant] } : { decision: 'allow' };
      } else if (r === 'deny') {
        out = { decision: 'deny', reason: DENIED };
      } else {
        // Gemini: no output = carry on and ask as usual; Antigravity needs to be told
        if (kind === 'gemini') return null;
        out = { decision: 'ask' };
      }
      break;
    }
    case 'cursor':
      if (r === 'allow' || r === 'answers') out = { permission: 'allow' };
      else if (r === 'deny') out = { permission: 'deny', user_message: L('Đã từ chối trên notch'), agent_message: DENIED };
      else out = { permission: 'ask' };
      break;
    case 'opencode':
      out = { status: r === 'allow' || r === 'answers' ? 'allow' : r === 'deny' ? 'deny' : 'ask' };
      break;
    default:
      return null;
  }
  return JSON.stringify(out);
}

/** The last turn's settings in a Codex session file. Quotes inside recorded output are escaped, so only real entries match. */
function codexTurn(transcript) {
  let d;
  try { d = fs.readFileSync(transcript); } catch { return null; }
  const mark = Buffer.from('"type":"turn_context"');
  const hit = d.lastIndexOf(mark);
  if (hit < 0) return null;
  const start = d.subarray(0, hit).lastIndexOf(10) + 1;
  const after = hit + mark.length;
  const nl = d.subarray(after).indexOf(10);
  const end = nl < 0 ? d.length : after + nl;
  try {
    const p = JSON.parse(d.subarray(start, end).toString('utf8'))?.payload;
    return isObj(p) ? p : null;
  } catch { return null; }
}

/** How the agent is set to approve: the hook runs ahead of it, and answering here would replace its own approval. */
function approvalMode(kind, input, { home = os.homedir(), antigravityConfig } = {}) {
  if (kind === 'codex') {
    const m = str(input.permission_mode);
    if (m !== undefined && m !== 'default') return m;
    const t = str(input.transcript_path);
    return (t ? str(codexTurn(t)?.approvals_reviewer) : undefined) ?? null;
  }
  if (kind === 'antigravity') {
    const root = P.readJsonOrNull(antigravityConfig ?? path.join(home, '.gemini', 'config', 'config.json'));
    return str(root?.userSettings?.permissionPreset) ?? null;
  }
  return null;
}

const SELF_MODES = ['auto_review', 'bypassPermissions', 'dontAsk', 'AGENT_PERMISSION_PRESET_TURBO'];
const QUESTION_TOOLS = ['request_user_input', 'ask_question'];

/** Commands and edits are the agent's to approve in these modes; a question still needs you. */
function selfApproved(mode, tool) {
  return SELF_MODES.includes(mode ?? '') && !QUESTION_TOOLS.includes(tool);
}

/** The end of the agent's session log (JSON lines), at most the last 256 KB. */
function tail(file) {
  let fd;
  try { fd = fs.openSync(file, 'r'); } catch { return []; }
  try {
    const size = fs.fstatSync(fd).size;
    const from = size > 262144 ? size - 262144 : 0;
    const text = P.readRange(fd, from, size - from).toString('utf8');
    const out = [];
    for (const line of text.split('\n')) {
      if (!line) continue;
      try { const j = JSON.parse(line); if (isObj(j)) out.push(j); } catch { /* partial first line */ }
    }
    return out;
  } catch { return []; } finally { fs.closeSync(fd); }
}

const sameInput = (a, b) => {
  const canon = (v) => JSON.stringify(v, (k, x) => (isObj(x) ? Object.fromEntries(Object.keys(x).sort().map((y) => [y, x[y]])) : x));
  return canon(a) === canon(b);
};

/** The id of the tool call this ask is about: the newest call of that tool with that input. Nil: not found (yet). */
function pendingCall(kind, tool, input, lines) {
  if (kind === 'claude') {
    const uses = lines.flatMap((l) => arr(isObj(l.message) ? l.message.content : undefined))
      .filter((c) => isObj(c) && c.type === 'tool_use' && c.name === tool);
    const same = uses.filter((u) => (input ? sameInput(isObj(u.input) ? u.input : {}, input) : true));
    const hit = same[same.length - 1] ?? (input === undefined ? uses[uses.length - 1] : undefined);
    return str(hit?.id) ?? null;
  }
  if (kind === 'codex') {
    const calls = lines.map((l) => l.payload).filter((p) => isObj(p) && p.type === 'function_call');
    return str(calls[calls.length - 1]?.call_id) ?? null;
  }
  // Gemini, Antigravity rewrite their whole session file, Cursor and OpenCode send none: only the agent quitting
  // ends their wait early
  return null;
}

/** That call has its result in the log: it ran (allowed in the agent) or was refused there, so the ask is settled. */
function settled(kind, call, lines) {
  if (kind === 'claude') {
    return lines.some((l) => arr(isObj(l.message) ? l.message.content : undefined)
      .some((c) => isObj(c) && c.type === 'tool_result' && c.tool_use_id === call));
  }
  if (kind === 'codex') {
    return lines.some((l) => isObj(l.payload) && l.payload.type === 'function_call_output' && l.payload.call_id === call);
  }
  return false;
}

const emit = (o) => (o == null ? '' : `${o}\n`);
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

function readReply(dir, id) {
  const j = P.readJsonOrNull(replyFile(dir, id));
  return isObj(j) && REPLY_KINDS.includes(j.kind) ? j : null;
}

/**
 * What `NotchTasks.exe --hook <agent>` does with the agent's stdin. Resolves {stdout, exitCode} (stdout ends with a
 * newline when there is output). Options (tests inject the rest): dir, home, pid, ppid, now (ms), wait (seconds),
 * appRunning (fn), antigravityConfig.
 */
async function runHook(kind, stdinText, opts = {}) {
  const k = AGENTS[kind] ? kind : 'claude';
  const spec = AGENTS[k];
  const dir = opts.dir ?? askDir();
  const home = opts.home ?? os.homedir();
  const pid = opts.pid ?? process.pid;
  const agent = opts.ppid ?? process.ppid;
  const now = opts.now ?? (() => Date.now());
  const wait = opts.wait ?? WAIT;
  const running = opts.appRunning ?? (() => appRunning(dir));
  const done = (input, reply) => ({ stdout: emit(output(reply ?? PASS, input, k)), exitCode: 0 });

  let input = null;
  try { input = JSON.parse(stdinText); } catch { input = null; }
  if (!isObj(input)) return done({}, null);
  if (typeof input.hook_event_name === 'string' && input.hook_event_name !== spec.event) return done(input, null);

  const tool = str(input.tool_name) ?? str(input.toolCall?.name) ?? '';
  if (selfApproved(approvalMode(k, input, { home, antigravityConfig: opts.antigravityConfig }), tool)) {
    // no objection: Antigravity's own permission check still runs, and "ask" would force its dialog
    return { stdout: k === 'antigravity' ? '{"decision":"allow"}\n' : '', exitCode: 0 };
  }
  if (!running()) return done(input, null);

  const sid = str(input.session_id);
  const s = k === 'claude' && sid ? P.claudeSessions({ home }).find((x) => x.sessionId === sid) : undefined;
  // Cursor may send an empty cwd: then the folder comes from the workspace list
  const rawCwd = str(input.cwd) ?? (Array.isArray(input.workspacePaths) ? str(input.workspacePaths[0]) : undefined);
  const cwd = rawCwd ? (P.lastComponent(rawCwd) || undefined) : undefined;
  const id = crypto.randomUUID();
  const a = askFrom(input, k, { id, pid, session: s?.name ?? cwd ?? spec.name, now: new Date(now()) });
  try {
    fs.mkdirSync(dir, { recursive: true });
    P.writeAtomic(askFile(dir, id), JSON.stringify({ ...a, at: a.at.toISOString() }));
  } catch {
    return done(input, null);
  }

  let reply = null;
  const end = now() + wait * 1000;
  const transcript = str(input.transcript_path);
  const toolInput = isObj(input.tool_input) ? input.tool_input : undefined;
  let call;  // the id the agent gave this tool call in its log, once found there
  let checks = 0;
  while (!reply && now() < end) {
    await sleep(POLL_MS);
    reply = readReply(dir, id);
    checks += 1;
    if (checks % 10 === 0 && !running()) break;  // the app quit: let the agent ask itself
    if (checks % 5 === 0) {
      if (!P.pidAlive(agent)) break;  // the agent is gone (quit, or the ask was cancelled with it)
      // answered in the agent itself: it moves on without stopping this hook, so watch for that and leave at once
      if (transcript) {
        const lines = tail(transcript);
        call = call ?? pendingCall(k, tool, toolInput, lines);
        if (call && settled(k, call, lines)) break;
      }
    }
  }
  fs.rmSync(askFile(dir, id), { force: true });
  fs.rmSync(replyFile(dir, id), { force: true });
  return done(input, reply);
}

// ---- The app side

/** Asks still waiting, oldest first. Leftovers of hooks that died, or older than the wait, are cleared. */
function pending(dir = askDir(), { now = Date.now(), wait = WAIT } = {}) {
  let names;
  try { names = fs.readdirSync(dir); } catch { return []; }
  const out = [];
  for (const n of names) {
    if (!n.endsWith('.json')) continue;
    const j = P.readJsonOrNull(path.join(dir, n));
    const at = isObj(j) ? new Date(j.at) : null;
    if (!isObj(j) || typeof j.id !== 'string' || !Number.isInteger(j.pid) || !at || Number.isNaN(at.getTime())) continue;
    const a = { ...j, at, questions: arr(j.questions) };
    if (P.pidGone(a.pid) || now - at.getTime() > wait * 1000) {
      fs.rmSync(askFile(dir, a.id), { force: true });
      fs.rmSync(replyFile(dir, a.id), { force: true });
      continue;
    }
    if (fs.existsSync(replyFile(dir, a.id))) continue;  // answered, the hook hasn't picked it up yet
    out.push(a);
  }
  return out.sort((x, y) => x.at - y.at);
}

/**
 * Watches the Asks folder (fs.watch, plus a scan every 2 s in case a watch event is missed). onChange(asks) runs when
 * the list changes. Returns {scan, close}.
 */
function watchAsks(dir, onChange, { wait = WAIT, intervalMs = 2000 } = {}) {
  fs.mkdirSync(dir, { recursive: true });
  let last = null;
  let timer = null;
  let closed = false;
  const scan = () => {
    if (closed) return;
    const list = pending(dir, { wait });
    const key = JSON.stringify(list);
    if (key !== last) { last = key; onChange(list); }
  };
  let watcher = null;
  try {
    watcher = fs.watch(dir, () => { clearTimeout(timer); timer = setTimeout(scan, 50); });
  } catch { /* no watch here: the periodic scan still runs */ }
  const iv = setInterval(scan, intervalMs);
  scan();
  return {
    scan,
    close() {
      closed = true;
      clearInterval(iv);
      clearTimeout(timer);
      if (watcher) watcher.close();
    },
  };
}

/** Writes the answer for an ask: {kind:'allow'|'deny'|'pass'} or {kind:'answers', answers:{question: label}}. */
function reply(id, answer, dir = askDir()) {
  fs.mkdirSync(dir, { recursive: true });
  P.writeAtomic(replyFile(dir, id), JSON.stringify(answer));
}

// ---- Installing the hook in each agent's config

/** OpenCode runs plugins in its own process: this one hands each permission prompt to `<exe> --hook opencode`
 *  without blocking OpenCode, and keeps OpenCode's own prompt when the pill has no answer. */
function openCodePlugin(exePath) {
  return `// Written by NotchTasks: answers OpenCode's permission prompts from the notch. Turn it off in NotchTasks.
import { spawn } from "node:child_process"

const ask = (request) => new Promise((resolve) => {
  const p = spawn(${JSON.stringify(exePath)}, ["--hook", "opencode"], { stdio: ["pipe", "pipe", "ignore"] })
  let out = ""
  p.stdout.on("data", (d) => (out += d))
  p.on("close", () => resolve(out))
  p.on("error", () => resolve(""))
  p.stdin.end(JSON.stringify(request))
})

export default {
  id: "notchtasks",
  async setup(ctx) {
    await ctx.permission.hook("evaluate", async (event) => {
      if (event.effect !== "ask") return  // already allowed by your rules: nothing to ask
      const reply = await ask({ action: event.action, resources: event.resources, metadata: event.metadata, sessionID: event.sessionID, agent: event.agent })
      try {
        const status = JSON.parse(reply).status
        if (status === "allow" || status === "deny") event.effect = status
        if (status === "deny") event.message = "The user denied this from the NotchTasks notch."
      } catch {}
    })
  },
}
`;
}

/** The hook command. Quoted, since the install path on Windows can hold spaces. */
function command(kind, exePath) {
  return `"${exePath}" --hook ${kind}`;
}

/** A matcher group holding our command, or (Cursor's flat list) our command itself. */
function isOurs(group) {
  return [...arr(isObj(group) ? group.hooks : undefined), group].some((h) => isObj(h) && typeof h.command === 'string'
    && h.command.includes('NotchTasks') && h.command.includes('--hook'));
}

function settingsOf(kind, opts = {}) {
  return opts.file ?? AGENTS[kind].settingsFile(opts.home ?? os.homedir());
}

function isInstalled(kind, opts = {}) {
  const file = settingsOf(kind, opts);
  if (kind === 'opencode') return (P.readTextOrNull(file) ?? '').includes('--hook');
  const root = P.readJsonOrNull(file);
  if (!isObj(root)) return false;
  if (kind === 'antigravity') return root[HOOK_NAME] !== undefined && root[HOOK_NAME] !== null;
  return arr(isObj(root.hooks) ? root.hooks[AGENTS[kind].event] : undefined).some(isOurs);
}

/** Adds or removes only our hook; everything else in the file is kept. The old file is saved beside it first. */
function setInstalled(on, kind, opts = {}) {
  const url = settingsOf(kind, opts);
  const spec = AGENTS[kind];
  const backup = () => {
    fs.rmSync(`${url}.notchtasks-backup`, { force: true });
    fs.copyFileSync(url, `${url}.notchtasks-backup`);
  };
  if (kind === 'opencode') {
    if (on) {
      fs.mkdirSync(path.dirname(url), { recursive: true });
      if (fs.existsSync(url)) backup();
      P.writeAtomic(url, openCodePlugin(opts.exePath));
    } else if (isInstalled('opencode', { file: url })) {
      fs.rmSync(url);  // only ever our own file
    }
    return;
  }
  let root = {};
  const raw = P.readTextOrNull(url);
  if (raw !== null && raw.length > 0) {
    // a file we can't read is never overwritten
    const parsed = JSON.parse(raw);
    if (!isObj(parsed)) throw new Error('NotchTasks: the agent settings file is not a JSON object');
    root = parsed;
    backup();
  }
  const wait = opts.wait ?? WAIT;
  // Gemini counts the timeout in milliseconds, the rest in seconds
  const timeout = kind === 'gemini' ? (wait + 10) * 1000 : wait + 10;
  const handler = { type: 'command', command: command(kind, opts.exePath), timeout };
  if (kind === 'gemini') handler.name = HOOK_NAME;  // Gemini lists hooks by name; others may reject unknown keys
  const group = { matcher: spec.matcher, hooks: [handler] };
  if (kind === 'antigravity') {
    if (on) root[HOOK_NAME] = { [spec.event]: [group] };
    else delete root[HOOK_NAME];
  } else if (kind === 'cursor') {
    const hooks = isObj(root.hooks) ? root.hooks : {};
    const list = arr(hooks[spec.event]).filter((g) => !isOurs(g));
    if (on) list.push({ command: command(kind, opts.exePath) });
    if (list.length) hooks[spec.event] = list; else delete hooks[spec.event];
    root.hooks = hooks;
    root.version = root.version ?? 1;
  } else {
    const hooks = isObj(root.hooks) ? root.hooks : {};
    const groups = arr(hooks[spec.event]).filter((g) => !isOurs(g));
    if (on) groups.push(group);
    if (groups.length) hooks[spec.event] = groups; else delete hooks[spec.event];
    if (Object.keys(hooks).length) root.hooks = hooks; else delete root.hooks;
  }
  fs.mkdirSync(path.dirname(url), { recursive: true });
  P.writeAtomic(url, sortedJson(root));
}

/** JSON with sorted keys and 2-space indent, as the Mac writes settings (JSONSerialization .sortedKeys). */
function sortedJson(v) {
  const sort = (x) => {
    if (Array.isArray(x)) return x.map(sort);
    if (isObj(x)) return Object.fromEntries(Object.keys(x).sort().map((k) => [k, sort(x[k])]));
    return x;
  };
  return JSON.stringify(sort(v), null, 2);
}

function install(kind, exePath, opts = {}) {
  setInstalled(true, kind, { ...opts, exePath });
}

function uninstall(kind, opts = {}) {
  setInstalled(false, kind, opts);
}

// ---- Setup steps the pill offers ("one click to whatever is left to do")

/** shortcut: Codex records trust as [hooks.state."<source>:permission_request:i:j"] in config.toml; matched as text,
 *  update if Codex changes the key's shape. */
function codexTrusted(home = os.homedir()) {
  const toml = P.readTextOrNull(path.join(home, '.codex', 'config.toml')) ?? '';
  return toml.split(/\r?\n/).some((l) => l.startsWith('[hooks.state.') && l.includes('hooks.json:permission_request'));
}

/** Signed in = an auth type picked in settings (or an API key in the environment). Credentials are never read. */
function geminiSignedIn(home = os.homedir(), env = process.env) {
  if (env.GEMINI_API_KEY !== undefined) return true;
  return (P.readTextOrNull(path.join(home, '.gemini', 'settings.json')) ?? '').includes('selectedType');
}

/** The step still left before the pill can answer this agent: what is missing, the button, and the command for it. */
function setupStep(kind, { home = os.homedir(), env = process.env } = {}) {
  if (kind === 'codex' && isInstalled('codex', { home }) && !codexTrusted(home)) {
    return { missing: L('Chưa Trust trong Codex'), button: L('Mở Codex'), command: 'codex' };
  }
  if (kind === 'gemini' && present('gemini', { home }) && !geminiSignedIn(home, env)) {
    return { missing: L('Chưa đăng nhập'), button: L('Đăng nhập'), command: 'gemini' };
  }
  return null;
}

/**
 * Sends your answer into a Codex thread as your next message (`codex queue`). Resolves {ok, out}.
 * shortcut: on Windows only a real codex.exe runs; the npm .cmd shim needs a shell, and building a shell command
 * from answer text is unsafe, so it is refused until the app decides how to call it.
 */
function codexReply(thread, text) {
  const codex = P.findCli('codex');
  if (!codex || !thread || !text) return Promise.resolve({ ok: false, out: 'no codex or empty message' });
  if (process.platform === 'win32' && !/\.exe$/i.test(codex)) return Promise.resolve({ ok: false, out: 'codex is a script shim; not run' });
  return new Promise((resolve) => {
    execFile(codex, ['queue', '--thread', thread, '--message', text], { windowsHide: true, timeout: 60000 },
      (err, stdout, stderr) => resolve({ ok: !err, out: `${stdout ?? ''}${stderr ?? ''}`.slice(0, 300) }));
  });
}

/**
 * Writes a small script that runs `command` in the home folder, for the app to open (macOS: Terminal; Windows: a
 * cmd window). Returns the file path; the app opens it with shell.openPath. The command is always one of our constants.
 */
function openInTerminal(command, dir = askDir()) {
  const base = path.dirname(dir);
  fs.mkdirSync(base, { recursive: true });
  const win = process.platform === 'win32';
  const file = path.join(base, `${command}-setup.${win ? 'cmd' : 'command'}`);
  fs.writeFileSync(file, win ? `@echo off\r\ncd /d "%USERPROFILE%"\r\n${command}\r\n` : `#!/bin/zsh -l\ncd ~\n${command}\n`);
  if (!win) fs.chmodSync(file, 0o755);
  return file;
}

module.exports = {
  WAIT, DENIED, POLL_MS, HOOK_NAME, AGENTS, AGENT_KINDS, present,
  askDir, askFile, replyFile, markAppRunning, appRunning, appPidFile,
  answered, actionLabel,
  askFrom, output, approvalMode, selfApproved, pendingCall, settled, tail, codexTurn,
  runHook,
  pending, watchAsks, reply,
  openCodePlugin, command, isOurs, isInstalled, setInstalled, install, uninstall, sortedJson,
  codexTrusted, geminiSignedIn, setupStep, codexReply, openInTerminal,
};
