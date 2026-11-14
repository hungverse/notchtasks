'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawn, spawnSync } = require('node:child_process');

const A = require('../src/core/asks');
const { L } = require('../src/core/l10n');

const FIX = path.join(__dirname, 'fixtures', 'agents');
const fixture = (name) => fs.readFileSync(path.join(FIX, `${name}.json`), 'utf8');
const made = [];
process.on('exit', () => made.forEach((d) => fs.rmSync(d, { recursive: true, force: true })));
const tmp = () => { const d = fs.mkdtempSync(path.join(os.tmpdir(), 'nt-asks-')); made.push(d); return d; };
/** An Asks folder that does not exist yet (tmp() creates its own folder). */
const newDir = () => path.join(tmp(), 'Asks');
const DENY = 'The user denied this from the NotchTasks notch.';
const EXE = 'C:\\Program Files\\NotchTasks\\NotchTasks.exe';

/** Waits until a .json ask appears in dir, then returns it parsed. */
async function waitForAsk(dir) {
  for (let i = 0; i < 60; i++) {
    const f = fs.existsSync(dir) ? fs.readdirSync(dir).find((n) => n.endsWith('.json')) : undefined;
    if (f) return JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8'));
    await new Promise((r) => setTimeout(r, 50));
  }
  throw new Error('no ask written');
}

/** Runs the hook, answers it with `answer` (null = no answer), and checks the Asks folder is clean afterwards. */
async function hook(kind, name, { answer = null, wait = 10, ...opts } = {}) {
  const dir = opts.dir ?? tmp();
  const run = A.runHook(kind, fixture(name), {
    dir, pid: process.pid, ppid: process.pid, appRunning: () => true, home: tmp(), wait, ...opts,
  });
  const ask = await waitForAsk(dir);
  if (answer) A.reply(ask.id, answer, dir);
  const res = await run;
  assert.deepEqual(fs.readdirSync(dir), [], 'ask and reply files are removed');
  return { ask, ...res };
}

const ANSWERS = { 'Proceed?': 'Yes' };
const ANSWERED_TEXT = 'The user already answered this on the NotchTasks notch, so the dialog was not shown. '
  + 'Their answers: "Proceed?" → "Yes". Continue with these answers; do not ask again.';
const claudeAllow = '{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}\n';
const claudeDeny = `{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"deny","message":"${DENY}"}}}\n`;

// ---- Agents and folders

test('agent table: names, CLIs, events, matchers, tested flags', () => {
  assert.equal(A.AGENTS.claude.name, 'Claude');
  assert.equal(A.AGENTS.antigravity.cli, 'agy');
  assert.equal(A.AGENTS.cursor.cli, 'cursor-agent');
  assert.equal(A.AGENTS.gemini.event, 'BeforeTool');
  assert.equal(A.AGENTS.opencode.event, 'permission.ask');
  assert.equal(A.AGENTS.gemini.tested, false);
  assert.equal(A.AGENTS.codex.tested, true);
  assert.equal(A.WAIT, 590);
  assert.equal(A.present('claude', { home: tmp() }), true, 'Claude is always present');
  assert.deepEqual(A.AGENT_KINDS, ['claude', 'codex', 'gemini', 'antigravity', 'cursor', 'opencode']);
});

test('askDir: %APPDATA%\\NotchTasks\\Asks, else ~/.notchtasks/Asks', () => {
  assert.equal(A.askDir({ env: {}, home: '/home/x' }), path.join('/home/x', '.notchtasks', 'Asks'));
  assert.equal(A.askDir({ env: { APPDATA: path.join('/roam', 'AppData') }, home: '/home/x' }),
    path.join('/roam', 'AppData', 'NotchTasks', 'Asks'));
});

test('actionLabel: words for tools, mcp tools by their last part', () => {
  assert.equal(A.actionLabel('Bash'), L('Chạy lệnh'));
  assert.equal(A.actionLabel('write_file'), L('Sửa file'));
  assert.equal(A.actionLabel('view_file'), L('Đọc file'));
  assert.equal(A.actionLabel('WebFetch'), L('Truy cập web'));
  assert.equal(A.actionLabel('mcp__github__create_issue'), L('Dùng công cụ ') + 'issue');
  assert.equal(A.actionLabel('AskUserQuestion'), 'AskUserQuestion');
});

// ---- The hook: parsing each agent's payload

