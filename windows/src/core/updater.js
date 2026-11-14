'use strict';
// Port of Updater.swift for Windows. Feed: version-win.json. A 404 or unreadable feed means no update, silently.
// Dropped as Mac-only: DMG mount, bundle copy, code-signature match, the swap script. The caller launches the
// verified installer.
// shortcut: no Authenticode check on the installer, the sha256 from the https manifest is the only check; add
// Get-AuthenticodeSignature before launch if the feed ever leaves our own hosting.

const crypto = require('node:crypto');
const fs = require('node:fs');
const http = require('node:http');
const https = require('node:https');
const os = require('node:os');
const path = require('node:path');
const { pipeline } = require('node:stream/promises');
const { L } = require('./l10n');

const DEFAULT_FEED = 'https://notchtasks.web.app/version-win.json';
const MAX_MANIFEST_BYTES = 64 * 1024;
const SHA256 = /^[0-9a-f]{64}$/i;

const MESSAGES = {
  invalidManifest: () => L('Thông tin bản cập nhật không hợp lệ.'),
  download: () => L('Không tải được bản cập nhật.'),
  hash: () => L('Tệp tải xuống không khớp mã kiểm tra.'),
  oldBuild: () => L('Bản cập nhật không mới hơn bản đang chạy.'),
  pending: () => L('Còn một bản cập nhật chưa hoàn tất. Hãy thử lại sau.'),
};

class UpdateFailure extends Error {
  constructor(kind) {
    super(MESSAGES[kind]());
    this.name = 'UpdateFailure';
    this.kind = kind;
  }
}

// Only https, or plain http to the loopback address (tests and local feeds), as Updater.isAllowedURL.
function isAllowedURL(url) {
  try {
    const u = new URL(url);
    return u.protocol === 'https:' || (u.protocol === 'http:' && u.hostname === '127.0.0.1');
  } catch {
    return false;
  }
}

// "10.0.22631" -> [10, 0, 22631]; null unless every part is a number and there are at most three.
function parseOS(text) {
  if (typeof text !== 'string') return null;
  const parts = text.split('.');
  if (parts.length > 3 || !parts.every((p) => /^\d+$/.test(p))) return null;
  return parts.map(Number);
}

// -1, 0 or 1; missing parts count as 0, so "26.0" equals "26.0.0".
function compareOS(a, b) {
  for (let i = 0; i < 3; i++) {
    const x = a[i] ?? 0;
    const y = b[i] ?? 0;
    if (x !== y) return x < y ? -1 : 1;
  }
  return 0;
}

const isNewer = (build, current) => build > current;

// Throws UpdateFailure('invalidManifest') for anything the Mac's UpdateManifest init refuses.
function parseManifest(m) {
  const bad = () => new UpdateFailure('invalidManifest');
  if (!m || typeof m !== 'object') throw bad();
  const { version, build, url, sha256, minOS, notes } = m;
  if (typeof version !== 'string' || !version || !Number.isInteger(build) || build <= 0) throw bad();
  if (typeof url !== 'string' || !isAllowedURL(url)) throw bad();
  if (typeof sha256 !== 'string' || !SHA256.test(sha256) || parseOS(minOS) === null) throw bad();
  if (!notes || typeof notes !== 'object' || !Object.values(notes).every((v) => typeof v === 'string')) throw bad();
  return { version, build, url, sha256: sha256.toLowerCase(), minOS, notes };
}

const noteFor = (manifest, lang) => manifest.notes[lang] ?? manifest.notes.en ?? L('Không có ghi chú.');

// GET with redirects followed (https only, or loopback http). Resolves with the response stream.
function get(url, { timeout = 10000, redirects = 5 } = {}) {
  return new Promise((resolve, reject) => {
    const lib = url.startsWith('http:') ? http : https;
    const req = lib.get(url, { timeout, headers: { 'cache-control': 'no-cache' } }, (res) => {
      if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location) {
        res.resume();
        const target = new URL(res.headers.location, url).href;
        if (redirects <= 0 || !isAllowedURL(target)) return reject(new UpdateFailure('download'));
        return get(target, { timeout, redirects: redirects - 1 }).then(resolve, reject);
      }
      resolve(res);
    });
    req.on('timeout', () => req.destroy(new Error('timeout')));
    req.on('error', reject);
  });
}

