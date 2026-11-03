"""Measured encoder selection and shared encoding quality policy."""

import re
import subprocess
import threading
import time


_VIDEO_ENCODER_ARGS: dict[tuple[str, bool], list] = {}
_VIDEO_ENCODER_LOCK = threading.Lock()


def _encoder_args(encoder: str, fast_mode: bool) -> list:
    quality = "19"  # Fast mode must not lower the quality target.
    if encoder == "h264_nvenc":
        if fast_mode:
            return [
                "-c:v", encoder, "-preset", "p1", "-tune", "ull",
                "-rc", "constqp", "-qp", quality, "-pix_fmt", "yuv420p",
            ]
        return [
            "-c:v", encoder, "-preset", "p4", "-rc", "vbr",
            "-cq", quality, "-pix_fmt", "yuv420p",
        ]
    if encoder == "h264_qsv":
        return [
            "-c:v", encoder,
            "-preset", "veryfast" if fast_mode else "medium",
            "-global_quality", quality,
            "-look_ahead", "0",
            "-pix_fmt", "yuv420p",
        ]
    if encoder == "h264_amf":
        return [
            "-c:v", encoder,
            "-quality", "speed" if fast_mode else "balanced",
            "-rc", "cqp",
            "-qp_i", quality,
            "-qp_p", quality,
            "-pix_fmt", "yuv420p",
        ]
    return [
        "-c:v", "libx264",
        "-preset", "ultrafast" if fast_mode else "veryfast",
        "-crf", quality,
        "-pix_fmt", "yuv420p",
    ]


def _benchmark_encoder(ffmpeg: str, encoder: str, fast_mode: bool) -> float | None:
    """Measure a real short encode; an encoder listing alone does not prove it works."""
    command = [
        ffmpeg, "-hide_banner", "-loglevel", "error",
        "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30:d=0.75",
        "-frames:v", "23", "-an",
        *_encoder_args(encoder, fast_mode),
        "-f", "null", "-",
    ]
    started = time.perf_counter()
    try:
        result = subprocess.run(
            command,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.PIPE,
            text=True,
            timeout=15,
        )
        if result.returncode != 0:
            return None
        return time.perf_counter() - started
    except (OSError, subprocess.SubprocessError):
        return None


def get_video_encoder_args(ffmpeg: str, fast_mode: bool = False, log=lambda message: None) -> list:
    global _VIDEO_ENCODER_ARGS
    cache_key = (ffmpeg, fast_mode)
    with _VIDEO_ENCODER_LOCK:
        if cache_key in _VIDEO_ENCODER_ARGS:
            return list(_VIDEO_ENCODER_ARGS[cache_key])

        listed_encoders = {"libx264"}
        try:
            result = subprocess.run(
                [ffmpeg, "-hide_banner", "-encoders"],
                capture_output=True, text=True, timeout=10,
            )
            listed_encoders.update(
                re.findall(
                    r"(?m)^\s*[A-Z.]{6}\s+(h264_(?:nvenc|qsv|amf))\s",
                    result.stdout,
                )
            )
        except Exception:
            pass

        # FFmpeg may list GPU encoders that cannot start because the matching
        # GPU or driver is absent. Benchmark only listed candidates and choose
        # the fastest working path for this machine instead of assuming a GPU
        # is always faster than x264 ultrafast.
        timings: list[tuple[float, str]] = []
        for encoder in ("h264_nvenc", "h264_qsv", "h264_amf", "libx264"):
            if encoder not in listed_encoders:
                continue
            elapsed = _benchmark_encoder(ffmpeg, encoder, fast_mode)
            if elapsed is not None:
                timings.append((elapsed, encoder))
                log(f"Encoder benchmark {encoder}: {elapsed:.3f}s")

        selected = min(timings)[1] if timings else "libx264"
        encoder_args = _encoder_args(selected, fast_mode)
        log(f"Selected {selected} for {'fast' if fast_mode else 'quality'} video encoding.")

        _VIDEO_ENCODER_ARGS[cache_key] = encoder_args
        return list(encoder_args)