test('claude Bash payload: tool, summary from the command, session from the folder', async () => {
  const { ask } = await hook('claude', 'claude-bash', { answer: { kind: 'allow' } });
  assert.equal(ask.tool, 'Bash');
  assert.equal(ask.summary, 'rm -rf dist');
  assert.equal(ask.session, 'notchtasks', 'Windows path: last folder, either separator');
  assert.deepEqual(ask.questions, []);
  assert.equal(ask.agent, 'claude');
  assert.equal(A.actionLabel(ask.tool), L('Chạy lệnh'));
});

test('claude AskUserQuestion payload: two questions, multiSelect, options as labels', async () => {
  const { ask } = await hook('claude', 'claude-askuserquestion', { answer: { kind: 'allow' } });
  assert.equal(ask.tool, 'AskUserQuestion');
  assert.equal(ask.summary, 'Which parts should the release notes cover?');
  assert.equal(ask.questions.length, 2);
  assert.deepEqual(ask.questions.map((q) => q.multiSelect), [true, false]);
  assert.deepEqual(ask.questions[0].options, ['Features', 'Fixes', 'Known issues']);
  assert.equal(ask.questions[1].header, 'Channel');
});

test('codex payload: command given as an argv array is joined', async () => {
  const { ask } = await hook('codex', 'codex-shell', { answer: { kind: 'allow' } });
  assert.equal(ask.tool, 'shell');
  assert.equal(ask.summary, 'bash -lc npm test');
  assert.equal(ask.session, 'notchtasks');
  assert.equal(ask.agent, 'codex');
});

test('gemini payload: run_shell_command', async () => {
  const { ask } = await hook('gemini', 'gemini-shell', { answer: { kind: 'allow' } });
  assert.equal(ask.tool, 'run_shell_command');
  assert.equal(ask.summary, 'npm run build');
  assert.equal(ask.agent, 'gemini');
});

test('antigravity payload: camelCase toolCall, command line in args', async () => {
  const { ask } = await hook('antigravity', 'antigravity-run', { answer: { kind: 'allow' } });
  assert.equal(ask.tool, 'run_command');
  assert.equal(ask.summary, 'npm test');
  assert.equal(ask.session, 'notchtasks');
});

test('cursor payload: the command sits at the top level', async () => {
  const { ask } = await hook('cursor', 'cursor-shell', { answer: { kind: 'allow' } });
  assert.equal(ask.tool, 'shell');
  assert.equal(ask.summary, 'git push origin main');
  assert.equal(ask.session, 'notchtasks');
});

test('opencode payload: permission evaluation, command from metadata, folder is the agent name', async () => {
  const { ask } = await hook('opencode', 'opencode-permission', { answer: { kind: 'allow' } });
  assert.equal(ask.tool, 'bash');
  assert.equal(ask.summary, 'git push origin main');
  assert.equal(ask.session, 'OpenCode');
});

// ---- The hook: exact stdout for allow, deny, answers, pass

test('claude: allow, deny, answers, pass print exactly what Claude Code expects', async () => {
  assert.equal((await hook('claude', 'claude-bash', { answer: { kind: 'allow' } })).stdout, claudeAllow);
  assert.equal((await hook('claude', 'claude-bash', { answer: { kind: 'deny' } })).stdout, claudeDeny);
  const bash = JSON.parse(fixture('claude-bash'));
  const ans = await hook('claude', 'claude-bash', { answer: { kind: 'answers', answers: ANSWERS } });
  assert.equal(ans.stdout, `${JSON.stringify({ hookSpecificOutput: { hookEventName: 'PermissionRequest',
    decision: { behavior: 'allow', updatedInput: { ...bash.tool_input, answers: ANSWERS } } } })}\n`);
  const pass = await hook('claude', 'claude-bash', { answer: { kind: 'pass' } });
  assert.equal(pass.exitCode, 0);
  assert.equal(pass.stdout, '');
});

test('claude AskUserQuestion: the answers go back in updatedInput', async () => {
  const q = JSON.parse(fixture('claude-askuserquestion'));
  const answers = { 'Which parts should the release notes cover?': 'Features, Fixes', 'Which channel gets the build?': 'Stable' };
  const res = await hook('claude', 'claude-askuserquestion', { answer: { kind: 'answers', answers } });
  assert.equal(res.stdout, `${JSON.stringify({ hookSpecificOutput: { hookEventName: 'PermissionRequest',
    decision: { behavior: 'allow', updatedInput: { ...q.tool_input, answers } } } })}\n`);
});

