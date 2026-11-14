'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const http = require('node:http');
const os = require('node:os');
const path = require('node:path');
const u = require('../src/core/updater');
const { L } = require('../src/core/l10n');

test('OS versions: parse and compare, missing parts count as 0', () => {
  assert.deepEqual(u.parseOS('10.0.22631'), [10, 0, 22631]);
  assert.equal(u.parseOS('10.x'), null);
  assert.equal(u.parseOS(''), null);
  assert.equal(u.compareOS(u.parseOS('10.0.19045'), u.parseOS('10.0.22631')), -1);
  assert.equal(u.compareOS(u.parseOS('10.0'), u.parseOS('10.0.0')), 0);
  assert.equal(u.compareOS(u.parseOS('11.0.22000'), u.parseOS('10.0.99999')), 1);
});

test('newer means a higher build', () => {
  assert.equal(u.isNewer(3, 2), true);
  assert.equal(u.isNewer(2, 2), false);
});

test('allowed URLs: https, or http only to 127.0.0.1', () => {
  assert.equal(u.isAllowedURL('https://example.com/a.exe'), true);
  assert.equal(u.isAllowedURL('http://127.0.0.1:8080/a.exe'), true);
  assert.equal(u.isAllowedURL('http://example.com/a.exe'), false);
  assert.equal(u.isAllowedURL('file:///C:/a.exe'), false);
  assert.equal(u.isAllowedURL('garbage'), false);
});

test('manifest validation mirrors UpdateManifest', () => {
  const good = { version: '2.3.0', build: 7, url: 'https://notchtasks.web.app/a.exe', sha256: 'a'.repeat(64),
    minOS: '10.0.19045', notes: { en: 'English', vi: 'Tiếng Việt' } };
  assert.equal(u.parseManifest(good).build, 7);
  assert.throws(() => u.parseManifest({ ...good, build: '7' }), { kind: 'invalidManifest' });
  assert.throws(() => u.parseManifest({ ...good, sha256: 'xyz' }), { kind: 'invalidManifest' });
  assert.throws(() => u.parseManifest({ ...good, url: 'http://example.com/a.exe' }), { kind: 'invalidManifest' });
  assert.throws(() => u.parseManifest({ ...good, minOS: 'ten' }), { kind: 'invalidManifest' });
  assert.throws(() => u.parseManifest({ version: '2.0' }), { kind: 'invalidManifest' });
});

test('release notes fall back to English, then to the empty note', () => {
  const m = u.parseManifest({ version: '1', build: 1, url: 'https://a.test/x', sha256: 'b'.repeat(64),
    minOS: '10.0', notes: { en: 'English' } });
  assert.equal(u.noteFor(m, 'en'), 'English');
  assert.equal(u.noteFor(m, 'fr'), 'English');
  assert.equal(u.noteFor({ ...m, notes: {} }, 'fr'), L('Không có ghi chú.'));
});

// A local feed and installer: the feed points at the installer, so the whole path runs over real HTTP.
function serve(routes) {
  return new Promise((resolve) => {
    const server = http.createServer((req, res) => {
      const hit = routes[req.url];
      if (!hit) { res.writeHead(404); return res.end(); }
      res.writeHead(200, { 'content-length': hit.length });
      res.end(hit);
    });
    server.listen(0, '127.0.0.1', () => resolve({ server, base: `http://127.0.0.1:${server.address().port}` }));
  });
}

test('finds a newer build for this Windows, refuses older builds and too-new minOS', async () => {
  const installer = Buffer.from('fake installer');
  const sha = crypto.createHash('sha256').update(installer).digest('hex');
  const manifest = (over = {}) => Buffer.from(JSON.stringify({ version: '2.3.0', build: 5,
    url: 'http://127.0.0.1/setup.exe', sha256: sha, minOS: '10.0.19045', notes: {}, ...over }));
  const { server, base } = await serve({ '/version-win.json': manifest(), '/bad.json': Buffer.from('{nope'),
    '/high.json': manifest({ minOS: '11.0.0' }), '/old.json': manifest({ build: 2 }) });
  try {
    const feed = (p) => `${base}${p}`;
    const offered = await u.checkForUpdate({ appBuild: 4, osRelease: '10.0.22631', feed: feed('/version-win.json') });
    assert.equal(offered.version, '2.3.0');
    assert.equal(await u.checkForUpdate({ appBuild: 4, osRelease: '10.0.22631', feed: feed('/old.json') }), null);
    assert.equal(await u.checkForUpdate({ appBuild: 4, osRelease: '10.0.22631', feed: feed('/high.json') }), null);
    assert.equal(await u.checkForUpdate({ appBuild: 4, osRelease: '10.0.22631', feed: feed('/bad.json') }), null);
    assert.equal(await u.checkForUpdate({ appBuild: 4, osRelease: '10.0.22631', feed: feed('/missing.json') }), null);
  } finally {
    server.close();
  }
});

test('an unreachable feed is a download failure, not silent', async () => {
  await assert.rejects(u.checkForUpdate({ appBuild: 1, feed: 'http://127.0.0.1:1/version-win.json' }), { kind: 'download' });
});

test('install downloads, verifies sha256 and returns the path', async () => {
  const installer = Buffer.from('signed bytes');
  const sha = crypto.createHash('sha256').update(installer).digest('hex');
  const { server, base } = await serve({ '/setup.exe': installer });
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'nt-upd-'));
  try {
    const manifest = u.parseManifest({ version: '2.3.0', build: 9, url: `${base}/setup.exe`, sha256: sha, minOS: '10.0', notes: {} });
    const progress = [];
    const file = await u.createUpdater({ appBuild: 1, tmpDir: tmp }).install(manifest, (p) => progress.push(p));
    assert.equal(file, path.join(tmp, 'NotchTasks-update-9.exe'));
    assert.deepEqual(fs.readFileSync(file), installer);
    assert.equal(progress.at(-1), 1);
  } finally {
    server.close();
    fs.rmSync(tmp, { recursive: true, force: true });
  }
});

test('a sha256 mismatch fails and leaves no file behind', async () => {
  const { server, base } = await serve({ '/setup.exe': Buffer.from('tampered') });
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'nt-upd-'));
  try {
    const manifest = u.parseManifest({ version: '2.3.0', build: 9, url: `${base}/setup.exe`, sha256: 'c'.repeat(64),
      minOS: '10.0', notes: {} });
    await assert.rejects(u.createUpdater({ appBuild: 1, tmpDir: tmp }).install(manifest), { kind: 'hash' });
    assert.equal(fs.existsSync(path.join(tmp, 'NotchTasks-update-9.exe')), false);
  } finally {
    server.close();
    fs.rmSync(tmp, { recursive: true, force: true });
  }
});
