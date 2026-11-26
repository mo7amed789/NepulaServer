"""
Nebula Engine - Production Grade Media Processing Engine
Optimized for CPU, RAM, Disk I/O, Network, and Throughput.
"""

import json
import logging
import copy
import os
import re
import shutil
import subprocess
import sys
import threading
import time
import traceback
import hashlib
import time
# Guard against missing package metadata (e.g. if .dist-info was stripped)
try:
    import importlib.metadata as _meta
    _orig_meta = _meta.metadata
    _orig_ver = _meta.version

    def _safe_metadata(name, *args, **kwargs):
        try:
            return _orig_meta(name, *args, **kwargs)
        except Exception:
            if name == "curl_cffi":
                from email.message import Message
                m = Message()
                m["Summary"] = "curl-impersonate python bindings"
                return m
            raise

    def _safe_version(name, *args, **kwargs):
        try:
            return _orig_ver(name, *args, **kwargs)
        except Exception:
            if name == "curl_cffi":
                return "0.15.0"
            raise

    _meta.metadata = _safe_metadata
    _meta.version = _safe_version
except Exception:
    pass

try:
    import curl_cffi
except Exception:
    curl_cffi = None

import html
import requests
import urllib.parse
import zipfile
from concurrent.futures import ThreadPoolExecutor, as_completed
from collections import deque
from nebula.encoding import _encoder_args, get_video_encoder_args
from nebula.remote_gpu import run_remote_demucs
from nebula.probe import probe_media
from datetime import datetime, timezone
from pathlib import Path


# ─── Version flag ─────────────────────────────────────────────────────────────
if "--version" in sys.argv:
    print("Nebula Engine 1.0")
    sys.exit(0)

# ─── Base paths ───────────────────────────────────────────────────────────────
BASE_DIR     = Path(__file__).resolve().parent   # Engine\
APP_BASE_DIR = BASE_DIR.parent                   # app root

# ─── Data directories ─────────────────────────────────────────────────────────
_data_env = os.environ.get("TANZEEL_DATA") or os.environ.get("NEBULA_DATA")
PROGRAM_DATA = Path(_data_env) if _data_env else (
    Path(os.environ.get("ProgramData", "C:/ProgramData")) / "Tanzeel"
)
DOWNLOAD_DIR = PROGRAM_DATA / "Downloads"
TEMP_DIR     = PROGRAM_DATA / "Temp"
LOG_DIR      = PROGRAM_DATA / "Logs"

for _d in (DOWNLOAD_DIR, TEMP_DIR, LOG_DIR):
    _d.mkdir(parents=True, exist_ok=True)

# ─── Models directory ─────────────────────────────────────────────────────────
MODELS_DIR = PROGRAM_DATA / "Models"
MODELS_DIR.mkdir(parents=True, exist_ok=True)
os.environ["TORCH_HOME"] = str(MODELS_DIR)

# ─── ffmpeg binaries ──────────────────────────────────────────────────────────
FFMPEG_BIN  = APP_BASE_DIR / "ffmpeg.exe"
FFPROBE_BIN = APP_BASE_DIR / "ffprobe.exe"
os.environ["FFMPEG_BINARY"]  = str(FFMPEG_BIN)
os.environ["FFPROBE_BINARY"] = str(FFPROBE_BIN)
os.environ["PATH"] = str(APP_BASE_DIR) + os.pathsep + os.environ.get("PATH", "")

# ─── UTF-8 stdout/stderr (force raw UTF-8 streams, not just reconfigure) ──────
try:
    sys.stdout = open(sys.stdout.fileno(), mode="w", encoding="utf-8", buffering=1)
    sys.stderr = open(sys.stderr.fileno(), mode="w", encoding="utf-8", buffering=1)
except Exception:
    if sys.stdout.encoding and sys.stdout.encoding.lower() != "utf-8":
        try:
            sys.stdout.reconfigure(encoding="utf-8")
            sys.stderr.reconfigure(encoding="utf-8")
        except AttributeError:
            pass

# ─── Runtime imports ──────────────────────────────────────────────────────────
# AI/audio-separation dependencies are loaded only when ExtractVocals is used.
# Importing PyTorch, NumPy, SoundFile, and Demucs delayed every normal FFmpeg job.
import yt_dlp
from yt_dlp.utils import download_range_func

sf = None
torch = None
demucs_apply = None
demucs_pretrained = None
convert_audio = None
_AI_DEPENDENCIES_LOCK = threading.Lock()