test('codex: allow, deny, answers, pass use the same PermissionRequest shape', async () => {
  assert.equal((await hook('codex', 'codex-shell', { answer: { kind: 'allow' } })).stdout, claudeAllow);
  assert.equal((await hook('codex', 'codex-shell', { answer: { kind: 'deny' } })).stdout, claudeDeny);
  const ans = await hook('codex', 'codex-shell', { answer: { kind: 'answers', answers: ANSWERS } });
  assert.match(ans.stdout, /^\{"hookSpecificOutput":\{"hookEventName":"PermissionRequest","decision":\{"behavior":"allow","updatedInput":\{"command":\["bash","-lc","npm test"\],"workdir":"C:\\\\Users\\\\hung\\\\Projects\\\\notchtasks","answers":\{"Proceed\?":"Yes"\}\}\}\}\}\n$/);
  assert.equal((await hook('codex', 'codex-shell', { answer: { kind: 'pass' } })).stdout, '');
});

test('gemini: allow, deny, answers (handed over as a deny reason), pass prints nothing', async () => {
  assert.equal((await hook('gemini', 'gemini-shell', { answer: { kind: 'allow' } })).stdout, '{"decision":"allow"}\n');
  assert.equal((await hook('gemini', 'gemini-shell', { answer: { kind: 'deny' } })).stdout, `{"decision":"deny","reason":"${DENY}"}\n`);
  assert.equal((await hook('gemini', 'gemini-shell', { answer: { kind: 'answers', answers: ANSWERS } })).stdout,
    `${JSON.stringify({ decision: 'deny', reason: ANSWERED_TEXT })}\n`);
  assert.equal((await hook('gemini', 'gemini-shell', { answer: { kind: 'pass' } })).stdout, '');
});

test('antigravity: allow grants this one call, pass means ask', async () => {
  assert.equal((await hook('antigravity', 'antigravity-run', { answer: { kind: 'allow' } })).stdout,
    '{"decision":"allow","permissionOverrides":["command(npm test)"]}\n');
  assert.equal((await hook('antigravity', 'antigravity-run', { answer: { kind: 'deny' } })).stdout, `{"decision":"deny","reason":"${DENY}"}\n`);
  assert.equal((await hook('antigravity', 'antigravity-run', { answer: { kind: 'answers', answers: ANSWERS } })).stdout,
    `${JSON.stringify({ decision: 'deny', reason: ANSWERED_TEXT })}\n`);
  assert.equal((await hook('antigravity', 'antigravity-run', { answer: { kind: 'pass' } })).stdout, '{"decision":"ask"}\n');
});

test('cursor: permission allow / deny (with the notch message) / ask', async () => {
  assert.equal((await hook('cursor', 'cursor-shell', { answer: { kind: 'allow' } })).stdout, '{"permission":"allow"}\n');
  assert.equal((await hook('cursor', 'cursor-shell', { answer: { kind: 'deny' } })).stdout,
    `${JSON.stringify({ permission: 'deny', user_message: L('Đã từ chối trên notch'), agent_message: DENY })}\n`);
  assert.equal((await hook('cursor', 'cursor-shell', { answer: { kind: 'answers', answers: ANSWERS } })).stdout, '{"permission":"allow"}\n');
  assert.equal((await hook('cursor', 'cursor-shell', { answer: { kind: 'pass' } })).stdout, '{"permission":"ask"}\n');
});

test('opencode: status allow / deny / ask', async () => {
  assert.equal((await hook('opencode', 'opencode-permission', { answer: { kind: 'allow' } })).stdout, '{"status":"allow"}\n');
  assert.equal((await hook('opencode', 'opencode-permission', { answer: { kind: 'deny' } })).stdout, '{"status":"deny"}\n');
  assert.equal((await hook('opencode', 'opencode-permission', { answer: { kind: 'answers', answers: ANSWERS } })).stdout, '{"status":"allow"}\n');
  assert.equal((await hook('opencode', 'opencode-permission', { answer: { kind: 'pass' } })).stdout, '{"status":"ask"}\n');
});

// ---- The hook: when it does not ask the app

test('no reply before the wait runs out: pass, files cleaned', async () => {
  const res = await hook('claude', 'claude-bash', { wait: 1 });
  assert.equal(res.stdout, '');
  assert.equal(res.exitCode, 0);
});

test('app not running: no ask is written, the agent asks as usual', async () => {
  const dir = newDir();
  const res = await A.runHook('antigravity', fixture('antigravity-run'), { dir, appRunning: () => false, home: tmp() });
  assert.equal(res.stdout, '{"decision":"ask"}\n');
  assert.equal(fs.existsSync(dir), false);
});

