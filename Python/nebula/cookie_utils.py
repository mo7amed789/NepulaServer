"""Cookie file hygiene for yt-dlp.

The cookies.txt that Tanzeel hands to yt-dlp can come from the in-app login,
a manual import, or a file pasted by hand. Three things commonly break it:

* yt-dlp rewrites the cookie file when it exits, so two jobs sharing one file
  can corrupt it or overwrite each other's rotated cookies.
* Files saved by editors have a BOM, CRLF line endings, or spaces instead of
  tabs, and yt-dlp then rejects the whole file.
* Expired cookies are sent anyway and the site treats the session as invalid.

prepare_cookie_file() builds a clean private copy for one job and never
logs cookie values.
"""

from __future__ import annotations

import atexit
import shutil
import tempfile
import time
from pathlib import Path

HEADER = "# Netscape HTTP Cookie File"
HTTP_ONLY_PREFIX = "#HttpOnly_"


def _clean_lines(text: str, now: int) -> tuple[list[str], int, int]:
    kept: list[str] = []
    expired = 0
    for raw in text.splitlines():
        line = raw.strip("\r\n")
        if not line.strip():
            continue
        if line.startswith("#") and not line.startswith(HTTP_ONLY_PREFIX):
            continue
        parts = line.split("\t")
        if len(parts) < 7:
            # Editors and chat apps often turn tabs into spaces.
            parts = line.split(None, 6)
        if len(parts) < 7:
            continue
        parts = [p.strip() for p in parts[:7]]
        try:
            expires = int(float(parts[4]))
        except ValueError:
            continue
        if parts[3].upper() not in ("TRUE", "FALSE") or parts[1].upper() not in ("TRUE", "FALSE"):
            continue
        parts[1] = parts[1].upper()
        parts[3] = parts[3].upper()
        if expires != 0 and expires < now:
            expired += 1
            continue
        kept.append("\t".join(parts))
    return kept, expired, len(kept)


def prepare_cookie_file(src: Path, base_dir: Path) -> tuple[Path | None, str]:
    """Return (private clean copy, note). The path is None when unusable."""
    try:
        text = Path(src).read_bytes().decode("utf-8-sig", errors="replace")
    except OSError as exc:
        return None, f"Cookies file could not be read ({exc.__class__.__name__}); skipping it."

    kept, expired, count = _clean_lines(text, int(time.time()))
    if count == 0:
        if expired:
            return None, (
                f"Cookies file has only expired cookies ({expired}). "
                "Sign in again from Settings to refresh it."
            )
        return None, "Cookies file is not in Netscape cookies.txt format; skipping it."

    base_dir = Path(base_dir)
    base_dir.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix="cookies_", dir=str(base_dir)))
    atexit.register(shutil.rmtree, str(work), True)
    out = work / "cookies.txt"
    out.write_text(HEADER + "\n" + "\n".join(kept) + "\n", encoding="utf-8", newline="\n")
    note = f"Using a private copy of the cookies file ({count} cookies"
    if expired:
        note += f", {expired} expired dropped"
    return out, note + ")."
