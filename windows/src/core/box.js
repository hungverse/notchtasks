'use strict';
// Sealed boxes, pairing and frames (docs/sync-v1.md), as the Mac (Sources/Sync.swift) and the iPhone (iOS/Shared/Seal.swift)
// do them. node:crypto only. A box is base64 of nonce (12) | ciphertext | tag (16), AES-256-GCM.

const crypto = require('node:crypto');

const NONCE = 12;
const TAG = 16;
const KEY = 32;
const MAX_FRAME = 4 * 1024 * 1024; // a frame longer than this is not ours: the connection is dropped

function b64url(buf) {
  return Buffer.from(buf).toString('base64url');
}

/** Strict: base64 or base64url characters only, no padding needed. Throws on anything else. */
function fromB64url(s) {
  if (typeof s !== 'string' || !/^[A-Za-z0-9+/_-]*$/.test(s) || s.length % 4 === 1) throw new TypeError('not base64url');
  return Buffer.from(s, 'base64');
}

function randomGroup() {
  return { g: b64url(crypto.randomBytes(16)), k: b64url(crypto.randomBytes(KEY)) };
}

function checkKey(key) {
  if (!Buffer.isBuffer(key) || key.length !== KEY) throw new TypeError('key must be 32 bytes');
}

function sealBytes(plain, key) {
  checkKey(key);
  const nonce = crypto.randomBytes(NONCE);
  const c = crypto.createCipheriv('aes-256-gcm', key, nonce, { authTagLength: TAG });
  const ct = Buffer.concat([c.update(plain), c.final()]);
  return Buffer.concat([nonce, ct, c.getAuthTag()]);
}

/** Throws when the tag does not match (wrong key, tampered or truncated box). */
function openBytes(combined, key) {
  checkKey(key);
  const buf = Buffer.from(combined);
  if (buf.length < NONCE + TAG) throw new Error('box too short');
  const d = crypto.createDecipheriv('aes-256-gcm', key, buf.subarray(0, NONCE), { authTagLength: TAG });
  d.setAuthTag(buf.subarray(buf.length - TAG));
  return Buffer.concat([d.update(buf.subarray(NONCE, buf.length - TAG)), d.final()]);
}

function seal(obj, key) {
  return sealBytes(Buffer.from(JSON.stringify(obj), 'utf8'), key).toString('base64');
}

function open(box, key) {
  if (typeof box !== 'string' || !/^[A-Za-z0-9+/]*={0,2}$/.test(box) || box.length % 4 !== 0) throw new TypeError('not a base64 box');
  return JSON.parse(openBytes(Buffer.from(box, 'base64'), key).toString('utf8'));
}

/** Port of Pairing.parse (iOS/Shared/Seal.swift). Returns {v,g,k,n} or null. */
function parsePairing(text) {
  if (typeof text !== 'string') return null;
  let raw = text.trim();
  if (raw.startsWith('https://') || raw.startsWith('notchtasks://')) {
    let u;
    try { u = new URL(raw); } catch { return null; }
    if (raw.startsWith('https://')) {
      if (u.host !== 'notchtasks.web.app' || u.pathname !== '/pair') return null;
    } else if (u.host !== 'pair' && u.pathname !== '/pair') {
      return null;
    }
    const hash = raw.indexOf('#');
    const frag = hash < 0 ? '' : raw.slice(hash + 1);
    if (!frag) return null;
    try { raw = decodeURIComponent(frag); } catch { return null; }
  } else if (raw.includes('://')) {
    return null;
  }
  // typed codes get iOS smart quotes
  const json = raw.replace(/[“”„]/g, '"').replace(/[‘’]/g, "'");
  let p;
  try { p = JSON.parse(json); } catch { return null; }
  if (!p || typeof p !== 'object' || Array.isArray(p)) return null;
  const { v, g, k, n } = p;
  if (v !== 1 || typeof g !== 'string' || typeof k !== 'string' || typeof n !== 'string') return null;
  if ([...g].length < 16 || !/^[\p{L}\p{N}_-]+$/u.test(g)) return null;
  let key;
  try { key = fromB64url(k); } catch { return null; }
  if (key.length !== KEY) return null;
  return { v, g, k, n };
}

/** The QR text, as SyncGroup.code builds it (the name's quotes become apostrophes). */
function pairCode({ g, k, name }) {
  return `{"v":1,"g":"${g}","k":"${k}","n":"${String(name).replace(/"/g, "'")}"}`;
}

// Characters URLComponents' .urlFragmentAllowed leaves as they are (checked against Swift on this Mac). Everything else,
// '%' and '#' included, is percent-encoded, non-ASCII bytes too, in uppercase hex.
const FRAGMENT_KEEP = /[!$&'()*+,\-./0-9:;=?@A-Z_a-z~]/;

/** As SyncHub.pairURL: the QR text inside the https fragment. */
function pairURL(code) {
  let out = '';
  for (const b of Buffer.from(code, 'utf8')) {
    const c = String.fromCharCode(b);
    out += FRAGMENT_KEEP.test(c) ? c : '%' + b.toString(16).toUpperCase().padStart(2, '0');
  }
  return 'https://notchtasks.web.app/pair#' + out;
}

/** The Bonjour name: "NotchTasks " + first 6 bytes of SHA-256(g), hex. Reveals nothing about the group. */
function serviceName(g) {
  return 'NotchTasks ' + crypto.createHash('sha256').update(g, 'utf8').digest().subarray(0, 6).toString('hex');
}

/** 4-byte big-endian length, then the bytes (one box). */
function frame(body) {
  const b = Buffer.from(body);
  if (b.length === 0 || b.length > MAX_FRAME) throw new RangeError(`frame length ${b.length} out of range`);
  const head = Buffer.alloc(4);
  head.writeUInt32BE(b.length, 0);
  return Buffer.concat([head, b]);
}

/** Splits a TCP byte stream into frame payloads. Throws on a length of 0 or over 4 MB: the caller drops the connection. */
class FrameReader {
  constructor() {
    this.buf = Buffer.alloc(0);
  }

  push(chunk) {
    this.buf = Buffer.concat([this.buf, Buffer.from(chunk)]);
    const out = [];
    while (this.buf.length >= 4) {
      const n = this.buf.readUInt32BE(0);
      if (n === 0 || n > MAX_FRAME) throw new RangeError(`frame length ${n} out of range`);
      if (this.buf.length < 4 + n) break;
      out.push(Buffer.from(this.buf.subarray(4, 4 + n)));
      this.buf = Buffer.from(this.buf.subarray(4 + n));
    }
    return out;
  }
}

module.exports = {
  b64url, fromB64url, randomGroup, seal, open, sealBytes, openBytes,
  parsePairing, pairCode, pairURL, serviceName, frame, FrameReader, MAX_FRAME,
};