test('a payload for another event passes straight through', async () => {
  const res = await A.runHook('claude', fixture('gemini-shell'), { dir: tmp(), appRunning: () => true, home: tmp() });
  assert.equal(res.stdout, '');
});

test('not JSON on stdin: pass', async () => {
  assert.equal((await A.runHook('gemini', 'not json', { dir: tmp(), appRunning: () => true })).stdout, '');
});

test('codex set to approve on its own (permission_mode auto_review) is not asked', async () => {
  const dir = newDir();
  const payload = JSON.parse(fixture('codex-shell'));
  payload.permission_mode = 'auto_review';
  const res = await A.runHook('codex', JSON.stringify(payload), { dir, appRunning: () => true, home: tmp() });
  assert.equal(res.stdout, '');
  assert.equal(fs.existsSync(dir), false);
});

test('antigravity turbo preset (from its config) is allowed without asking, questions still come here', async () => {
  const home = tmp();
  fs.mkdirSync(path.join(home, '.gemini', 'config'), { recursive: true });
  fs.writeFileSync(path.join(home, '.gemini', 'config', 'config.json'),
    JSON.stringify({ userSettings: { permissionPreset: 'AGENT_PERMISSION_PRESET_TURBO' } }));
  const dir = newDir();
  const res = await A.runHook('antigravity', fixture('antigravity-run'), { dir, appRunning: () => true, home });
  assert.equal(res.stdout, '{"decision":"allow"}\n');
  assert.equal(fs.existsSync(dir), false);
  const q = JSON.parse(fixture('antigravity-run'));
  q.toolCall.name = 'ask_question';
  const asked = waitForAsk(dir);
  const run = A.runHook('antigravity', JSON.stringify(q), { dir, appRunning: () => true, home, wait: 1 });
  assert.equal((await asked).tool, 'ask_question');
  assert.equal((await run).stdout, '{"decision":"ask"}\n');
});

test('codex auto review (from the rollout) is allowed without asking', async () => {
  const dir = newDir();
  const rollout = path.join(tmp(), 'rollout.jsonl');
  fs.writeFileSync(rollout, '{"type":"turn_context","payload":{"approvals_reviewer":"auto_review"}}\n');
  const payload = JSON.parse(fixture('codex-shell'));
  payload.transcript_path = rollout;
  const res = await A.runHook('codex', JSON.stringify(payload), { dir, appRunning: () => true, home: tmp() });
  assert.equal(res.stdout, '');
  assert.equal(fs.existsSync(dir), false);
});

test('claude: the call is settled in its transcript (answered in the agent) so the hook leaves early', async () => {
  const transcript = path.join(tmp(), 'session.jsonl');
  const bash = JSON.parse(fixture('claude-bash'));
  fs.writeFileSync(transcript, `${JSON.stringify({ message: { content: [{ type: 'tool_use', id: 'toolu_1', name: 'Bash', input: bash.tool_input }] } })}\n`);
  const payload = { ...bash, transcript_path: transcript };
  setTimeout(() => fs.appendFileSync(transcript,
    `${JSON.stringify({ message: { content: [{ type: 'tool_result', tool_use_id: 'toolu_1', content: 'ok' }] } })}\n`), 400);
  const started = Date.now();
  const res = await A.runHook('claude', JSON.stringify(payload), { dir: tmp(), pid: process.pid, ppid: process.pid, appRunning: () => true, home: tmp(), wait: 60 });
  assert.equal(res.stdout, '');
  assert.ok(Date.now() - started < 10000, 'left well before the 60 s wait');
});

// ---- The app side

const writeAsk = (dir, id, pid, at, extra = {}) => fs.writeFileSync(A.askFile(dir, id), JSON.stringify({
  id, pid, session: 's', tool: 'Bash', summary: 'x', questions: [], at: new Date(at).toISOString(), agent: 'claude', ...extra,
}));

test('pending: drops asks of dead hooks and old ones, hides answered ones, oldest first', () => {
  const dir = tmp();
  const dead = spawnSync(process.execPath, ['-e', '0']).pid;
  const now = Date.now();
  writeAsk(dir, 'newer', process.pid, now - 1000);
  writeAsk(dir, 'older', process.pid, now - 5000);
  writeAsk(dir, 'dead', dead, now);
  writeAsk(dir, 'old', process.pid, now - 591e3);
  writeAsk(dir, 'answered', process.pid, now);
  fs.writeFileSync(A.replyFile(dir, 'answered'), '{"kind":"allow"}');
  assert.deepEqual(A.pending(dir, { now }).map((a) => a.id), ['older', 'newer']);
  assert.equal(fs.existsSync(A.askFile(dir, 'dead')), false);
  assert.equal(fs.existsSync(A.askFile(dir, 'old')), false);
  assert.equal(fs.existsSync(A.askFile(dir, 'answered')), true, 'answered: left for the hook to pick up');
});

