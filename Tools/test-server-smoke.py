"""Run the Release host against isolated state, without opening the desktop UI."""
import gzip
import argparse
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / 'bin/Release/net10.0-windows/win-x64'
parser = argparse.ArgumentParser()
parser.add_argument('--youtube-image-url', help='Optional live CDN URL for image analyze/download verification')
args = parser.parse_args()

with socket.socket() as sock:
    sock.bind(('127.0.0.1', 0))
    port = sock.getsockname()[1]

with tempfile.TemporaryDirectory(prefix='tanzeel-smoke-') as temp:
    env = dict(os.environ, TANZEEL_DATA_DIR=temp, Server__Host='127.0.0.1', Server__Port=str(port))
    log = open(Path(temp) / 'host-output.log', 'wb')
    process = subprocess.Popen([str(OUTPUT / 'NebulaServer.exe'), '--headless'], cwd=OUTPUT,
                               env=env, stdout=log, stderr=subprocess.STDOUT,
                               creationflags=subprocess.CREATE_NO_WINDOW)
    try:
        base = f'http://127.0.0.1:{port}'
        def get(path, headers=None):
            return urllib.request.urlopen(urllib.request.Request(base + path, headers=headers or {}), timeout=5)
        deadline = time.monotonic() + 30
        while True:
            if process.poll() is not None:
                raise RuntimeError(f'Host exited with code {process.returncode}')
            try:
                with get('/api/health') as response:
                    assert json.load(response)['status'] == 'online'
                break
            except (OSError, urllib.error.URLError):
                if time.monotonic() > deadline: raise
                time.sleep(.2)
        for path in ['/api/system/ready', '/api/dashboardapp', '/api/server/health', '/api/version']:
            with get(path) as response:
                assert response.status == 200
                assert isinstance(json.load(response), dict)
        with get('/api/system/recovery') as response:
            state = json.load(response)
            assert state['recovery']['isComplete'], state['recovery']
            assert Path(state['db']['path']).resolve().parent == Path(temp).resolve()
        for route, source in [('/app', 'app.min.html'), ('/images', 'images.min.html')]:
            with get(route) as response:
                data = response.read()
                assert data == (OUTPUT / 'Html' / source).read_bytes()
                etag = response.headers['ETag']
                assert etag
            try:
                get(route, {'If-None-Match': etag})
                raise AssertionError('Conditional request must return 304')
            except urllib.error.HTTPError as response:
                assert response.code == 304
                assert response.read() == b''
            with get(route, {'Accept-Encoding': 'gzip'}) as response:
                assert response.headers['Content-Encoding'] == 'gzip'
                compressed = response.read()
                assert gzip.decompress(compressed) == data
                print(f'{route}: {len(data)} bytes; gzip {len(compressed)} bytes; ETag 304 verified')
        for path in ['/static/index.min.css', '/static/animations.min.js']:
            with get(path) as response: assert response.status == 200 and response.read()
        for name in ['nebula_engine.py', 'nebula/__init__.py', 'nebula/probe.py', 'nebula/encoding.py']:
            assert (OUTPUT / 'Python' / name).read_bytes() == (ROOT / 'Python' / name).read_bytes()
        assert not (OUTPUT / 'Keys/private.pem').exists()
        if args.youtube_image_url:
            def post(path, payload):
                return urllib.request.urlopen(urllib.request.Request(base + path,
                    data=json.dumps(payload).encode(), headers={'Content-Type': 'application/json'}), timeout=60)
            with post('/api/images/analyze', {'url': args.youtube_image_url}) as response:
                result = json.load(response)
            assert len(result['images']) == 1, result
            candidate = result['images'][0]
            assert candidate['url'].endswith('=s0'), candidate
            with post('/api/images/download', {'url': candidate['url'], 'sourceUrl': args.youtube_image_url}) as response:
                image_bytes = response.read()
                assert '.jpg' in response.headers['Content-Disposition']
            assert image_bytes.startswith(b'\xff\xd8\xff')
            downloaded = Path(temp) / 'youtube-image.jpg'
            downloaded.write_bytes(image_bytes)
            info = json.loads(subprocess.check_output([str(ROOT / 'ffprobe.exe'), '-v', 'error',
                '-show_entries', 'stream=width,height', '-of', 'json', str(downloaded)]))
            size = info['streams'][0]
            assert max(size['width'], size['height']) > 288
            print(f"PASS: supplied YouTube image analyzed and downloaded: {size['width']}x{size['height']}, {len(image_bytes)} bytes")
        print('PASS: live health/recovery/dashboard/pages/compression/cache validators and Python build assets')
    finally:
        if process.poll() is None:
            process.terminate()
            process.wait(timeout=10)
        log.close()
