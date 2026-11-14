'use strict';
// Port of Track.swift: GA4 Measurement Protocol over plain HTTPS. Names are lowercase_snake, params are enums or counts.
// MEASUREMENT_ID and API_SECRET stay empty until they are set in Google Analytics: while empty, nothing is sent.

const crypto = require('node:crypto');
const https = require('node:https');

const MEASUREMENT_ID = '';
const API_SECRET = '';
const PLATFORM = 'windows';
const BATCH = 25;
const FLUSH_MS = 60 * 1000;
const OPT_OUT_KEY = 'analytics.off';
const CLIENT_KEY = 'analytics.cid';

function postJson(url, body) {
  return new Promise((resolve, reject) => {
    const u = new URL(url);
    const req = https.request({
      hostname: u.hostname, path: u.pathname + u.search, method: 'POST', timeout: 10000,
      headers: { 'content-type': 'application/json', 'content-length': Buffer.byteLength(body) },
    }, (res) => {
      res.resume();
      res.on('end', () => resolve(res.statusCode));
    });
    req.on('timeout', () => req.destroy(new Error('timeout')));
    req.on('error', reject);
    req.end(body);
  });
}

// load(key) / save(key, value) play the role of UserDefaults. Keys match the Mac's: analytics.off, analytics.cid.
function createTrack({ measurementId = MEASUREMENT_ID, apiSecret = API_SECRET, load = () => undefined,
  save = () => {}, request = postJson, env = process.env, platform = PLATFORM,
  session = Math.floor(Date.now() / 1000), randomId = () => crypto.randomUUID() } = {}) {
  let queue = [];
  let timer = null;
  const enabled = () => !load(OPT_OUT_KEY);

  // One random id per install, created on first send only: it ties events to an install, never to a person.
  function clientId() {
    let id = load(CLIENT_KEY);
    if (!id) {
      id = randomId();
      save(CLIENT_KEY, id);
    }
    return id;
  }

  function schedule() {
    timer = setTimeout(flush, FLUSH_MS);
    timer.unref?.();
  }

  async function flush() {
    clearTimeout(timer);
    timer = null;
    if (!queue.length || !measurementId) return;
    const batch = queue.slice(0, BATCH);
    queue = queue.slice(BATCH);
    const url = 'https://www.google-analytics.com/mp/collect?measurement_id='
      + `${encodeURIComponent(measurementId)}&api_secret=${encodeURIComponent(apiSecret)}`;
    try {
      await request(url, JSON.stringify({ client_id: clientId(), events: batch }));
    } catch {
      // lost on failure: statistics, not data
    }
    if (queue.length) schedule();
  }

  function event(name, params = {}) {
    if (!enabled() || !measurementId || env.CI_SELFTEST !== undefined) return;
    queue.push({ name, params: { session_id: session, engagement_time_msec: 1, platform, ...params } });
    if (queue.length >= BATCH) flush();
    else if (queue.length === 1) schedule();
  }

  return {
    event,
    screen: (name) => event('screen_view', { screen_name: name }),
    flush,
    get enabled() { return enabled(); },
    set enabled(on) {
      save(OPT_OUT_KEY, !on);
      if (!on) queue = [];
    },
  };
}

module.exports = { createTrack, MEASUREMENT_ID, API_SECRET, PLATFORM };