test('watchAsks: reports new asks, and drops one once it is answered', async () => {
  const dir = tmp();
  const seen = [];
  const w = A.watchAsks(dir, (list) => seen.push(list.map((a) => a.id)), { intervalMs: 100 });
  writeAsk(dir, 'live', process.pid, Date.now());
  for (let i = 0; i < 60 && !seen.some((s) => s.includes('live')); i++) await new Promise((r) => setTimeout(r, 50));
  assert.ok(seen.some((s) => s.includes('live')));
  A.reply('live', { kind: 'allow' }, dir);
  w.scan();
  assert.deepEqual(seen[seen.length - 1], []);
  w.close();
});

test('markAppRunning / appRunning: a live pid other than this process', async () => {
  const dir = path.join(tmp(), 'NotchTasks', 'Asks');
  assert.equal(A.appRunning(dir), false, 'no pid file');
  const child = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)']);
  try {
    A.markAppRunning(dir, child.pid);
    assert.equal(A.appRunning(dir), true);
    A.markAppRunning(dir, process.pid);
    assert.equal(A.appRunning(dir), false, 'this process does not count as the app');
  } finally {
    child.kill();
  }
});

// ---- Installing the hook

const readJson = (file) => JSON.parse(fs.readFileSync(file, 'utf8'));

test('claude install: keeps the other settings and hooks, idempotent, uninstall keeps the rest', () => {
  const home = tmp();
  const file = path.join(home, '.claude', 'settings.json');
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, JSON.stringify({ model: 'opus', hooks: { PermissionRequest: [{ matcher: 'Bash', hooks: [{ type: 'command', command: 'other' }] }] } }));
  A.install('claude', EXE, { home });
  A.install('claude', EXE, { home });
  let j = readJson(file);
  assert.equal(j.model, 'opus');
  assert.equal(j.hooks.PermissionRequest.length, 2, 'twice: still one of ours');
  const ours = j.hooks.PermissionRequest[1];
  assert.equal(ours.matcher, '*');
  assert.equal(ours.hooks[0].command, `"${EXE}" --hook claude`);
  assert.equal(ours.hooks[0].timeout, 600, 'wait + 10 seconds');
  assert.equal(A.isInstalled('claude', { home }), true);
  assert.equal(fs.existsSync(`${file}.notchtasks-backup`), true);
  A.uninstall('claude', { home });
  assert.equal(A.isInstalled('claude', { home }), false);
  j = readJson(file);
  assert.equal(j.model, 'opus');
  assert.deepEqual(j.hooks.PermissionRequest, [{ matcher: 'Bash', hooks: [{ type: 'command', command: 'other' }] }]);
});

test('claude install into a missing file creates it; uninstall leaves no hooks key', () => {
  const home = tmp();
  A.install('claude', EXE, { home });
  assert.equal(A.isInstalled('claude', { home }), true);
  A.uninstall('claude', { home });
  assert.deepEqual(readJson(path.join(home, '.claude', 'settings.json')), {});
});

test('claude settings path follows the home given', () => {
  assert.equal(A.AGENTS.claude.settingsFile('/h').endsWith(path.join('.claude', 'settings.json')), true);
});

test('gemini: named hook with a timeout in milliseconds', () => {
  const home = tmp();
  A.install('gemini', EXE, { home });
  const hook1 = readJson(path.join(home, '.gemini', 'settings.json')).hooks.BeforeTool[0];
  assert.match(hook1.matcher, /run_shell_command/);
  assert.equal(hook1.hooks[0].name, 'notchtasks');
  assert.equal(hook1.hooks[0].timeout, 600000);
  A.uninstall('gemini', { home });
  assert.equal(A.isInstalled('gemini', { home }), false);
});

