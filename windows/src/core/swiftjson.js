'use strict';
// Swift's JSONEncoder/JSONDecoder defaults, as the Mac (Sources/) and the iPhone (iOS/Shared) use them (docs/sync-v1.md).
// Dates are Doubles: seconds since 2001-01-01T00:00:00Z (Swift's reference date). Enums with values are {"case":{"_0":v}}.

const SWIFT_EPOCH_MS = 978307200000; // 1970-01-01 to 2001-01-01, in ms

/** A JS Date (or ms since 1970) as Swift's Double seconds since 2001. */
function toSwiftDate(d) {
  const ms = d instanceof Date ? d.getTime() : Number(d);
  if (!Number.isFinite(ms)) throw new TypeError('toSwiftDate: not a date');
  return (ms - SWIFT_EPOCH_MS) / 1000;
}

/** Swift's Double seconds since 2001 as a JS Date (millisecond precision). */
function fromSwiftDate(n) {
  if (typeof n !== 'number' || !Number.isFinite(n)) throw new TypeError('fromSwiftDate: not a number');
  return new Date(Math.round(n * 1000) + SWIFT_EPOCH_MS);
}

/** Swift omits nil optionals: drop undefined and null keys (shallow). */
function dropNil(o) {
  const out = {};
  for (const k of Object.keys(o)) if (o[k] !== undefined && o[k] !== null) out[k] = o[k];
  return out;
}

/** A Swift enum case without values, as synthesized Codable writes it: {"name":{}}. */
function enumNoValue(name) {
  return { [name]: {} };
}

/** A Swift enum case with unlabeled values, as synthesized Codable writes it: {"name":{"_0":v0,"_1":v1}}. */
function enumWithValues(name, values) {
  const inner = {};
  values.forEach((v, i) => { inner['_' + i] = v; });
  return { [name]: inner };
}

module.exports = { SWIFT_EPOCH_MS, toSwiftDate, fromSwiftDate, dropNil, enumNoValue, enumWithValues };