// The parsed feed, or null for 404, a body that is not JSON, or one over 64 KB. Network failures throw.
async function readManifest(feed) {
  let res;
  try {
    res = await get(feed);
  } catch {
    throw new UpdateFailure('download');
  }
  if (res.statusCode === 404) { res.resume(); return null; }
  if (res.statusCode < 200 || res.statusCode >= 300) { res.resume(); throw new UpdateFailure('download'); }
  const chunks = [];
  let size = 0;
  for await (const chunk of res) {
    size += chunk.length;
    if (size > MAX_MANIFEST_BYTES) { res.destroy(); return null; }
    chunks.push(chunk);
  }
  try {
    return JSON.parse(Buffer.concat(chunks).toString('utf8'));
  } catch {
    return null;
  }
}

// The manifest when it is newer than appBuild and this Windows build meets minOS; otherwise null.
async function checkForUpdate({ appBuild, osRelease = os.release(), feed = DEFAULT_FEED }) {
  const raw = await readManifest(isAllowedURL(feed) ? feed : DEFAULT_FEED);
  if (raw === null) return null;
  let manifest;
  try {
    manifest = parseManifest(raw);
  } catch {
    return null;
  }
  const current = parseOS(osRelease);
  if (!isNewer(manifest.build, appBuild) || !current || compareOS(current, parseOS(manifest.minOS)) < 0) return null;
  return manifest;
}

// Streams the installer to dest; onProgress(0…1) when the length is known.
async function download(url, dest, onProgress) {
  let res;
  try {
    res = await get(url, { timeout: 30000 });
  } catch {
    throw new UpdateFailure('download');
  }
  if (res.statusCode < 200 || res.statusCode >= 300) { res.resume(); throw new UpdateFailure('download'); }
  const total = Number(res.headers['content-length']) || 0;
  let got = 0;
  if (onProgress && total) res.on('data', (c) => onProgress(Math.min(1, (got += c.length) / total)));
  try {
    await pipeline(res, fs.createWriteStream(dest));
  } catch {
    await fs.promises.rm(dest, { force: true });
    throw new UpdateFailure('download');
  }
}

async function sha256File(file) {
  const hash = crypto.createHash('sha256');
  for await (const chunk of fs.createReadStream(file)) hash.update(chunk);
  return hash.digest('hex');
}

// Downloads the installer into tmpDir and checks its sha256 against the manifest. Returns the path.
async function fetchInstaller(manifest, { tmpDir = os.tmpdir(), onProgress } = {}) {
  const dest = path.join(tmpDir, `NotchTasks-update-${manifest.build}.exe`);
  await fs.promises.rm(dest, { force: true });
  await download(manifest.url, dest, onProgress);
  if ((await sha256File(dest)) !== manifest.sha256) {
    await fs.promises.rm(dest, { force: true });
    throw new UpdateFailure('hash');
  }
  return dest;
}

function createUpdater({ appBuild, osRelease = os.release(), feed = DEFAULT_FEED, tmpDir = os.tmpdir() }) {
  let installing = false;
  return {
    check: () => checkForUpdate({ appBuild, osRelease, feed }),
    async install(manifest, onProgress) {
      if (installing) throw new UpdateFailure('pending');
      installing = true;
      try {
        return await fetchInstaller(manifest, { tmpDir, onProgress });
      } finally {
        installing = false;
      }
    },
  };
}

module.exports = {
  createUpdater, checkForUpdate, fetchInstaller, parseManifest, parseOS, compareOS, isNewer, isAllowedURL,
  noteFor, sha256File, UpdateFailure, DEFAULT_FEED,
};
