"""Bounded, stat-aware media probing shared by validation and processing."""

import json
import math
import subprocess
from dataclasses import dataclass
from functools import lru_cache
from pathlib import Path


@dataclass(frozen=True)
class MediaInfo:
    video_codec: str
    audio_codecs: tuple[str, ...]
    duration: float | None
    has_streams: bool
    video_fps: float | None

    @property
    def audio_is_aac(self) -> bool:
        return all(codec == "aac" for codec in self.audio_codecs)


@lru_cache(maxsize=256)
def _probe_cached(ffprobe: str, path: str, size: int, modified: int, created: int) -> MediaInfo:
    # The stat fields form the cache key. Replaced/modified files are probed again.
    result = subprocess.run(
        [ffprobe, "-v", "error", "-show_entries",
         "format=duration:stream=codec_type,codec_name,r_frame_rate,avg_frame_rate", "-of", "json", path],
        capture_output=True, text=True, timeout=20, check=True,
    )
    data = json.loads(result.stdout)
    streams = data.get("streams") or []
    video = next((str(stream.get("codec_name") or "").lower()
                  for stream in streams if stream.get("codec_type") == "video"), "")
    video_stream = next((stream for stream in streams if stream.get("codec_type") == "video"), {})

    def _rate(value) -> float | None:
        try:
            text = str(value or "")
            if "/" in text:
                numerator, denominator = text.split("/", 1)
                rate = float(numerator) / float(denominator)
            else:
                rate = float(text)
            return rate if math.isfinite(rate) and rate > 0 else None
        except (TypeError, ValueError, ZeroDivisionError):
            return None

    video_fps = _rate(video_stream.get("avg_frame_rate")) or _rate(video_stream.get("r_frame_rate"))
    audio = tuple(str(stream.get("codec_name") or "").lower()
                  for stream in streams if stream.get("codec_type") == "audio")
    try:
        duration = float((data.get("format") or {}).get("duration"))
        if not math.isfinite(duration) or duration <= 0:
            duration = None
    except (TypeError, ValueError):
        duration = None
    return MediaInfo(video, audio, duration, bool(streams), video_fps)


def probe_media(source: str | Path, ffprobe: str) -> MediaInfo | None:
    """Probe local media, without caching failures or retaining unbounded entries."""
    try:
        path = Path(source).resolve()
        stat = path.stat()
        return _probe_cached(ffprobe, str(path), stat.st_size, stat.st_mtime_ns, stat.st_ctime_ns)
    except (OSError, ValueError, TypeError, subprocess.SubprocessError):
        return None