test('codex: hook in ~/.codex/hooks.json, others kept', () => {
  const home = tmp();
  fs.mkdirSync(path.join(home, '.codex'), { recursive: true });
  fs.writeFileSync(path.join(home, '.codex', 'hooks.json'), JSON.stringify({ hooks: { PermissionRequest: [{ matcher: 'x', hooks: [{ type: 'command', command: 'mine' }] }] } }));
  A.install('codex', EXE, { home });
  assert.equal(A.isInstalled('codex', { home }), true);
  assert.equal(readJson(path.join(home, '.codex', 'hooks.json')).hooks.PermissionRequest.length, 2);
  A.uninstall('codex', { home });
  assert.equal(readJson(path.join(home, '.codex', 'hooks.json')).hooks.PermissionRequest.length, 1);
});

test('antigravity: keyed by name, removed by name', () => {
  const home = tmp();
  A.install('antigravity', EXE, { home });
  const root = readJson(path.join(home, '.gemini', 'config', 'hooks.json'));
  assert.equal(root.notchtasks.PreToolUse[0].hooks[0].command, `"${EXE}" --hook antigravity`);
  assert.equal(A.isInstalled('antigravity', { home }), true);
  A.uninstall('antigravity', { home });
  assert.deepEqual(readJson(path.join(home, '.gemini', 'config', 'hooks.json')), {});
});

test('cursor: flat list under hooks, version kept, others kept', () => {
  const home = tmp();
  const file = path.join(home, '.cursor', 'hooks.json');
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, JSON.stringify({ version: 1, hooks: { beforeShellExecution: [{ command: 'mine.sh' }] } }));
  A.install('cursor', EXE, { home });
  A.install('cursor', EXE, { home });
  let j = readJson(file);
  assert.equal(j.hooks.beforeShellExecution.length, 2);
  assert.equal(j.version, 1);
  assert.equal(A.isInstalled('cursor', { home }), true);
  A.uninstall('cursor', { home });
  assert.deepEqual(readJson(file).hooks.beforeShellExecution, [{ command: 'mine.sh' }]);
  assert.equal(A.isInstalled('cursor', { home }), false);
});

test('opencode: plugin file spawns the exe with --hook opencode; backup of an old file; removed on uninstall', () => {
  const home = tmp();
  const file = path.join(home, '.config', 'opencode', 'plugins', 'notchtasks.js');
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, '// someone else\n');
  A.install('opencode', EXE, { home });
  const text = fs.readFileSync(file, 'utf8');
  assert.ok(text.includes(`spawn(${JSON.stringify(EXE)}, ["--hook", "opencode"]`));
  assert.equal(A.isInstalled('opencode', { home }), true);
  assert.equal(fs.readFileSync(`${file}.notchtasks-backup`, 'utf8'), '// someone else\n');
  A.uninstall('opencode', { home });
  assert.equal(fs.existsSync(file), false);
});

test('a settings file we cannot read is never overwritten', () => {
  const home = tmp();
  const file = path.join(home, '.claude', 'settings.json');
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, '{ not json');
  assert.throws(() => A.install('claude', EXE, { home }));
  assert.equal(fs.readFileSync(file, 'utf8'), '{ not json');
  fs.writeFileSync(file, '[1, 2]');
  assert.throws(() => A.install('claude', EXE, { home }), /not a JSON object/);
  assert.equal(fs.readFileSync(file, 'utf8'), '[1, 2]');
});

test('command: quoted exe path with --hook', () => {
  assert.equal(A.command('codex', EXE), `"${EXE}" --hook codex`);
});

// ---- Setup steps

test('codex trust is read from config.toml; setup step asks for it until trusted', () => {
  const home = tmp();
  fs.mkdirSync(path.join(home, '.codex'), { recursive: true });
  assert.equal(A.codexTrusted(home), false);
  fs.writeFileSync(path.join(home, '.codex', 'config.toml'),
    'model = "x"\r\n[hooks.state."C:/h/hooks.json:permission_request:0:0"]\r\ntrusted_hash = "y"\r\n');
  assert.equal(A.codexTrusted(home), true);
});

test('setupStep: none for agents without a step; none for Gemini once an API key is set', () => {
  const home = tmp();
  assert.equal(A.setupStep('cursor', { home }), null);
  assert.equal(A.setupStep('gemini', { home, env: { GEMINI_API_KEY: 'x' } }), null);
});

test('openInTerminal writes a script beside the Asks folder and returns its path', () => {
  const base = tmp();
  const file = A.openInTerminal('codex', path.join(base, 'NotchTasks', 'Asks'));
  assert.equal(path.dirname(file), path.join(base, 'NotchTasks'));
  assert.match(fs.readFileSync(file, 'utf8'), /codex/);
});
