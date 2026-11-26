"""Optional remote GPU for Demucs vocal extraction (off by default).

Enabled only when NEBULA_REMOTE_DEMUCS_URL is set to an https:// endpoint.
Any failure returns False so the caller falls back to local Demucs.

  NEBULA_REMOTE_DEMUCS_URL      HTTPS endpoint: multipart field "audio" in, PCM WAV (vocals) out
  NEBULA_REMOTE_DEMUCS_TOKEN    optional bearer token for the Authorization header
  NEBULA_REMOTE_DEMUCS_TIMEOUT  seconds to wait for the result (default 300, minimum 10)

See Python/remote_gpu/README.md for the endpoint contract and a Modal example.
"""
import os
import time
import wave
from pathlib import Path


def remote_demucs_config():
    url = os.environ.get("NEBULA_REMOTE_DEMUCS_URL", "").strip()
    if not url or not url.lower().startswith("https://"):
        return None
    token = os.environ.get("NEBULA_REMOTE_DEMUCS_TOKEN", "").strip()
    try:
        timeout = max(10, int(os.environ.get("NEBULA_REMOTE_DEMUCS_TIMEOUT", "300")))
    except ValueError:
        timeout = 300
    return url, token, timeout


def run_remote_demucs(audio_in: Path, vocals_out: Path, log=lambda message: None) -> bool:
    """Return True when vocals_out was written by the remote GPU, otherwise False."""
    config = remote_demucs_config()
    if config is None:
        return False
    url, token, timeout = config
    headers = {"Authorization": f"Bearer {token}"} if token else {}
    tmp_out = vocals_out.with_suffix(".remote.tmp")
    try:
        import requests

        log("Demucs: sending audio to remote GPU...")
        started = time.time()
        with audio_in.open("rb") as handle:
            response = requests.post(
                url,
                files={"audio": (audio_in.name, handle, "application/octet-stream")},
                headers=headers,
                timeout=(15, timeout),
                stream=True,
            )
        with response:
            response.raise_for_status()
            with tmp_out.open("wb") as out:
                for chunk in response.iter_content(chunk_size=1024 * 1024):
                    if chunk:
                        out.write(chunk)
        with wave.open(str(tmp_out), "rb") as check:
            if check.getnframes() <= 0:
                raise RuntimeError("remote GPU returned an empty audio file")
        tmp_out.replace(vocals_out)
        log(f"Demucs: remote GPU finished in {time.time() - started:.1f}s")
        return True
    except Exception as exc:  # any failure falls back to local processing
        log(f"Demucs: remote GPU failed ({type(exc).__name__}: {exc}); using local processing.")
        try:
            tmp_out.unlink()
        except OSError:
            pass
        return False
