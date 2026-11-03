import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

function readFunctionRegion(path, start, end) {
    const source = fs.readFileSync(path, 'utf8');
    const first = source.indexOf(start);
    assert.ok(first >= 0, `${start} exists`);
    const last = source.indexOf(end, first);
    assert.ok(last > first, `${end} exists`);
    return source.slice(first, last);
}

// Exercise the actual page function with a fake network. Responses deliberately
// finish out of order, include duplicates/failures and a malformed filename.
const imageSource = readFunctionRegion('Html/images.html',
    '        async function downloadImagesFromUrl(url)', '        // ─── Gallery');
let active = 0;
let peak = 0;
const requests = [];
const blobs = [];
const imageContext = vm.createContext({
    pairingKey: 'test', gallery: [],
    URL: { createObjectURL(blob) { blobs.push(blob); return `blob:${blob.index}`; } },
    async fetch(endpoint, options) {
        if (endpoint.endsWith('/analyze')) {
            return { ok: true, json: async () => ({ title: 'a/b', images:
                [1, 2, 3, 2, 4, 5, 6, 7].map(index => ({ url: `https://image/${index}` })) }) };
        }
        const index = Number(JSON.parse(options.body).Url.split('/').pop());
        requests.push(index);
        peak = Math.max(peak, ++active);
        await new Promise(resolve => setTimeout(resolve, (4 - index % 3) * 5));
        active--;
        if (index === 4) throw new Error('network interruption');
        return { ok: index !== 5,
            headers: { get: () => index === 6 ? "filename*=UTF-8''invalid%ZZ" : null },
            blob: async () => ({ index, type: 'image/jpeg', originalBytes: true }) };
    }
});
vm.runInContext(imageSource, imageContext);
const start = performance.now();
assert.equal(await imageContext.downloadImagesFromUrl('https://post'), 5);
assert.equal(peak, 3, 'three concurrent downloads, never unbounded');
assert.equal(requests.length, 7, 'duplicate URL fetched only once');
assert.deepEqual(Array.from(imageContext.gallery, image => image.blobUrl),
    ['blob:7', 'blob:6', 'blob:3', 'blob:2', 'blob:1'], 'stable gallery order');
assert.equal(imageContext.gallery[1].name, 'a_b_7.jpg', 'malformed header uses safe fallback');
assert.ok(blobs.every(blob => blob.originalBytes), 'full original blobs retained');
console.log(`Carousel: 7 unique requests, max concurrency ${peak}, 5 originals retained (${Math.round(performance.now() - start)} ms fake network)`);
imageContext.fetch = async () => ({ ok: true, json: async () => ({ images: [] }) });
await assert.rejects(() => imageContext.downloadImagesFromUrl('https://post'), /No images/);

const pollingSource = readFunctionRegion('Html/app.html',
    '        let pairingPollTimer = null;', '        async function fetchVideoQualities');
let timerId = 0;
const timers = new Map();
const handlers = {};
const elements = new Map();
let badgeRenders = 0;
const document = {
    hidden: false,
    getElementById(id) {
        if (!elements.has(id)) elements.set(id, {
            dataset: {}, style: {}, replaceChildren() { badgeRenders++; }
        });
        return elements.get(id);
    },
    createElement() { return { style: {} }; },
    addEventListener(event, callback) { handlers[event] = callback; }
};
let pending = [];
let fetchCount = 0;
const pollingContext = vm.createContext({
    globalPairingKey: '', document, AbortController,
    setTimeout(callback, delay) { timers.set(++timerId, { callback, delay }); return timerId; },
    clearTimeout(id) { timers.delete(id); },
    fetch(endpoint, options) {
        fetchCount++;
        return new Promise((resolve, reject) => {
            options.signal.addEventListener('abort', () => reject(new Error('aborted')), { once: true });
            pending.push(() => resolve({ ok: true, json: async () => endpoint.endsWith('/pairing')
                ? { pairingKey: 'key', serverId: 'server' } : { isServerRunning: true } }));
        });
    }
});
vm.runInContext(pollingSource, pollingContext);
const poll = pollingContext.loadPairingData();
assert.equal(fetchCount, 2, 'independent status endpoints requested in parallel');
assert.equal(pollingContext.loadPairingData(), poll, 'overlapping requests share one promise');
pending.splice(0).forEach(resolve => resolve());
await poll;
assert.equal(badgeRenders, 1);
assert.deepEqual([...timers.values()].map(timer => timer.delay), [5000]);
document.hidden = true;
handlers.visibilitychange();
assert.equal(timers.size, 0, 'hidden page stops polling');
document.hidden = false;
handlers.visibilitychange();
const resumed = pollingContext.loadPairingData();
assert.equal(fetchCount, 4, 'visible page refreshes immediately');
pending.splice(0).forEach(resolve => resolve());
await resumed;
assert.equal(badgeRenders, 1, 'unchanged badge is not rebuilt');
const timeoutPoll = pollingContext.loadPairingData();
[...timers.values()].find(timer => timer.delay === 10000).callback();
await timeoutPoll;
assert.equal(elements.get('serverBadge').dataset.status, 'Error');
assert.deepEqual([...timers.values()].map(timer => timer.delay), [5000], 'timed out requests reschedule');
assert.ok(!fs.readFileSync('Html/app.html', 'utf8').includes('setInterval(loadPairingData'), 'no duplicate legacy interval');
console.log('Polling: parallel, single flight, hidden pause, immediate resume, stable DOM and timeout recovery passed');
