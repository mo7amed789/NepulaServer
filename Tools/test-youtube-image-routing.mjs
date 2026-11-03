import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

const app = fs.readFileSync('Html/app.html', 'utf8');
const route = app.slice(app.indexOf('        async function startDownload()'),
    app.indexOf('        async function downloadToPC()'));
const sample = 'https://yt3.ggpht.com/id=s288-c-fcrop64=1,000020b9ffffe0b5-rw-nd-v1';
let redirected;
const context = vm.createContext({
    URL, encodeURIComponent,
    requestNotificationPermission() {}, validateUrl() { return true; },
    document: { getElementById(id) { assert.equal(id, 'videoUrl'); return { value: sample }; } },
    window: { location: { assign(url) { redirected = url; } } }
});
vm.runInContext(route, context);
await context.startDownload();
assert.equal(new URL(redirected, 'http://localhost').pathname, '/images');
assert.equal(new URL(redirected, 'http://localhost').searchParams.get('url'), sample);

const images = fs.readFileSync('Html/images.html', 'utf8');
const incoming = images.slice(images.indexOf('        const incomingImageUrl ='), images.lastIndexOf('    </script>'));
const input = { value: '' };
let queued = false;
vm.runInNewContext(incoming, {
    URLSearchParams, window: { location: { search: new URL(redirected, 'http://localhost').search } },
    document: { getElementById() { return input; } },
    addUrl() { assert.equal(input.value, sample); queued = true; }
});
assert.ok(queued);
console.log('PASS YouTube image links route to image workspace and queue without losing crop/query characters');