def _ensure_ai_dependencies() -> None:
    global sf, torch, demucs_apply, demucs_pretrained, convert_audio
    global _DEMUCS_MAX_CONCURRENT, _DEMUCS_INFERENCE_SEM
    if torch is not None:
        return
    with _AI_DEPENDENCIES_LOCK:
        if torch is not None:
            return

        import soundfile as loaded_soundfile
        import torch as loaded_torch
        from demucs import apply as loaded_apply, pretrained as loaded_pretrained
        from demucs.audio import convert_audio as loaded_convert_audio

        sf = loaded_soundfile
        torch = loaded_torch
        demucs_apply = loaded_apply
        demucs_pretrained = loaded_pretrained
        convert_audio = loaded_convert_audio

        try:
            torch.backends.cudnn.benchmark = True
            torch.backends.cuda.matmul.allow_tf32 = True
            torch.backends.cudnn.allow_tf32 = True
        except Exception:
            pass
        try:
            torch.set_num_threads(os.cpu_count() or 4)
        except Exception:
            pass

        _DEMUCS_MAX_CONCURRENT = (
            1 if torch.cuda.is_available() else max(1, (os.cpu_count() or 4) // 4)
        )
        _DEMUCS_INFERENCE_SEM = threading.Semaphore(_DEMUCS_MAX_CONCURRENT)

_DEMUCS_USE_HALF = True

# ─── Domains helpers ─────────────────────────────────────────────────────────
_FACEBOOK_DOMAINS = ("facebook.com", "fb.com", "fb.watch", "m.facebook.com")

def _is_facebook_url(url: str) -> bool:
    return any(d in url.lower() for d in _FACEBOOK_DOMAINS)

_INSTAGRAM_DOMAINS = ("instagram.com", "instagr.am")

def _is_instagram_url(url: str) -> bool:
    return any(d in url.lower() for d in _INSTAGRAM_DOMAINS)

def _normalize_instagram_url(url: str) -> str:
    if not url:
        return url
    url = re.sub(r"^https?://(?:www\.)?instagr\.am/", "https://www.instagram.com/", url, flags=re.I)
    url = re.sub(r"/share/(reels?|p|tv)/([^/?#&]+)", r"/\1/\2", url, flags=re.I)
    url = re.sub(r"/reels/([^/?#&]+)", r"/reel/\1", url, flags=re.I)
    if "instagram.com" in url.lower():
        if "?" in url and ("igsh=" in url.lower() or "utm_" in url.lower()):
            url = url.split("?", 1)[0]
        if re.search(r"/(?:p|tv|reel)/[^/?#]+$", url):
            url += "/"
    return url

# Missing function added here:
def _is_pornhub_url(url: str) -> bool:
    return "pornhub.com" in url.lower() or "pornhubpremium.com" in url.lower()

# ─── Adult content filtering ──────────────────────────────────────────────────
_ADULT_DOMAINS = (
    "pornhub.com", "phncdn.com", "pornhubpremium.com", "redtube.com", "youporn.com",
    "tube8.com", "brazzers.com", "realitykings.com", "bangbros.com", "naughtyamerica.com",
    "digitalplayground.com", "twistys.com", "xvideos.com", "xvideos2.com", "xvideos3.com",
    "xnxx.com", "xnxx2.com", "xnxx3.com", "xhamster.com", "xhamster2.com", "xhamster.desi",
    "spankbang.com", "beeg.com", "eporner.com", "daftsex.com", "hqporner.com",
    "thumbzilla.com", "tnaflix.com", "motherless.com", "heavy-r.com", "fuq.com",
    "cliphunter.com", "xtube.com", "youjizz.com", "vporn.com", "txxx.com", "porn300.com",
    "drtuber.com", "nuvid.com", "upornia.com", "hclips.com", "hdzog.com", "4tube.com",
    "porntrex.com", "pornhat.com", "chaturbate.com", "stripchat.com", "bongacams.com",
    "livejasmin.com", "cam4.com", "camwhores.tv", "camwhores.video", "myfreecams.com",
    "camsoda.com", "flirt4free.com", "onlyfans.com", "fansly.com", "manyvids.com",
    "clips4sale.com", "loyalfans.com", "erome.com", "coomer.party", "coomer.su",
    "kemono.party", "kemono.su", "rule34.xxx", "rule34.paheal.net", "rule34video.com",
    "rule34world.com", "e-hentai.org", "nhentai.net", "hentaihaven.xxx", "hanime.tv",
    "fakku.net", "gelbooru.com", "danbooru.donmai.us", "luscious.net", "hentai-foundry.com",
    "xbooru.com", "hypnohub.net", "realbooru.com", "javhd.com", "javlibrary.com",
    "avgle.com", "missav.com", "jable.tv", "7mmtv.tv"
)

_ADULT_KEYWORDS = (
    "porn", "xxx", "xvideos", "xnxx", "xhamster", "redtube", "youporn",
    "spankbang", "beeg", "chaturbate", "stripchat", "bongacams", "livejasmin",
    "onlyfans", "fansly", "erome", "rule34", "nhentai", "hentai", "eporner",
    "tnaflix", "motherless", "thumbzilla", "brazzers", "camwhores", "sexvid",
    "tube8", "youjizz", "cam4"
)

def _is_adult_url(url: str) -> bool:
    if not url:
        return False
    try:
        parsed = urllib.parse.urlparse(url.strip())
        host = (parsed.hostname or parsed.netloc or "").lower()
    except Exception:
        host = url.lower()

    if not host:
        host = url.lower()

    if host.startswith("www."):
        host = host[4:]
    if host.startswith("m."):
        host = host[2:]

    for d in _ADULT_DOMAINS:
        if host == d or host.endswith("." + d):
            return True

    if host.endswith((".xxx", ".porn", ".adult", ".sex")):
        return True

    parts = host.split(".")
    for part in parts:
        for kw in _ADULT_KEYWORDS:
            if kw in part:
                return True

    return False


# =============================================================================
# OPTIMIZATION 1: Buffered logging
# =============================================================================

_log_handler: logging.FileHandler | None = None
_logger = logging.getLogger("nebula")

def _init_logger(pid: int) -> None:
    global _log_handler
    log_path = LOG_DIR / f"engine_{pid}.log"
    _log_handler = logging.FileHandler(log_path, encoding="utf-8")
    _log_handler.setFormatter(
        logging.Formatter("%(asctime)s [%(levelname)s] %(message)s",
                          datefmt="%Y-%m-%dT%H:%M:%S")
    )
    _logger.addHandler(_log_handler)
    _logger.setLevel(logging.DEBUG)

_init_logger(os.getpid())

def log(message: str, job_id: str = None, level: str = "INFO") -> None:
    prefix = f"[{job_id}] " if job_id else ""
    lvl = level.lower()
    if lvl == "warn":
        lvl = "warning"
    getattr(_logger, lvl, _logger.info)(prefix + message)


# =============================================================================
# IPC — messaging to C# host
# =============================================================================

def _safe(text: str) -> str:
    return str(text).replace("\r", " ").replace("\n", " ").replace("|", "/")

def send_progress(
    job_id: str,
    progress: str,
    speed: str,
    eta: str,
    status: str = "DOWNLOADING",
    downloaded: int = 0,
    total: int = 0,
) -> None:
    # Keep this protocol aligned with JobQueueManager.ParseEngineMessageAsync.
    # Numeric fields make download and FFmpeg progress render identically.
    print(
        f"JOB_PROGRESS|{job_id}|{status}|{progress}|{downloaded}|{total}|{speed}|{eta}",
        flush=True,
    )

def send_stage(job_id: str, stage: str, message: str = "") -> None:
    payload = f"JOB_STAGE|{job_id}|{stage}"
    if message:
        payload += f"|{_safe(message)}"
    print(payload, flush=True)

def send_result(job_id: str, file_name: str, original_title: str = "") -> None:
    print(f"JOB_RESULT|{job_id}|{file_name}|{_safe(original_title)}", flush=True)

def send_thumbnail(job_id: str, url: str) -> None:
    print(f"JOB_THUMBNAIL|{job_id}|{url}", flush=True)


# =============================================================================
# Sanitization
# =============================================================================

def sanitize_job_id(job_id: str) -> str:
    if not job_id:
        raise ValueError("Job ID cannot be empty.")
    cleaned = "".join(c for c in str(job_id) if c.isalnum() or c in ("-", "_"))
    if not cleaned:
        raise ValueError(f"Job ID contains no valid characters: {job_id!r}")
    return cleaned


def sanitize_filename(value: str, max_len: int = 180) -> str:
    if not value:
        return "media"

    WIN_FORBIDDEN = r'<>:"/\\|?*'
    cleaned = "".join(
        "_" if c in WIN_FORBIDDEN else c
        for c in value if ord(c) >= 32
    )
    cleaned = cleaned[:max_len].strip(" .")
    return cleaned or "media"


_GENERIC_TITLES = ("video", "na", "untitled", "unknown", "")


def _is_generic_title(title: str) -> bool:
    return title.strip().lower() in _GENERIC_TITLES


def _resolve_entry_title(entry: dict, fallback_entry: dict | None = None) -> str:
    fallback_entry = fallback_entry or {}
    title = entry.get("title") or fallback_entry.get("title") or "media"

    if _is_generic_title(title):
        uploader = entry.get("uploader") or fallback_entry.get("uploader")
        vid_id   = entry.get("id") or fallback_entry.get("id")
        if uploader and vid_id:
            title = f"{uploader} - {vid_id}"
        elif vid_id:
            title = vid_id

    return title.strip()


# =============================================================================
# Job loading
# =============================================================================

def load_job(path: str) -> dict:
    with open(path, "r", encoding="utf-8") as fh:
        return json.load(fh)


# =============================================================================
# Environment
# =============================================================================

def detect_ffmpeg() -> str:
    if FFMPEG_BIN.exists():
        return str(FFMPEG_BIN)
    system = shutil.which("ffmpeg")
    if system:
        return system
    raise FileNotFoundError(f"ffmpeg.exe not found. Place it in: {APP_BASE_DIR}")


def configure_node_path() -> None:
    embedded = APP_BASE_DIR / "Node" / "node.exe"
    if embedded.exists():
        os.environ["PATH"] = str(embedded.parent) + os.pathsep + os.environ.get("PATH", "")
        return
    for cand in [
        Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "nodejs" / "node.exe",
        Path(os.environ.get("ProgramFiles(x86)", "C:/Program Files (x86)")) / "nodejs" / "node.exe",
        Path(os.environ.get("LOCALAPPDATA", "")) / "Programs" / "nodejs" / "node.exe",
    ]:
        if cand.exists():
            os.environ["PATH"] = str(cand.parent) + os.pathsep + os.environ.get("PATH", "")
            return
    if shutil.which("node"):
        return
    log("Node.js not found. Some yt-dlp extractors may not work.", level="WARN")


# =============================================================================
# Cookies & Facebook helpers
# =============================================================================

def _cookies_file_path() -> Path | None:
    search_paths = [
        BASE_DIR / "cookies.txt",
        APP_BASE_DIR / "cookies.txt",
        PROGRAM_DATA / "cookies.txt",
    ]
    for p in search_paths:
        if p.exists() and p.stat().st_size > 0:
            return p
    return None


def _detect_installed_browsers() -> list[str]:
    local_app_data = os.environ.get("LOCALAPPDATA", "")
    app_data = os.environ.get("APPDATA", "")
    browsers = []
    if local_app_data and os.path.exists(os.path.join(local_app_data, "Microsoft", "Edge", "User Data")):
        browsers.append("edge")
    if local_app_data and os.path.exists(os.path.join(local_app_data, "Google", "Chrome", "User Data")):
        browsers.append("chrome")
    if local_app_data and os.path.exists(os.path.join(local_app_data, "BraveSoftware", "Brave-Browser", "User Data")):
        browsers.append("brave")
    if app_data and os.path.exists(os.path.join(app_data, "Mozilla", "Firefox", "Profiles")):
        browsers.append("firefox")
    return browsers


def _browser_cookies_source() -> list[str]:
    configured = os.environ.get("NEBULA_YTDLP_BROWSER_COOKIES", "").strip()
    if configured.lower() in {"0", "false", "off", "no", "none"}:
        return []
    if configured:
        return [configured]
    return _detect_installed_browsers()


def _request_proxy(request: dict) -> str | None:
    for key in ("Proxy", "ProxyUrl", "HttpProxy", "HttpsProxy"):
        value = request.get(key)
        if value is None:
            continue
        text = str(value).strip()
        if text:
            return text
    return None


def _apply_network_identity(opts: dict, request: dict, job_id: str | None = None) -> dict:
    proxy = _request_proxy(request)
    if proxy:
        opts["proxy"] = proxy
        if job_id:
            log(f"Using proxy: {proxy}", job_id)
    else:
        # yt-dlp otherwise inherits the machine's WinHTTP/urllib proxy. On
        # some Windows installations that proxy is stale or malformed and
        # produces "Tunnel connection failed: 400 No host specified" even
        # though the user did not configure a proxy in Tanzeel. An empty
        # yt-dlp proxy explicitly means direct connection.
        opts["proxy"] = ""

    if _has_curl_cffi():
        opts["http_headers"] = {
            "User-Agent": (
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
                "AppleWebKit/537.36 (KHTML, like Gecko) "
                "Chrome/138.0.0.0 Safari/537.36"
            ),
            "Accept-Language": "en-US,en;q=0.9",
        }

    return opts


def _build_cookie_variants(base_opts: dict, job_id: str) -> list[tuple[str, dict]]:
    variants: list[tuple[str, dict]] = []

    cookies = _cookies_file_path()
    if cookies:
        with_file = copy.deepcopy(base_opts)
        with_file["cookiefile"] = str(cookies)
        variants.append(("cookies file", with_file))
        log(f"Using cookies file: {cookies}", job_id)

    browsers = _browser_cookies_source()
    for b in browsers:
        with_b = copy.deepcopy(base_opts)
        with_b["cookiesfrombrowser"] = (b,)
        variants.append((f"browser cookies ({b})", with_b))

    variants.append(("anonymous", copy.deepcopy(base_opts)))
    return variants


def _has_curl_cffi() -> bool:
    try:
        import curl_cffi  # noqa: F401
        return True
    except Exception:
        return False


def _facebook_ydl_opts(base: dict) -> dict:
    extra: dict = {
        "extractor_args": {
            "facebook": {
                "player_client": ["web"],
            }
        }
    }

    extra["http_headers"] = {
        "User-Agent": (
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
            "AppleWebKit/537.36 (KHTML, like Gecko) "
            "Chrome/138.0.0.0 Safari/537.36"
        ),
        "Accept-Language": "en-US,en;q=0.9",
    }

    return {**base, **extra}


def _instagram_ydl_opts(base: dict) -> dict:
    extra: dict = {
        "http_headers": {
            "User-Agent": (
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
                "AppleWebKit/537.36 (KHTML, like Gecko) "
                "Chrome/138.0.0.0 Safari/537.36"
            ),
            "Accept-Language": "en-US,en;q=0.9",
            "Sec-Fetch-Mode": "navigate",
        }
    }
    return {**base, **extra}

# Missing function added here:
def _pornhub_ydl_opts(base: dict) -> dict:
    extra: dict = {}
    # يمكن إضافة أي إعدادات إضافية هنا إن تطلب الأمر لمواقع مثل pornhub
    return {**base, **extra}


# =============================================================================
# 🚀 PERFORMANCE: Measured video encoder selection with safe CPU fallback
# =============================================================================

def _get_video_encoder_args(ffmpeg: str, fast_mode: bool = False) -> list:
    return get_video_encoder_args(ffmpeg, fast_mode, log)


# =============================================================================
# OPTIMIZATION 2 & 3: Single yt-dlp call via native API + progress hooks
# =============================================================================

_RE_PCT   = re.compile(r"([\d.]+)%")
_RE_SPEED = re.compile(r"at\s+([^\s]+)")
_RE_ETA   = re.compile(r"ETA\s+([^\s]+)")


def _make_progress_hook(job_id: str):
    def hook(d: dict) -> None:
        status = d.get("status")
        if status == "downloading":
            total      = d.get("total_bytes") or d.get("total_bytes_estimate") or 0
            downloaded = d.get("downloaded_bytes") or 0
            pct        = f"{downloaded / total * 100:.1f}%" if total > 0 else "0%"
            # Keep the engine protocol machine-readable. The C# host formats
            # these values for the UI; yt-dlp display strings such as
            # "1.2MiB/s" and "00:34" would make numeric parsing fail and
            # discard the complete progress update, including ETA.
            raw_speed  = d.get("speed")
            speed      = f"{float(raw_speed):.0f}" if isinstance(raw_speed, (int, float)) and raw_speed >= 0 else "0"
            raw_eta    = d.get("eta")
            if isinstance(raw_eta, (int, float)) and raw_eta >= 0:
                eta = str(max(0, int(raw_eta)))
            elif total > downloaded and isinstance(raw_speed, (int, float)) and raw_speed > 0:
                # Some extractors omit yt-dlp's ETA. Calculate a useful
                # fallback from the current transfer rate instead of hiding
                # the ETA from the UI.
                eta = str(max(0, int((total - downloaded) / raw_speed)))
            else:
                eta = "-1"
            send_progress(job_id, pct, speed, eta, "DOWNLOADING", downloaded, total)
        elif status == "finished":
            send_stage(job_id, "PROCESSING", "Download finished.")
    return hook


def _parse_quality_height(value) -> int | None:
    """Return the requested video height as an int, or None when uncapped.

    The UI sends labels such as "1080", "720p", "Original", "best" or "audio".
    Only a concrete height may constrain the format selector: embedding a
    non-numeric label (e.g. ``bestvideo[height<=Original]``) produces an
    invalid yt-dlp filter that aborts the whole download.
    """
    text = str(value or "").strip()
    if not text:
        return None
    match = re.match(r"^(\d{3,4})", text)
    return int(match.group(1)) if match else None


def _parse_quality_fps(value) -> int | None:
    """Return an optional requested frame-rate preference from a UI label."""
    match = re.search(r"(?:·|@|\s)(50|60)\s*fps\b", str(value or "").lower())
    return int(match.group(1)) if match else None


def _make_ytdlp_opts(
    job_id     : str,
    request    : dict,
    raw_dir    : Path,
    ffmpeg     : str,
    is_playlist: bool,
) -> dict:
    fmt     = str(request.get("Format", "mp4")).lower()
    quality = str(request.get("Quality", "720") or "720")
    fps     = _parse_quality_fps(quality)
    data_saver = bool(request.get("DataSaverMode"))

    if is_playlist:
        outtmpl = str(raw_dir / "%(playlist_index)02d_%(id)s.%(ext)s")
    else:
        outtmpl = str(raw_dir / "%(id)s.%(ext)s")

    opts: dict = {
        "ffmpeg_location": ffmpeg,
        "outtmpl": outtmpl,
        "windowsfilenames": True,
        "trim_filenames": 180,
        "retries": 5,
        "fragment_retries": 5,
        "continuedl": True,
        "overwrites": False,
        "file_access_retries": 3,
        # Keep incomplete transfers in a .part file.  With nopart=True an
        # interrupted request can leave a tiny, apparently-complete MP4 that
        # is then picked up by the processing pipeline.
        "nopart": False,
        "noprogress": True,
        "progress_hooks": [_make_progress_hook(job_id)],
        "quiet": True,
        "no_warnings": False,
        "noplaylist": not is_playlist,
        # Parallel fragments improve throughput on fast connections. Keep
        # both values configurable for slower networks and provider limits.
        # Ultra-performance defaults: DASH fragments are independent, so
        # parallel requests and a larger chunk window reduce wall-clock time
        # without changing the selected stream or output quality.
        "concurrent_fragment_downloads": (
            max(1, min(48, int(os.environ.get("NEBULA_FRAGMENT_DOWNLOADS", "32"))))
            if not data_saver else 4
        ),
        "http_chunk_size": (
            max(0, int(os.environ.get("NEBULA_HTTP_CHUNK_SIZE", str(16 * 1024 * 1024))))
            if not data_saver else 0
        ),
        "buffersize": 1024 * 1024 if not data_saver else 256 * 1024,
        "js_runtimes": {"node": {}},
        "remote_components": ["ejs:github"],
        "extractor_args": {
            "youtube": {
                # VisionOS currently exposes YouTube's direct 50/60 FPS
                # formats reliably. Android/iOS clients can advertise SABR
                # entries without URLs, causing valid HD formats to appear
                # unavailable to yt-dlp.
                "player_client": ["visionos", "web"],
            }
        },
    }

    opts = _apply_network_identity(opts, request, job_id)

    # ── Segment: download ONLY the requested time range ─────────────────────
    if not is_playlist and has_segment(request):
        start = parse_time(request.get("SegmentStart")) or 0.0
        end   = parse_time(request.get("SegmentEnd"))
        effective_end = end if end is not None else float("inf")
        opts["download_ranges"] = download_range_func(None, [(start, effective_end)])
        # A trim must be frame accurate at both boundaries. yt-dlp's external
        # FFmpeg downloader otherwise adds `-c copy`, which seeks to the nearest
        # keyframe and leaks frames before the requested start. FastMode still
        # uses the faster encoding preset below, but never sacrifices cut
        # accuracy.
        opts["force_keyframes_at_cuts"] = True
        log(f"Restricting download to segment {start}-{'end of video' if end is None else effective_end} seconds.", job_id)

    # ── Playlist range ────────────────────────────────────────────────────────
    if is_playlist:
        if request.get("PlaylistStart"):
            opts["playliststart"] = int(request["PlaylistStart"])
        if request.get("PlaylistEnd"):
            opts["playlistend"] = int(request["PlaylistEnd"])

    # ── Format ────────────────────────────────────────────────────────────────
    if fmt == "mp3":
        audio_quality = str(request.get("AudioQuality", "128") or "128")
        if audio_quality not in {"96", "128", "160", "192", "320"}:
            audio_quality = "128"
        opts["format"]         = "bestaudio/best"
        opts["postprocessors"] = [{
            "key"             : "FFmpegExtractAudio",
            "preferredcodec"  : "mp3",
            "preferredquality": audio_quality,
        }]
    else:
        height = _parse_quality_height(quality)
        if data_saver:
            # Data Saver is a real bandwidth policy: cap video at 480p and
            # prefer a <=96 kbps audio stream. Explicit high-FPS selections
            # remain respected when that source exists.
            height = min(height or 480, 480)
            log("Data Saver enabled: capping video at 480p and audio at 96 kbps.", job_id)
        if height:
            # Keep every fallback capped. An uncapped fallback would let
            # yt-dlp return a higher stream when a site exposes only progressive
            # (audio+video) formats for the requested range.
            #
            # The first branch prefers separate video/audio streams. The second
            # handles muxed streams, which are common on Facebook, TikTok, and
            # Twitter/X. "720" must never unexpectedly become 1080p or 4K.
            # Landscape services describe resolution by height, while TikTok,
            # Reels, and Shorts commonly expose portrait streams such as
            # 576x1024. For portrait media the width is the comparable quality
            # dimension. Prefer a strict cap on either dimension, then use the
            # smallest real video only when a platform exposes no stream under
            # the requested cap (otherwise 480p TikToks fail outright).
            fps_filter = f"[fps>={fps}]" if fps else ""
            # Prefer the requested 50/60 fps stream at the chosen HD height.
            # High-frame-rate presets intentionally do not fall back to 30 fps;
            # yt-dlp will report a clear unavailable-format error instead.
            audio_selector = "bestaudio[abr<=96]" if data_saver else "bestaudio"
            opts["format"] = (
                f"bestvideo[height<={height}]{fps_filter}+{audio_selector}/"
                f"best[height<={height}]{fps_filter}/"
                f"bestvideo[width<={height}]{fps_filter}+{audio_selector}/"
                f"best[width<={height}]{fps_filter}/"
                + (
                    f"bestvideo[height<={height}]+{audio_selector}/"
                    f"best[height<={height}]/"
                    f"bestvideo[width<={height}]+{audio_selector}/"
                    f"best[width<={height}]/"
                    f"worstvideo[width>0]+bestaudio/"
                    f"worst[width>0]"
                    if not fps else ""
                )
            )
        else:
            # "best" / "Original" / unknown → best available, no height cap.
            opts["format"] = "bestvideo+bestaudio/best"
        opts["merge_output_format"] = "mp4"

    return opts


def _is_valid_media_file(path: Path) -> bool:
    """Reject zero-byte, partial, or otherwise invalid media outputs."""
    try:
        if not path.is_file() or path.stat().st_size < 1024:
            return False

        media_exts = {".mp4", ".mp3", ".m4a", ".webm", ".mkv", ".wav", ".mov", ".avi"}
        if path.suffix.lower() not in media_exts:
            return True

        media = _probe_media(path)
        return media is not None and media.has_streams and media.duration is not None
    except Exception:
        return False


def _download_resumable_http(
    url: str,
    out_file: Path,
    request_kwargs: dict,
    job_id: str,
    label: str,
) -> None:
    """Download to .part and resume it with HTTP Range when supported."""
    part_file = out_file.with_suffix(out_file.suffix + ".part")
    part_file.parent.mkdir(parents=True, exist_ok=True)
    last_error: Exception | None = None

    for attempt in range(1, 6):
        offset = part_file.stat().st_size if part_file.exists() else 0
        kwargs = dict(request_kwargs)
        headers = dict(kwargs.get("headers") or {})
        if offset:
            headers["Range"] = f"bytes={offset}-"
        kwargs["headers"] = headers

        try:
            with requests.get(url, **kwargs) as response:
                response.raise_for_status()
                append = offset > 0 and response.status_code == 206
                if offset and not append:
                    offset = 0
                mode = "ab" if append else "wb"
                total = int(response.headers.get("content-length", 0)) + offset
                downloaded = offset
                with part_file.open(mode) as stream:
                    for chunk in response.iter_content(chunk_size=1024 * 1024):
                        if chunk:
                            stream.write(chunk)
                            downloaded += len(chunk)
                            if total:
                                send_progress(job_id, f"{downloaded / total * 100:.1f}%", "", "")
            os.replace(part_file, out_file)
            return
        except (requests.RequestException, OSError) as exc:
            last_error = exc
            log(
                f"{label} attempt {attempt}/5 failed; partial data preserved at {part_file}: {exc}",
                job_id,
                level="WARN",
            )
            if attempt < 5:
                time.sleep(min(2 ** (attempt - 1), 8))

    raise RuntimeError(f"{label} failed after retries; partial data was preserved.") from last_error


def _collect_files(raw_dir: Path, ext: str, validate: bool = False) -> list:
    matches = sorted(raw_dir.glob(f"*.{ext}"))
    if not matches:
        matches = sorted(raw_dir.rglob(f"*.{ext}"))

    fallback_exts = {
        ".mp4", ".mp3", ".m4a", ".webm", ".mkv", ".wav",
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".zip"
    }
    if not matches:
        matches = sorted(
            p for p in raw_dir.rglob("*")
            if p.is_file() and p.suffix.lower() in fallback_exts
        )

    if validate:
        matches = [p for p in matches if _is_valid_media_file(p)]
    return matches


def _mux_orphan_audio(raw_dir: Path, ffmpeg: str, job_id: str) -> None:
    """Recover audio left beside a video when a segmented merge was incomplete."""
    videos = [p for p in raw_dir.rglob("*.mp4") if p.is_file()]
    audios = [p for p in raw_dir.rglob("*") if p.is_file() and p.suffix.lower() in {".m4a", ".webm", ".opus", ".mp3"}]
    if not videos or not audios:
        return
    for video in videos:
        media = _probe_media(video)
        if media and media.audio_codecs:
            continue
        audio = min(audios, key=lambda p: abs(p.stat().st_size - video.stat().st_size))
        merged = video.with_name(f"{video.stem}_with_audio.mp4")
        run_command([ffmpeg, "-y", "-i", str(video), "-i", str(audio),
                     "-map", "0:v:0", "-map", "1:a:0", "-c:v", "copy",
                     "-c:a", "aac", "-b:a", "192k", "-shortest", "-movflags", "+faststart",
                     str(merged)], job_id, "ffmpeg recover audio")
        if merged.exists() and merged.stat().st_size > 0:
            video.unlink(missing_ok=True)
            merged.rename(video)
            log(f"Recovered audio track from {audio.name}.", job_id)
            return


def _download_instagram_fallback(
    url: str, raw_dir: Path, job_id: str, proxy: str | None = None
) -> dict | None:
    headers = {
        "User-Agent": "Mozilla/5.0 (iPhone; CPU iPhone OS 16_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.6 Mobile/15E148 Safari/604.1",
        "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
        "Accept-Language": "en-US,en;q=0.9",
    }
    try:
        log(f"Attempting Instagram mobile web fallback: {url}", job_id)
        send_stage(job_id, "DOWNLOADING", "Downloading Instagram media...")
        request_kwargs = {"headers": headers, "timeout": 20}
        if proxy:
            request_kwargs["proxies"] = {"http": proxy, "https": proxy}
        resp = requests.get(url, **request_kwargs)
        if resp.status_code != 200:
            log(f"Instagram fallback HTTP status: {resp.status_code}", job_id, level="WARN")
            return None

        page_html = resp.text

        # 1. Extract title
        title = "Instagram Media"
        title_matches = re.findall(r'<meta\s+(?:property|name)=["\']og:title["\']\s+content=["\']([^"\']+)["\']', page_html)
        if not title_matches:
            title_matches = re.findall(r'content=["\']([^"\']+)["\']\s+(?:property|name)=["\']og:title["\']', page_html)
        if not title_matches:
            title_matches = re.findall(r'<title>([^<]+)</title>', page_html)
        if title_matches:
            raw_title = html.unescape(title_matches[0]).strip()
            if " on Instagram: " in raw_title:
                parts = raw_title.split(" on Instagram: ", 1)
                raw_title = parts[1].strip(' "\'') if len(parts) > 1 else raw_title
            title = sanitize_filename(raw_title) or "Instagram Media"

        # 2. Check for video
        video_matches = re.findall(r'<meta\s+(?:property|name)=["\']og:video(?::secure_url)?["\']\s+content=["\']([^"\']+)["\']', page_html)
        if not video_matches:
            video_matches = re.findall(r'content=["\']([^"\']+)["\']\s+(?:property|name)=["\']og:video(?::secure_url)?["\']', page_html)

        if video_matches:
            vid_url = html.unescape(video_matches[0])
            log(f"Instagram fallback found video URL: {vid_url[:80]}", job_id)
            out_file = raw_dir / f"{title}.mp4"
            request_kwargs = {"headers": headers, "stream": True, "timeout": 30}
            if proxy:
                request_kwargs["proxies"] = {"http": proxy, "https": proxy}
            _download_resumable_http(vid_url, out_file, request_kwargs, job_id, "Instagram video")
            if not _is_valid_media_file(out_file):
                out_file.unlink(missing_ok=True)
                raise RuntimeError("Instagram fallback produced an invalid media file")
            img_matches = re.findall(r'<meta\s+(?:property|name)=["\']og:image(?::secure_url)?["\']\s+content=["\']([^"\']+)["\']', page_html)
            if img_matches:
                send_thumbnail(job_id, html.unescape(img_matches[0]))
            return {
                "files": [out_file],
                "extension": "mp4",
                "title": title,
                "entry_titles": [title],
            }

        # 3. Check for image
        img_matches = re.findall(r'<meta\s+(?:property|name)=["\']og:image(?::secure_url)?["\']\s+content=["\']([^"\']+)["\']', page_html)
        if not img_matches:
            img_matches = re.findall(r'content=["\']([^"\']+)["\']\s+(?:property|name)=["\']og:image(?::secure_url)?["\']', page_html)

        if img_matches:
            img_url = html.unescape(img_matches[0])
            log(f"Instagram fallback found image URL: {img_url[:80]}", job_id)
            out_file = raw_dir / f"{title}.jpg"
            request_kwargs = {"headers": headers, "stream": True, "timeout": 30}
            if proxy:
                request_kwargs["proxies"] = {"http": proxy, "https": proxy}
            _download_resumable_http(img_url, out_file, request_kwargs, job_id, "Instagram image")
            send_thumbnail(job_id, img_url)
            send_progress(job_id, "100%", "", "")
            return {
                "files": [out_file],
                "extension": "jpg",
                "title": title,
                "entry_titles": [title],
            }

        log("Instagram fallback: No media found in HTML", job_id, level="WARN")
    except Exception as exc:
        log(f"Instagram fallback exception: {exc}", job_id, level="WARN")
    return None


def _download_gallery_dl_fallback(
    url: str, raw_dir: Path, job_id: str, proxy: str | None = None
) -> dict | None:
    try:
        log(f"Attempting gallery-dl fallback: {url}", job_id)
        send_stage(job_id, "DOWNLOADING", "Trying gallery-dl fallback...")
        gdl_dir = raw_dir / "gallery_dl"
        gdl_dir.mkdir(parents=True, exist_ok=True)
        cmd = [
            sys.executable, "-m", "gallery_dl",
            "--dest", str(gdl_dir),
        ]
        if proxy:
            cmd.extend(["--proxy", proxy])
        cmd.append(url)
        res = subprocess.run(cmd, capture_output=True, text=True, timeout=60)
        log(f"gallery-dl exit code: {res.returncode}", job_id)
        found_files = [p for p in gdl_dir.rglob("*") if p.is_file() and p.stat().st_size > 0]
        if found_files:
            log(f"gallery-dl downloaded {len(found_files)} files.", job_id)
            title = sanitize_filename(found_files[0].stem)
            if len(found_files) == 1:
                f = found_files[0]
                ext = f.suffix.lower().lstrip(".")
                return {
                    "files": [f],
                    "extension": ext,
                    "title": title,
                    "entry_titles": [title],
                }
            else:
                zip_path = raw_dir / f"{title}.zip"
                with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
                    for f in found_files:
                        zf.write(f, f.name)
                return {
                    "files": [zip_path],
                    "extension": "zip",
                    "title": title,
                    "entry_titles": [f.stem for f in found_files],
                }
    except Exception as exc:
        log(f"gallery-dl fallback error: {exc}", job_id, level="WARN")
    return None


def _download_direct_media_fallback(
    url: str, raw_dir: Path, job_id: str, proxy: str | None = None
) -> dict | None:
    try:
        parsed = urllib.parse.urlparse(url)
        path_lower = parsed.path.lower()
        known_exts = {".mp4", ".mp3", ".m4a", ".webm", ".mkv", ".wav", ".jpg", ".jpeg", ".png", ".webp", ".gif"}
        ext_match = None
        for e in known_exts:
            if path_lower.endswith(e):
                ext_match = e
                break

        if not ext_match:
            try:
                request_kwargs = {"allow_redirects": True, "timeout": 10}
                if proxy:
                    request_kwargs["proxies"] = {"http": proxy, "https": proxy}
                head_resp = requests.head(url, **request_kwargs)
                ct = head_resp.headers.get("content-type", "").lower()
                if "image/jpeg" in ct: ext_match = ".jpg"
                elif "image/png" in ct: ext_match = ".png"
                elif "image/webp" in ct: ext_match = ".webp"
                elif "video/mp4" in ct: ext_match = ".mp4"
                elif "audio/mpeg" in ct: ext_match = ".mp3"
            except Exception:
                pass

        if ext_match:
            title = sanitize_filename(Path(parsed.path).stem or "media")
            out_file = raw_dir / f"{title}{ext_match}"
            send_stage(job_id, "DOWNLOADING", "Downloading direct media...")
            request_kwargs = {"stream": True, "timeout": 30}
            if proxy:
                request_kwargs["proxies"] = {"http": proxy, "https": proxy}
            _download_resumable_http(url, out_file, request_kwargs, job_id, "Direct media")
            if not _is_valid_media_file(out_file):
                out_file.unlink(missing_ok=True)
                raise RuntimeError("Direct fallback produced an invalid media file")
            return {
                "files": [out_file],
                "extension": ext_match.lstrip("."),
                "title": title,
                "entry_titles": [title],
            }
    except Exception as exc:
        log(f"Direct media fallback error: {exc}", job_id, level="WARN")
    return None


def _try_media_fallback(url: str, raw_dir: Path, request: dict, ffmpeg: str, job_id: str) -> dict | None:
    proxy = _request_proxy(request)
    if _is_instagram_url(url):
        res = _download_instagram_fallback(url, raw_dir, job_id, proxy)
        if res:
            return res

    res = _download_gallery_dl_fallback(url, raw_dir, job_id, proxy)
    if res:
        return res

    res = _download_direct_media_fallback(url, raw_dir, job_id, proxy)
    if res:
        return res

    return None


def download_media(job_id: str, request: dict, ffmpeg: str, raw_dir: Path) -> dict:
    url = request.get("Url")
    if not url:
        raise ValueError("Request is missing the 'Url' field.")

    if _is_instagram_url(url):
        url = _normalize_instagram_url(url)
        request["Url"] = url

    fmt         = str(request.get("Format", "mp4")).lower()
    is_playlist = bool(request.get("IsPlaylist"))
    send_stage(job_id, "DOWNLOADING", "Starting media download.")

    opts = _make_ytdlp_opts(job_id, request, raw_dir, ffmpeg, is_playlist)

    if _is_facebook_url(url):
        log("Facebook URL detected — injecting platform args", job_id)
        opts = _facebook_ydl_opts(opts)
    elif _is_instagram_url(url):
        log("Instagram URL detected — injecting platform args", job_id)
        opts = _instagram_ydl_opts(opts)
    elif _is_pornhub_url(url):
        log("PornHub URL detected — injecting platform args", job_id)
        opts = _pornhub_ydl_opts(opts)

    title        = "media"
    thumbnail    = None
    entry_titles = []
    last_error   = ""

    for attempt_name, attempt_opts in _build_cookie_variants(opts, job_id):
        attempt_dir = raw_dir / sanitize_filename(attempt_name.replace(" ", "_"))
        attempt_dir.mkdir(parents=True, exist_ok=True)
        attempt_opts = copy.deepcopy(attempt_opts)
        # Isolate each authentication attempt. This prevents a broken output
        # from one attempt being mistaken for the successful output of the
        # next attempt.
        outtmpl = attempt_opts.get("outtmpl")
        if isinstance(outtmpl, str):
            attempt_opts["outtmpl"] = str(attempt_dir / Path(outtmpl).name)

        error_log: list[str] = []

        def _on_warning(msg: str) -> None:
            log(f"yt-dlp warn: {msg}", job_id, level="WARN")

        def _on_error(msg: str) -> None:
            error_log.append(msg)
            log(f"yt-dlp error: {msg}", job_id, level="ERROR")

        attempt_opts["logger"] = type("_L", (), {
            "debug"  : lambda self, m: None,
            "warning": lambda self, m: _on_warning(m),
            "error"  : lambda self, m: _on_error(m),
        })()

        if "impersonate" in attempt_opts and isinstance(attempt_opts["impersonate"], str):
            try:
                from yt_dlp.networking.impersonate import ImpersonateTarget
                attempt_opts["impersonate"] = ImpersonateTarget.from_str(attempt_opts["impersonate"])
            except Exception:
                attempt_opts.pop("impersonate", None)

        try:
            log(f"curl_cffi: {_has_curl_cffi()}", job_id)
            log(f"impersonate: {attempt_opts.get('impersonate')}", job_id)
            log(f"Trying yt-dlp with {attempt_name}.", job_id)
            with yt_dlp.YoutubeDL(attempt_opts) as ydl:
                info = ydl.extract_info(url, download=True)

                if info:
                    entries = info.get("entries") or [info]
                    first   = entries[0] or {}

                    selected = first.get("requested_formats") or info.get("requested_formats") or [first]
                    video = next((item for item in selected if item and item.get("vcodec") not in (None, "none")), first)
                    actual_height = video.get("height")
                    actual_fps = video.get("fps")
                    if actual_height or actual_fps:
                        actual_label = f"{actual_height or '?'}p"
                        if actual_fps:
                            actual_label += f" · {actual_fps:g} fps"
                        send_stage(job_id, "DOWNLOADING", f"Selected source: {actual_label}")
                        log(f"Selected source format: {actual_label}", job_id)

                    entry_titles = [_resolve_entry_title(e or {}, info) for e in entries]
                    title = entry_titles[0] if entry_titles else _resolve_entry_title(info)

                    thumbnails = info.get("thumbnails") or first.get("thumbnails") or []
                    thumbnail  = (
                        info.get("thumbnail")
                        or first.get("thumbnail")
                        or (thumbnails[-1].get("url") if thumbnails else None)
                    )
                    candidate_ext = "mp3" if fmt == "mp3" else "mp4"
                    if fmt == "mp4":
                        _mux_orphan_audio(raw_dir, ffmpeg, job_id)
                    valid_files = _collect_files(raw_dir, candidate_ext, validate=True)
                    if valid_files:
                        break
                    last_error = "yt-dlp completed but produced no valid media file"
                    log(last_error, job_id, level="WARN")
                    continue

        except Exception as exc:
            raw_err = traceback.format_exc()
            last_error = raw_err
            log(f"yt-dlp attempt failed ({attempt_name}): {raw_err}", job_id, level="WARN")
            continue
    else:
        fallback_res = _try_media_fallback(url, raw_dir, request, ffmpeg, job_id)
        if fallback_res:
            return fallback_res
        _raise_download_error(last_error or "yt-dlp failed without a detailed error.", url, job_id)

    if thumbnail:
        send_thumbnail(job_id, thumbnail)

    ext   = "mp3" if fmt == "mp3" else "mp4"
    files = _collect_files(raw_dir, ext, validate=True)
    if not files:
        raise FileNotFoundError("Download completed but no output files were found.")

    file_title = title.strip()
    log(f"Resolved title: {file_title!r}", job_id)

    return {
        "files": files,
        "extension": ext,
        "title": file_title,
        "entry_titles": entry_titles,
        "segment_applied": not is_playlist and has_segment(request),
    }


def _raise_download_error(raw: str, url: str, job_id: str) -> None:
    is_fb = _is_facebook_url(url)
    raw_lc = raw.lower()

    if "proxy" in raw_lc or "407" in raw_lc or "tunnel connection failed" in raw_lc:
        raise RuntimeError(
            "The configured proxy could not be used for this download. "
            "Check that the proxy URL is correct, reachable, and includes credentials if required."
        )

    if "Sign in to confirm" in raw or "not a bot" in raw:
        cookies_path = _cookies_file_path()
        if cookies_path:
            hint = (
                "YouTube rejected the request (bot detection). "
                f"Your cookies file may be expired. Re-export a fresh cookies.txt and replace: {cookies_path}"
            )
        else:
            hint = (
                "YouTube rejected the request (bot detection). "
                "Export cookies.txt and place it in the app directory, or configure in Settings."
            )
        raise RuntimeError(hint)

    if is_fb and "Cannot parse data" in raw:
        raise RuntimeError(
            "Failed to download Facebook video. The post may be private or require login cookies."
        )

    if _is_instagram_url(url):
        if "login" in raw_lc or "empty media response" in raw_lc or "not granting access" in raw_lc:
            raise RuntimeError(
                "Instagram requires login to view this post. Please export cookies.txt or check the link."
            )
        if "no video formats found" in raw_lc or "there is no video in this post" in raw_lc:
            raise RuntimeError("No downloadable media could be found in this Instagram post.")

    if _is_pornhub_url(url):
        if "410" in raw or "gone" in raw_lc:
            raise RuntimeError(
                "PornHub blocked access (HTTP 410 Gone). "
                "The site's bot protection blocked the request. Please provide cookies.txt or check the link."
            )

    # Clean up raw error message to avoid flooding UI with Python stack trace
    err_match = re.search(r"(?:ERROR|ExtractorError):\s*([^\r\n]+)", raw)
    if err_match:
        raise RuntimeError(f"Download failed: {err_match.group(1).strip()}")

    if "is not a valid URL" in raw or "Unsupported URL" in raw:
        raise RuntimeError("Unsupported URL or invalid website address.")

    # Fallback to the last non-empty line of the error
    lines = [line.strip() for line in raw.strip().splitlines() if line.strip()]
    last_line = lines[-1] if lines else "Download failed."
    raise RuntimeError(last_line)


# =============================================================================
# Time parsing
# =============================================================================

def parse_time(value):
    if value is None:
        return None
    if isinstance(value, (int, float)):
        return float(value)
    text = str(value).strip()
    if not text:
        return None
    if ":" not in text:
        return float(text)
    parts = text.split(":")
    if len(parts) > 3:
        raise ValueError(f"Invalid time format: {value!r}")
    total = 0.0
    for part in parts:
        total = total * 60 + float(part)
    return total


def validate_segment(request: dict) -> None:
    start = parse_time(request.get("SegmentStart"))
    end   = parse_time(request.get("SegmentEnd"))
    if start is not None and start < 0:
        raise ValueError("SegmentStart must be >= 0.")
    if end is not None and end <= 0:
        raise ValueError("SegmentEnd must be > 0.")
    if start is not None and end is not None and end <= start:
        raise ValueError("SegmentEnd must be greater than SegmentStart.")


def has_segment(request: dict) -> bool:
    return bool(request.get("SegmentStart") or request.get("SegmentEnd"))


# =============================================================================
# FFmpeg helpers
# =============================================================================

def _probe_media(source: str | Path):
    ffprobe = str(FFPROBE_BIN) if FFPROBE_BIN.exists() else shutil.which("ffprobe") or "ffprobe"
    return probe_media(source, ffprobe)


def _probe_duration_seconds(source: str | Path) -> float | None:
    """Reuse the validation/codec probe for FFmpeg progress calculation."""
    media = _probe_media(source)
    return media.duration if media else None


def run_command(command: list, job_id: str, step: str) -> None:
    log(f"{step}: {' '.join(str(c) for c in command)}", job_id)
    is_ffmpeg = Path(str(command[0])).stem.casefold() == "ffmpeg"
    duration_us = None
    if is_ffmpeg:
        try:
            input_index = command.index("-i") + 1
            duration = _probe_duration_seconds(command[input_index])
            duration_us = int(duration * 1_000_000) if duration else None
        except (ValueError, IndexError):
            pass

    ffmpeg_command = command
    if is_ffmpeg:
        # FFmpeg's key/value progress stream is stable and does not require
        # parsing localized stderr output.
        ffmpeg_command = command[:-1] + ["-progress", "pipe:1", "-nostats", command[-1]]
        send_stage(job_id, "SEGMENTING" if "segment" in step.casefold() else "PROCESSING", step)

    process = subprocess.Popen(
        ffmpeg_command,
        stdout=subprocess.PIPE if is_ffmpeg else subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        encoding="utf-8",
        errors="ignore",
        bufsize=1,
    )
    progress_values: dict[str, str] = {}
    output_lines: deque[str] = deque(maxlen=100)
    assert process.stdout is not None
    try:
        for line in process.stdout:
            clean = line.strip()
            if is_ffmpeg and "=" in clean:
                key, value = clean.split("=", 1)
                if key in {"out_time_us", "speed", "progress"}:
                    progress_values[key] = value
                if key == "progress":
                    try:
                        out_time_us = int(progress_values.get("out_time_us", "0") or 0)
                    except ValueError:
                        out_time_us = 0
                    speed_text = progress_values.get("speed", "").rstrip("x")
                    try:
                        speed_factor = float(speed_text)
                    except ValueError:
                        speed_factor = 0.0
                    if duration_us:
                        percent = min(99.9, max(0.0, out_time_us / duration_us * 100))
                        eta = int(max(0, (duration_us - out_time_us) / 1_000_000 / speed_factor)) if speed_factor > 0 else -1
                        send_progress(job_id, f"{percent:.1f}", f"{speed_factor:.2f}", str(eta),
                                      "SEGMENTING" if "segment" in step.casefold() else "PROCESSING",
                                      out_time_us, duration_us)
                    continue
            if clean:
                output_lines.append(clean[-4000:])
        process.wait()
    except BaseException:
        if process.poll() is None:
            process.kill()
        process.wait()
        raise
    finally:
        process.stdout.close()
    output = "\n".join(output_lines)
    if output:
        log(f"{step} output: {output[-4000:]}", job_id, level="WARN" if process.returncode else "INFO")
    if process.returncode != 0:
        last_err = (output.splitlines() or [""])[-1]
        raise RuntimeError(
            f"{step} failed (exit {process.returncode}). "
            + (f"Error: {last_err}" if last_err else "")
        )


# =============================================================================
# OPTIMIZATION 4: Smart FFmpeg — stream copy when no re-encode needed
# =============================================================================

def _probe_stream_codecs(src: Path) -> tuple[str, str]:
    media = _probe_media(src)
    if media is None:
        return "", ""
    return media.video_codec, media.audio_codecs[0] if media.audio_codecs else ""


def segment_media(src: Path, dst: Path, ffmpeg: str, request: dict, job_id: str) -> None:
    fast_mode = bool(request.get("FastMode", True))
    cmd = [ffmpeg, "-y"]
    # Put -ss after -i for accurate decoding. Input-side seeking is fast but
    # keyframe based when packets are copied.
    cmd += ["-i", str(src)]
    if request.get("SegmentStart"):
        cmd += ["-ss", str(request["SegmentStart"])]
    if request.get("SegmentEnd"):
        start = parse_time(request.get("SegmentStart")) or 0.0
        cmd += ["-t", str(parse_time(request["SegmentEnd"]) - start)]

    if dst.suffix.lower() == ".mp3":
        quality = str(request.get("AudioQuality", "128") or "128")
        if quality not in {"96", "128", "160", "192", "320"}:
            quality = "128"
        cmd += ["-vn", "-c:a", "libmp3lame", "-b:a", quality + "k"]
    else:
        cmd += ["-map", "0:v:0", "-map", "0:a?", "-sn", "-dn"]
        # Re-encode the selected interval so the first decoded frame and final
        # timestamp are exact. FastMode selects ultrafast CRF 19; quality mode
        # uses the faster-than-default veryfast CRF 19 path.
        cmd += _encoder_args("libx264", fast_mode)
        cmd += ["-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"]
    cmd += ["-avoid_negative_ts", "make_zero"]
    if dst.suffix.lower() == ".mp3":
        cmd += ["-write_xing", "0"]
    cmd.append(str(dst))
    run_command(cmd, job_id, "ffmpeg segment")


def standardize_mp4(
    src: Path,
    dst: Path,
    ffmpeg: str,
    request: dict,
    job_id: str,
) -> Path:
    fast_mode = bool(request.get("FastMode", True))
    media = _probe_media(src)
    if media is None or not media.video_codec:
        raise RuntimeError(f"Cannot read a video stream from {src.name}.")

    # Decide independently for every stream type. An audio conversion should
    # never force an already-compatible video through another lossy encode.
    needs_reencode = media.video_codec != "h264"
    encoder_args = (_get_video_encoder_args(ffmpeg, fast_mode=fast_mode)
                    if needs_reencode else ["-c:v", "copy"])
    audio_args = ["-c:a", "copy"]
    for index, codec in enumerate(media.audio_codecs):
        if codec != "aac":
            audio_args += [f"-c:a:{index}", "aac", f"-b:a:{index}", "192k"]

    def build_command(video_args):
        command = [
            ffmpeg, "-y", "-i", str(src),
            "-map", "0:v:0", "-map", "0:a?", "-sn", "-dn",
            *video_args, *audio_args,
        ]
        if not fast_mode:
            command += ["-movflags", "+faststart"]
        return command + [str(dst)]

    try:
        run_command(build_command(encoder_args), job_id, "ffmpeg standardize")
    except Exception as exc:
        selected_encoder = encoder_args[1]
        if not needs_reencode or selected_encoder == "libx264":
            raise
        log(f"{selected_encoder} failed; retrying with libx264: {exc}", job_id, level="WARN")
        run_command(build_command(_encoder_args("libx264", fast_mode)),
                    job_id, "ffmpeg standardize fallback")

    if not dst.exists() or dst.stat().st_size == 0:
        raise RuntimeError(f"FFmpeg did not produce {dst.name}.")
    return dst


def repair_mp4_audio_start(
    src: Path,
    dst: Path,
    ffmpeg: str,
    job_id: str,
) -> Path:
    """Repair AAC priming/negative PTS while stream-copying the video.

    Some providers put AAC priming packets before t=0.  Keeping those packets
    with a plain stream-copy can produce a short burst of static/clicking at the
    beginning in desktop players.  Re-encode only the audio with an explicit
    first PTS of zero; the video remains stream-copied, so this is much faster
    than a full video encode while removing the audible glitch.
    """
    cmd = [
        ffmpeg, "-y",
        "-i", str(src),
        "-map", "0:v:0", "-map", "0:a?", "-map_metadata", "0",
        "-sn", "-dn", "-c:v", "copy",
        "-c:a", "aac", "-b:a", "192k",
        "-af", "aresample=async=1:first_pts=0",
        "-movflags", "+faststart",
        str(dst),
    ]
    run_command(cmd, job_id, "ffmpeg normalize timestamps")
    if not dst.exists() or dst.stat().st_size == 0:
        raise RuntimeError(f"FFmpeg did not produce {dst.name}.")
    return dst


def inject_metadata(src: Path, title: str, ffmpeg: str, job_id: str) -> Path:
    if src.suffix.lower() in (".jpg", ".jpeg", ".png", ".webp", ".gif", ".zip"):
        return src
    tmp = src.parent / f"meta_{src.name}"
    cmd = [ffmpeg, "-y", "-i", str(src), "-metadata", f"title={title}", "-c", "copy"]
    if src.suffix.lower() == ".mp4":
        cmd += ["-movflags", "+faststart"]
    cmd.append(str(tmp))
    try:
        run_command(cmd, job_id, "ffmpeg metadata")
        if tmp.exists() and tmp.stat().st_size > 0:
            src.unlink(missing_ok=True)
            return tmp
    except Exception as exc:
        log(f"Metadata injection skipped: {exc}", job_id, level="WARN")
    return src


# =============================================================================
# OPTIMIZATION 5: Thread-safe Demucs model loading + bounded inference concurrency
# =============================================================================

_DEMUCS_MODEL      = None
_DEMUCS_MODEL_LOCK = threading.Lock()

_DEMUCS_MAX_CONCURRENT   = 1
_DEMUCS_INFERENCE_SEM    = threading.Semaphore(1)


def _get_demucs_model(job_id: str):
    global _DEMUCS_MODEL
    _ensure_ai_dependencies()
    with _DEMUCS_MODEL_LOCK:
        if _DEMUCS_MODEL is None:
            log("Loading Demucs model htdemucs...", job_id)
            _DEMUCS_MODEL = demucs_pretrained.get_model("htdemucs")
            device = "cuda" if torch.cuda.is_available() else "cpu"
            log(f"Demucs device: {device}", job_id)
            _DEMUCS_MODEL.to(device)
            if device == "cuda" and _DEMUCS_USE_HALF:
                _DEMUCS_MODEL.half()
                log("Demucs running in FP16 (half precision) on GPU.", job_id)
            _DEMUCS_MODEL.eval()
    return _DEMUCS_MODEL


def _extract_wav(src: Path, dst: Path, ffmpeg: str, job_id: str) -> None:
    run_command(
        [ffmpeg, "-y", "-i", str(src), "-vn", "-ac", "2", "-ar", "44100",
         "-c:a", "pcm_s16le", str(dst)],
        job_id, "ffmpeg extract wav",
    )


def _run_demucs(audio_in: Path, vocals_out: Path, job_id: str) -> None:
    if run_remote_demucs(audio_in, vocals_out, lambda message: log(message, job_id)):
        return
    _ensure_ai_dependencies()
    model    = _get_demucs_model(job_id)
    device   = "cuda" if torch.cuda.is_available() else "cpu"
    use_half = device == "cuda" and _DEMUCS_USE_HALF

    samples, sr = sf.read(str(audio_in), always_2d=True, dtype="float32")
    waveform    = torch.from_numpy(samples.T)
    waveform    = convert_audio(waveform, sr, model.samplerate, model.audio_channels)
    waveform    = waveform.to(device)

    ref     = waveform.mean(0)
    ref_std = ref.std().clamp(min=1e-8)
    normed  = (waveform - ref.mean()) / ref_std

    model_input = normed.half() if use_half else normed
    log(f"Waveform: {waveform.shape}", job_id)
    log(f"Model input: {model_input.shape}", job_id)
    log(f"Sample rate: {model.samplerate}", job_id)
    log(f"Model segment: {getattr(model, 'segment', 'N/A')}", job_id)

    with _DEMUCS_INFERENCE_SEM:
        with torch.no_grad():
            sources = demucs_apply.apply_model(
                model,
                model_input[None],
                device=device,
                shifts=1,
                split=True,
                overlap=0.25,
                progress=False,
                num_workers=0,
            )[0]

    if use_half:
        sources = sources.float()

    sources = sources * ref_std + ref.mean()
    if "vocals" not in model.sources:
        raise RuntimeError("Demucs model does not expose a 'vocals' stem.")

    vocals_np = sources[model.sources.index("vocals")].detach().cpu().numpy().T
    sf.write(str(vocals_out), vocals_np, model.samplerate, subtype="PCM_16")


def separate_vocals(src: Path, work_dir: Path, ffmpeg: str, fmt: str, job_id: str) -> Path:
    wav_input = src if src.suffix.lower() == ".mp3" else work_dir / f"{src.stem}_din.wav"
    if wav_input != src:
        _extract_wav(src, wav_input, ffmpeg, job_id)

    vocals_wav = work_dir / f"{src.stem}_vocals.wav"
    _run_demucs(wav_input, vocals_wav, job_id)

    if wav_input != src and wav_input.exists():
        wav_input.unlink(missing_ok=True)

    if fmt == "mp3":
        out = work_dir / f"{src.stem}_vocals.mp3"
        run_command(
            [ffmpeg, "-y", "-i", str(vocals_wav),
             "-codec:a", "libmp3lame", "-q:a", "2", str(out)],
            job_id, "ffmpeg vocals→mp3",
        )
        vocals_wav.unlink(missing_ok=True)
        return out

    out = work_dir / f"{src.stem}_vocals.mp4"
    video_codec, _ = _probe_stream_codecs(src)
    encoder_args = ["-c:v", "copy"] if video_codec == "h264" else _get_video_encoder_args(ffmpeg)
    run_command(
        [
            ffmpeg, "-y", "-threads", "0",
            "-i", str(src), "-i", str(vocals_wav),
            "-map", "0:v:0", "-map", "1:a:0",
            *encoder_args,
            "-c:a", "aac", "-b:a", "320k",
            "-shortest", str(out),
        ],
        job_id, "ffmpeg merge vocals",
    )
    vocals_wav.unlink(missing_ok=True)
    return out


# =============================================================================
# File utilities
# =============================================================================

def file_hash(path: Path, chunk_size: int = 1024 * 1024) -> str:
    h = hashlib.blake2b(digest_size=20)
    with path.open("rb") as f:
        while True:
            chunk = f.read(chunk_size)
            if not chunk:
                break
            h.update(chunk)
    return h.hexdigest()


def files_are_identical(a: Path, b: Path) -> bool:
    if not a.exists() or not b.exists():
        return False
    if a.stat().st_size != b.stat().st_size:
        return False
    return file_hash(a) == file_hash(b)


def unique_path(path: Path) -> Path:
    if not path.exists():
        return path
    i = 1
    while True:
        candidate = path.with_name(f"{path.stem} ({i}){path.suffix}")
        if not candidate.exists():
            return candidate
        i += 1


def finalize(src: Path, dst: Path, job_id: str) -> Path:
    dst.parent.mkdir(parents=True, exist_ok=True)

    if not dst.exists():
        os.replace(src, dst)
        log(f"Output: {dst}", job_id)
        return dst

    if files_are_identical(src, dst):
        log(f"Duplicate detected. Reusing existing file: {dst.name}", job_id)
        src.unlink(missing_ok=True)
        return dst

    out = unique_path(dst)
    os.replace(src, out)
    log(f"Output: {out}", job_id)
    return out


# =============================================================================
# Per-file processing pipeline
# =============================================================================

def process_file(src: Path, request: dict, ffmpeg: str, work_dir: Path, job_id: str,
                 *, segment_applied: bool = False) -> Path:
    if src.suffix.lower() in (".jpg", ".jpeg", ".png", ".webp", ".gif", ".zip"):
        log(f"Static media format ({src.suffix}) — skipping video postprocessing.", job_id)
        return src

    fmt = str(request.get("Format", "mp4")).lower()
    cur = src

    # yt-dlp range downloads are already cut. Full-file fallbacks and playlist
    # items must be trimmed locally, before expensive conversions/AI processing.
    if has_segment(request) and not segment_applied:
        validate_segment(request)
        extension = ".mp3" if fmt == "mp3" else (
            src.suffix if request.get("FastMode", True) else ".mp4")
        trimmed = work_dir / f"{src.stem}_trim{extension}"
        segment_media(cur, trimmed, ffmpeg, request, job_id)
        cur = trimmed

    if request.get("ExtractVocals"):
        expected_ext = ".mp3" if fmt == "mp3" else ".mp4"
        vocals_out   = work_dir / f"{src.stem}_vocals{expected_ext}"
        if not (vocals_out.exists() and vocals_out.stat().st_size > 0):
            send_stage(job_id, "SEPARATING_VOCALS", src.name)
            cur = separate_vocals(cur, work_dir, ffmpeg, fmt, job_id)
        else:
            cur = vocals_out

    if (
        fmt == "mp4"
        and not request.get("ExtractVocals")
    ):
        media = _probe_media(cur)
        if media is None or not media.video_codec:
            raise RuntimeError(f"Cannot read a video stream from {cur.name}.")
        if cur.suffix.lower() == ".mp4" and media.video_codec == "h264" and media.audio_is_aac:
            normalized = work_dir / f"{cur.stem}_normalized.mp4"
            if not (normalized.exists() and normalized.stat().st_size > 0):
                cur = repair_mp4_audio_start(cur, normalized, ffmpeg, job_id)
            else:
                cur = normalized
            log(f"Repaired AAC start while copying video: {cur.name}", job_id)
            return cur

        std = work_dir / f"{src.stem}_std.mp4"
        if not (std.exists() and std.stat().st_size > 0):
            cur = standardize_mp4(
                cur,
                std,
                ffmpeg,
                request,
                job_id,
            )
        else:
            cur = std

    # A high-frame-rate preset is a hard requirement.  yt-dlp's format
    # metadata can say 50/60 fps while a provider or a later remux produces a
    # lower-rate file, so verify the saved stream itself before publishing it.
    requested_fps = _parse_quality_fps(request.get("Quality"))
    if fmt == "mp4" and requested_fps:
        media = _probe_media(cur)
        actual_fps = media.video_fps if media else None
        if actual_fps is None or actual_fps + 0.5 < requested_fps:
            actual_text = f"{actual_fps:.3g} fps" if actual_fps else "an unknown frame rate"
            raise RuntimeError(
                f"The selected {requested_fps} fps download produced {actual_text}. "
                "Choose another source or quality; the app will not label it as the requested FPS."
            )
        log(f"Verified output frame rate: {actual_fps:.3g} fps (requested {requested_fps}).", job_id)

    return cur


# =============================================================================
# OPTIMIZATION 6: Parallel playlist item processing
# =============================================================================
_PLAYLIST_MAX_WORKERS = min(max(2, os.cpu_count() or 2), 8)


def _playlist_worker_count(request: dict) -> int:
    if request.get("ExtractVocals"):
        return min(_DEMUCS_MAX_CONCURRENT + 1, _PLAYLIST_MAX_WORKERS)
    return _PLAYLIST_MAX_WORKERS


def _process_playlist_item(
    idx      : int,
    total    : int,
    src      : Path,
    request  : dict,
    ffmpeg   : str,
    work_dir : Path,
    out_dir  : Path,
    ext      : str,
    job_id   : str,
    title    : str | None = None,
) -> Path | None:

    log(f"Playlist item {idx}: {src.name}", job_id)

    item_work = work_dir / f"item_{idx:02d}"
    item_work.mkdir(parents=True, exist_ok=True)

    try:
        processed = process_file(src, request, ffmpeg, item_work, job_id)

        if title:
            real_title = sanitize_filename(title)
        else:
            real_title = sanitize_filename(re.sub(r"^\d+[_ -]+", "", src.stem))

        need_metadata = (
            not request.get("FastMode", True)
            and processed.suffix.lower() == ".mp4"
            and processed.stem != real_title
        )

        if need_metadata:
            processed = inject_metadata(processed, real_title, ffmpeg, job_id)
        else:
            log("Skipping metadata injection.", job_id)

        dest  = out_dir / f"{idx:02d}_{real_title}{ext}"
        final = finalize(processed, dest, job_id)

        send_stage(job_id, "ITEM_COMPLETED", final.name)
        send_result(job_id, f"{out_dir.name}/{final.name}", real_title)

        return final

    except Exception as exc:
        log(f"Playlist item {idx} failed: {exc}", job_id, level="ERROR")
        return None
    finally:
        if item_work.exists():
            shutil.rmtree(item_work, ignore_errors=True)


# =============================================================================
# OPTIMIZATION 7: Streaming playlist download + process pipeline
# =============================================================================

def _make_postprocessor_hook(
    job_id,
    request,
    ffmpeg,
    work_dir,
    out_dir,
    ext,
    pool,
    futures,
    counter_lock,
    counter,
):
    processed_files = set()
    processed_lock = threading.Lock()

    def hook(d: dict) -> None:
        if d.get("status") != "finished":
            return

        pp_name = d.get("postprocessor", "")
        if pp_name not in (
            "MoveFiles",
            "Merger",
            "FFmpegExtractAudio",
            "VideoRemuxer",
        ):
            return

        info = d.get("info_dict") or {}

        filepath = (
            info.get("filepath")
            or info.get("_filename")
            or d.get("filename")
        )

        if not filepath:
            return

        src = Path(filepath)

        if not src.exists():
            return

        key = str(src.resolve()).lower()

        with processed_lock:
            if key in processed_files:
                log(f"Skipping duplicate postprocessor event: {src.name}", job_id)
                return

            processed_files.add(key)
            counter[0] += 1
            idx = counter[0]

        title = _resolve_entry_title(info)

        log(
            f"Processing playlist item #{idx}: {src.name} ({pp_name})",
            job_id,
        )

        future = pool.submit(
            _process_playlist_item,
            idx,
            -1,
            src,
            request,
            ffmpeg,
            work_dir,
            out_dir,
            ext,
            job_id,
            title,
        )

        futures.append((idx, future))

    return hook


def download_and_process_playlist_streaming(
    job_id: str, request: dict, ffmpeg: str, raw_dir: Path, work_dir: Path, out_dir: Path
) -> list:
    url = request.get("Url")
    if not url:
        raise ValueError("Request is missing the 'Url' field.")

    if _is_instagram_url(url):
        url = _normalize_instagram_url(url)
        request["Url"] = url

    fmt = str(request.get("Format", "mp4")).lower()
    ext = ".mp3" if fmt == "mp3" else ".mp4"

    send_stage(job_id, "DOWNLOADING", "Starting playlist download.")

    opts = _make_ytdlp_opts(job_id, request, raw_dir, ffmpeg, is_playlist=True)
    if _is_facebook_url(url):
        opts = _facebook_ydl_opts(opts)
    elif _is_instagram_url(url):
        opts = _instagram_ydl_opts(opts)
    elif _is_pornhub_url(url):
        opts = _pornhub_ydl_opts(opts)

    if "impersonate" in opts and isinstance(opts["impersonate"], str):
        try:
            from yt_dlp.networking.impersonate import ImpersonateTarget
            opts["impersonate"] = ImpersonateTarget.from_str(opts["impersonate"])
        except Exception:
            opts.pop("impersonate", None)

    cookies = _cookies_file_path()
    if cookies:
        opts["cookiefile"] = str(cookies)
    else:
        browsers = _browser_cookies_source()
        if browsers:
            opts["cookiesfrombrowser"] = (browsers[0],)

    out_dir.mkdir(parents=True, exist_ok=True)

    futures: list = []
    counter = [0]
    counter_lock = threading.Lock()

    worker_count = _playlist_worker_count(request)
    pool = ThreadPoolExecutor(max_workers=worker_count)

    opts["progress_hooks"] = [_make_progress_hook(job_id)]
    opts["postprocessor_hooks"] = [
        _make_postprocessor_hook(
            job_id, request, ffmpeg, work_dir, out_dir, ext,
            pool, futures, counter_lock, counter,
        )
    ]

    error_log: list[str] = []

    def _on_warning(msg): log(f"yt-dlp warn: {msg}", job_id, level="WARN")
    def _on_error(msg):
        error_log.append(msg)
        log(f"yt-dlp error: {msg}", job_id, level="ERROR")

    opts["logger"] = type("_L", (), {
        "debug": lambda self, m: None,
        "warning": lambda self, m: _on_warning(m),
        "error": lambda self, m: _on_error(m),
    })()

    try:
        with yt_dlp.YoutubeDL(opts) as ydl:
            ydl.extract_info(url, download=True)
    except Exception as exc:
        if ("cookiesfrombrowser" in opts or "cookiefile" in opts) and not futures:
            log("Playlist extraction with cookies failed; retrying anonymously.", job_id, level="WARN")
            opts_anon = copy.deepcopy(opts)
            opts_anon.pop("cookiesfrombrowser", None)
            opts_anon.pop("cookiefile", None)
            try:
                with yt_dlp.YoutubeDL(opts_anon) as ydl:
                    ydl.extract_info(url, download=True)
            except Exception as exc2:
                raw_err = "\n".join(error_log) if error_log else str(exc2)
                pool.shutdown(wait=False)
                _raise_download_error(raw_err, url, job_id)
        else:
            raw_err = "\n".join(error_log) if error_log else str(exc)
            if not futures:
                pool.shutdown(wait=False)
                _raise_download_error(raw_err, url, job_id)
            log(f"Playlist download ended with errors (partial results kept): {raw_err}",
                job_id, level="WARN")

    successes = []
    pool.shutdown(wait=True)
    for idx, future in sorted(futures, key=lambda t: t[0]):
        result = future.result()
        if result:
            successes.append(result)

    return successes


def download_and_process_playlist_fallback(
    job_id: str, request: dict, ffmpeg: str, raw_dir: Path, work_dir: Path, out_dir: Path
) -> list:
    dl           = download_media(job_id, request, ffmpeg, raw_dir)
    files        = dl["files"]
    entry_titles = dl.get("entry_titles") or []
    fmt          = str(request.get("Format", "mp4")).lower()
    ext          = ".mp3" if fmt == "mp3" else ".mp4"
    total        = len(files)

    out_dir.mkdir(parents=True, exist_ok=True)
    successes = []

    def _title_for(i: int) -> str | None:
        return entry_titles[i - 1] if i - 1 < len(entry_titles) else None

    worker_count = _playlist_worker_count(request)
    use_parallel = worker_count > 1

    if use_parallel:
        with ThreadPoolExecutor(max_workers=worker_count) as pool:
            futures = {
                pool.submit(
                    _process_playlist_item,
                    idx, total, src, request, ffmpeg, work_dir, out_dir, ext, job_id,
                    _title_for(idx),
                ): idx
                for idx, src in enumerate(files, 1)
            }
            for future in as_completed(futures):
                result = future.result()
                if result:
                    successes.append(result)
    else:
        for idx, src in enumerate(files, 1):
            result = _process_playlist_item(
                idx, total, src, request, ffmpeg, work_dir, out_dir, ext, job_id,
                _title_for(idx),
            )
            if result:
                successes.append(result)

    return successes


# =============================================================================
# Job orchestration
# =============================================================================

def process_job(job: dict) -> None:
    job_id  = sanitize_job_id(job["JobId"])
    request = job["Request"]

    validate_segment(request)
    configure_node_path()
    ffmpeg = detect_ffmpeg()
    log(f"ffmpeg: {ffmpeg}", job_id)

    fmt      = str(request.get("Format", "mp4")).lower()
    ext      = ".mp3" if fmt == "mp3" else ".mp4"
    playlist = bool(request.get("IsPlaylist"))

    job_tmp  = TEMP_DIR / job_id
    raw_dir  = job_tmp / "raw"
    work_dir = job_tmp / "working"

    job_tmp.mkdir(parents=True, exist_ok=True)
    raw_dir.mkdir(parents=True, exist_ok=True)
    work_dir.mkdir(parents=True, exist_ok=True)

    try:
        if playlist:
            out_dir = DOWNLOAD_DIR / job_id

            try:
                successes = download_and_process_playlist_streaming(
                    job_id, request, ffmpeg, raw_dir, work_dir, out_dir
                )
            except RuntimeError:
                log("Streaming playlist path failed before any item completed; "
                    "falling back to full-download mode.", job_id, level="WARN")
                successes = download_and_process_playlist_fallback(
                    job_id, request, ffmpeg, raw_dir, work_dir, out_dir
                )

            if not successes:
                raise RuntimeError("Playlist download produced no valid files.")

            send_stage(job_id, "COMPLETED", f"{len(successes)} files saved.")
            send_result(job_id, out_dir.name)
            return

        # ── Single file ───────────────────────────────────────────────────────
        dl           = download_media(job_id, request, ffmpeg, raw_dir)
        files        = dl["files"]
        title        = dl["title"]

        src        = files[0]
        # الاستفادة من العنوان الحقيقي المستخرج لاحقاً بدلاً من الاسم المؤقت (ID) 
        real_title = sanitize_filename(title) if title else sanitize_filename(src.stem)

        log(f"Processing: {src.name} → {real_title!r}", job_id)
        processed = process_file(src, request, ffmpeg, work_dir, job_id,
                                 segment_applied=bool(dl.get("segment_applied", False)))
        if request.get("FastMode", True):
            log("Fast mode: skipping the extra metadata remux pass.", job_id)
        else:
            processed = inject_metadata(processed, real_title, ffmpeg, job_id)

        out_ext = processed.suffix.lower() if processed.suffix.lower() in (
            ".jpg", ".jpeg", ".png", ".webp", ".gif", ".zip"
        ) else ext

        dest  = DOWNLOAD_DIR / f"{real_title}{out_ext}"
        final = finalize(processed, dest, job_id)

        try:
            (DOWNLOAD_DIR / f"{job_id}_title.txt").write_text(real_title, encoding="utf-8")
        except Exception:
            pass

        send_result(job_id, final.name, real_title)

    finally:
        if job_tmp.exists():
            shutil.rmtree(job_tmp, ignore_errors=True)
            log(f"Temp cleaned: {job_tmp}", job_id)

        if _log_handler:
            _log_handler.flush()


# =============================================================================
# OPTIMIZATION 8: Concurrent multi-job daemon mode
# =============================================================================

_JOB_MAX_CONCURRENT = max(1, int(os.environ.get("NEBULA_MAX_CONCURRENT_JOBS",
                                                  str(min(4, os.cpu_count() or 4)))))
_job_pool         = ThreadPoolExecutor(max_workers=_JOB_MAX_CONCURRENT, thread_name_prefix="job")
_active_jobs: dict = {}
_active_jobs_lock = threading.Lock()


def _run_job_safely(job: dict, job_id: str) -> None:
    log("Job started.", job_id)
    try:
        process_job(job)
        log("Job completed successfully.", job_id)
    except Exception as exc:
        log(f"Fatal error: {exc}", job_id, level="ERROR")
        log(traceback.format_exc(), job_id, level="ERROR")
        send_stage(job_id, "FAILED", str(exc))
    finally:
        with _active_jobs_lock:
            _active_jobs.pop(job_id, None)
        if _log_handler:
            _log_handler.flush()


def _submit_job(job: dict) -> None:
    try:
        job_id = sanitize_job_id(job["JobId"])
    except Exception as exc:
        print(f"Initialization error: {exc}", file=sys.stderr, flush=True)
        return

    with _active_jobs_lock:
        if job_id in _active_jobs:
            log(f"Job {job_id} is already running/queued — ignoring duplicate submission.",
                job_id, level="WARN")
            return
        future = _job_pool.submit(_run_job_safely, job, job_id)
        _active_jobs[job_id] = future


def run_daemon() -> None:
    print("ENGINE_READY", flush=True)
    log(f"Daemon mode started. Max concurrent jobs: {_JOB_MAX_CONCURRENT}.")

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        if line == "SHUTDOWN":
            log("Shutdown requested — waiting for in-flight jobs to finish.")
            break

        try:
            job = json.loads(line) if line.startswith("{") else load_job(line)
        except Exception as exc:
            print(f"Initialization error: {exc}", file=sys.stderr, flush=True)
            continue

        _submit_job(job)

    _job_pool.shutdown(wait=True)
    log("Daemon mode exiting — all jobs finished.")
    if _log_handler:
        _log_handler.flush()
        _log_handler.close()


# =============================================================================
# Entry point
# =============================================================================

def main() -> None:
    if len(sys.argv) >= 2 and sys.argv[1] == "--daemon":
        run_daemon()
        return

    if len(sys.argv) < 2:
        print(json.dumps({"error": "Missing job JSON path."}), file=sys.stderr)
        sys.exit(1)

    try:
        job    = load_job(sys.argv[1])
        job_id = sanitize_job_id(job["JobId"])
    except Exception as exc:
        print(f"Initialization error: {exc}", file=sys.stderr)
        sys.exit(1)

    log("Job started.", job_id)
    try:
        process_job(job)
        log("Job completed successfully.", job_id)
    except Exception as exc:
        log(f"Fatal error: {exc}", job_id, level="ERROR")
        log(traceback.format_exc(), job_id, level="ERROR")
        send_stage(job_id, "FAILED", str(exc))
        sys.exit(1)
    finally:
        if _log_handler:
            _log_handler.flush()
            _log_handler.close()


if __name__ == "__main__":
    main()
