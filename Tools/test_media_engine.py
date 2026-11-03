"""Offline integration regressions. Run with Python\python.exe Tools\test_media_engine.py."""

import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "Python"))
TEMP = tempfile.TemporaryDirectory(prefix="nebula-media-test-")
os.environ["TANZEEL_DATA"] = str(Path(TEMP.name) / "data")
import nebula_engine as engine
from nebula.probe import _probe_cached

FFMPEG = str(ROOT / "ffmpeg.exe")
FFPROBE = str(ROOT / "ffprobe.exe")


def ffmpeg(*args):
    subprocess.run([FFMPEG, "-hide_banner", "-loglevel", "error", "-y", *args],
                   check=True, capture_output=True, timeout=30)


def packet_hashes(path, stream):
    result = subprocess.run(
        [FFPROBE, "-v", "error", "-select_streams", stream, "-show_packets",
         "-show_data_hash", "sha256", "-show_entries", "packet=data_hash",
         "-of", "json", str(path)],
        check=True, capture_output=True, text=True, timeout=15,
    )
    return [packet["data_hash"] for packet in json.loads(result.stdout)["packets"]]


class MediaIntegrationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.work = Path(TEMP.name)
        cls.mp4 = cls.work / "compatible.mp4"
        cls.mov = cls.work / "pcm.mov"
        cls.mkv = cls.work / "mpeg4.mkv"
        inputs = ["-f", "lavfi", "-i", "testsrc2=s=320x180:r=24:d=1.2",
                  "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=1.2"]
        ffmpeg(*inputs, "-c:v", "libx264", "-preset", "ultrafast", "-crf", "19",
               "-c:a", "aac", str(cls.mp4))
        ffmpeg("-i", str(cls.mp4), "-c:v", "copy", "-c:a", "pcm_s16le", str(cls.mov))
        ffmpeg("-i", str(cls.mp4), "-c:v", "mpeg4", "-q:v", "2", "-c:a", "copy", str(cls.mkv))

    def test_compatible_media_is_untouched(self):
        with patch.object(engine, "repair_mp4_audio_start", side_effect=lambda src, dst, ffmpeg, job: src), \
             patch.object(engine, "run_command") as run:
            result = engine.process_file(self.mp4, {"Format": "mp4"}, FFMPEG, self.work, "test")
        self.assertEqual(result, self.mp4)
        run.assert_not_called()

    def test_audio_conversion_copies_video_packets(self):
        output = engine.process_file(self.mov, {"Format": "mp4", "FastMode": True},
                                     FFMPEG, self.work, "test")
        self.assertEqual(engine._probe_stream_codecs(output), ("h264", "aac"))
        self.assertEqual(packet_hashes(self.mov, "v:0"), packet_hashes(output, "v:0"))

    def test_video_conversion_copies_aac_packets(self):
        # Fix the encoder so this regression is independent of local GPU drivers.
        with patch.object(engine, "_get_video_encoder_args",
                          return_value=engine._encoder_args("libx264", True)):
            output = engine.process_file(self.mkv, {"Format": "mp4", "FastMode": True},
                                         FFMPEG, self.work, "test")
        self.assertEqual(engine._probe_stream_codecs(output), ("h264", "aac"))
        self.assertEqual(packet_hashes(self.mkv, "a:0"), packet_hashes(output, "a:0"))

    def test_mixed_audio_tracks_preserve_existing_aac(self):
        source = self.work / "mixed.mov"
        ffmpeg("-i", str(self.mp4), "-i", str(self.mov),
               "-map", "0:v:0", "-map", "0:a:0", "-map", "1:a:0", "-c", "copy", str(source))
        output = engine.process_file(source, {"Format": "mp4"}, FFMPEG, self.work, "test")
        self.assertEqual(engine._probe_media(output).audio_codecs, ("aac", "aac"))
        self.assertEqual(packet_hashes(source, "a:0"), packet_hashes(output, "a:0"))
        self.assertEqual(packet_hashes(source, "v:0"), packet_hashes(output, "v:0"))

    def test_hardware_failure_falls_back_to_software(self):
        output = self.work / "fallback.mp4"
        with patch.object(engine, "_get_video_encoder_args", return_value=["-c:v", "invalid_encoder"]):
            result = engine.standardize_mp4(self.mkv, output, FFMPEG, {}, "test")
        self.assertEqual(result, output)
        self.assertEqual(engine._probe_stream_codecs(output), ("h264", "aac"))
        self.assertEqual(packet_hashes(self.mkv, "a:0"), packet_hashes(output, "a:0"))

    def test_vocals_merge_preserves_video(self):
        # Keep the test offline and independent of optional AI model weights.
        with patch.object(engine, "_run_demucs",
                          side_effect=lambda source, target, job: shutil.copyfile(source, target)):
            output = engine.separate_vocals(self.mp4, self.work, FFMPEG, "mp4", "test")
        self.assertEqual(packet_hashes(self.mp4, "v:0"), packet_hashes(output, "v:0"))

    def test_validation_codecs_duration_share_one_probe(self):
        _probe_cached.cache_clear()
        with patch("nebula.probe.subprocess.run", wraps=subprocess.run) as run:
            self.assertTrue(engine._is_valid_media_file(self.mp4))
            self.assertEqual(engine._probe_stream_codecs(self.mp4), ("h264", "aac"))
            self.assertGreater(engine._probe_duration_seconds(self.mp4), 1)
            self.assertEqual(run.call_count, 1)
            stat = self.mp4.stat()
            os.utime(self.mp4, ns=(stat.st_atime_ns, stat.st_mtime_ns + 1_000_000_000))
            engine._probe_stream_codecs(self.mp4)
            self.assertEqual(run.call_count, 2)

    def test_failed_probe_is_retried(self):
        _probe_cached.cache_clear()
        with patch("nebula.probe.subprocess.run", side_effect=OSError("temporary failure")) as run:
            self.assertIsNone(engine._probe_media(self.mp4))
            self.assertIsNone(engine._probe_media(self.mp4))
            self.assertEqual(run.call_count, 2)

    def test_encode_failure_is_reported(self):
        with patch.object(engine, "run_command", side_effect=RuntimeError("encode failed")):
            with self.assertRaisesRegex(RuntimeError, "encode failed"):
                engine.standardize_mp4(self.mov, self.work / "failed.mp4", FFMPEG, {}, "test")

    def test_invalid_input_is_not_treated_as_compatible(self):
        invalid = self.work / "invalid.mp4"
        invalid.write_bytes(b"not media" * 200)
        with self.assertRaisesRegex(RuntimeError, "Cannot read a video stream"):
            engine.process_file(invalid, {"Format": "mp4"}, FFMPEG, self.work, "test")

    def test_fast_mode_retains_quality_target(self):
        for encoder, quality_option in [("libx264", "-crf"), ("h264_qsv", "-global_quality"),
                                        ("h264_amf", "-qp_i")]:
            values = [engine._encoder_args(encoder, mode) for mode in [False, True]]
            self.assertEqual([args[args.index(quality_option) + 1] for args in values], ["19", "19"])

    def test_range_download_keeps_frame_accurate_cuts_in_both_modes(self):
        for fast in [True, False]:
            opts = engine._make_ytdlp_opts("trim", {
                "Url": "https://example.com/video", "SegmentStart": "2.35",
                "SegmentEnd": "5.1", "FastMode": fast}, self.work, FFMPEG, False)
            self.assertTrue(opts["force_keyframes_at_cuts"])
            self.assertIn("download_ranges", opts)

    def test_fallback_trim_and_precise_cut(self):
        source = self.work / "long.mp4"
        ffmpeg("-f", "lavfi", "-i", "testsrc2=s=640x360:r=30:d=8",
               "-f", "lavfi", "-i", "sine=sample_rate=48000:duration=8",
               "-c:v", "libx264", "-preset", "ultrafast", "-crf", "19", "-g", "30",
               "-c:a", "aac", str(source))
        durations = {}
        for fast in [True, False]:
            work = self.work / ("fast-trim" if fast else "precise-trim")
            work.mkdir(exist_ok=True)
            request = {"Format": "mp4", "FastMode": fast,
                       "SegmentStart": "2.35", "SegmentEnd": "5.1"}
            started = time.perf_counter()
            output = engine.process_file(source, request, FFMPEG, work, "trim")
            durations[fast] = time.perf_counter() - started
            duration = engine._probe_duration_seconds(output)
            self.assertAlmostEqual(duration, 2.75, delta=.1,
                                   msg="Both boundaries must be accurate")
        print(f"TRIM_BENCHMARK: copy={durations[True]:.4f}s precise={durations[False]:.4f}s")

    def test_already_trimmed_download_is_not_cut_twice(self):
        with patch.object(engine, "segment_media") as cut, \
             patch.object(engine, "repair_mp4_audio_start", side_effect=lambda src, dst, ffmpeg, job: src):
            result = engine.process_file(self.mp4, {"Format": "mp4", "SegmentStart": "20",
                "SegmentEnd": "25"}, FFMPEG, self.work, "trim", segment_applied=True)
        cut.assert_not_called()
        self.assertEqual(result, self.mp4)

    def test_single_job_passes_download_trim_state(self):
        for already_cut in [True, False]:
            with patch.object(engine, "download_media", return_value={"files": [self.mp4],
                    "title": "trim-check", "segment_applied": already_cut}), \
                 patch.object(engine, "process_file", return_value=self.mp4) as process, \
                 patch.object(engine, "finalize", return_value=self.work / "trim-check.mp4"):
                engine.process_job({"JobId": "trim-state", "Request": {
                    "Url": "https://example.com/video", "Format": "mp4",
                    "SegmentStart": "1", "SegmentEnd": "2"}})
                self.assertEqual(process.call_args.kwargs["segment_applied"], already_cut)

    @classmethod
    def tearDownClass(cls):
        # Compare the former three-process pattern to the shared probe path.
        def measure(shared):
            started = time.perf_counter()
            for _ in range(3):
                if not shared:
                    _probe_cached.cache_clear()
                engine._probe_media(cls.mp4)
            return time.perf_counter() - started
        _probe_cached.cache_clear()
        old = measure(False)
        _probe_cached.cache_clear()
        new = measure(True)
        print(f"PROBE_BENCHMARK: three_uncached={old:.4f}s shared={new:.4f}s", flush=True)


if __name__ == "__main__":
    try:
        unittest.main(verbosity=2)
    finally:
        engine._job_pool.shutdown(wait=True)
        if engine._log_handler:
            engine._log_handler.close()
        TEMP.cleanup()
