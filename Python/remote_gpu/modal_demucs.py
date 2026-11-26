"""Reference remote-GPU endpoint for NepulaServer vocal extraction (Modal).

Deploy: modal deploy Python/remote_gpu/modal_demucs.py
Contract: POST multipart field "audio"; returns vocals as PCM WAV.
Not yet run in this repo: test once before production use.
"""
import io
import os
import tempfile

import modal

image = (
    modal.Image.debian_slim(python_version="3.11")
    .apt_install("ffmpeg")
    .pip_install("torch==2.5.1", "torchaudio==2.5.1", "demucs==4.0.1", "soundfile", "fastapi[standard]", "python-multipart")
    .run_commands("python -c \"from demucs.pretrained import get_model; get_model('htdemucs')\"")
)

app = modal.App("nebula-demucs", image=image)


@app.cls(gpu="L4", scaledown_window=120, timeout=600, secrets=[modal.Secret.from_name("nebula-demucs-token")])
class Separator:
    @modal.enter()
    def load(self):
        import torch
        from demucs.pretrained import get_model

        self.torch = torch
        self.model = get_model("htdemucs").to("cuda").eval()

    @modal.fastapi_endpoint(method="POST")
    async def separate(self, request):
        from fastapi import HTTPException

        expected = os.environ.get("DEMUCS_TOKEN", "")
        if not expected or request.headers.get("authorization", "") != f"Bearer {expected}":
            raise HTTPException(status_code=401, detail="unauthorized")

        form = await request.form()
        upload = form.get("audio")
        if upload is None:
            raise HTTPException(status_code=400, detail="missing audio field")

        import soundfile as sf
        from demucs.apply import apply_model
        from demucs.audio import convert_audio
        from fastapi.responses import Response

        with tempfile.NamedTemporaryFile(suffix=".bin") as tmp:
            tmp.write(await upload.read())
            tmp.flush()
            import subprocess

            wav_path = tmp.name + ".wav"
            subprocess.run(["ffmpeg", "-y", "-i", tmp.name, "-vn", "-ac", "2", "-ar", "44100", "-c:a", "pcm_s16le", wav_path], check=True, capture_output=True)
            samples, sr = sf.read(wav_path, always_2d=True, dtype="float32")
            os.unlink(wav_path)

        torch = self.torch
        wave_t = convert_audio(torch.from_numpy(samples.T), sr, self.model.samplerate, self.model.audio_channels).to("cuda")
        ref = wave_t.mean(0)
        std = ref.std().clamp(min=1e-8)
        with torch.no_grad():
            out = apply_model(self.model, ((wave_t - ref.mean()) / std)[None], device="cuda", shifts=1, split=True, overlap=0.25, progress=False)[0]
        out = out * std + ref.mean()
        vocals = out[self.model.sources.index("vocals")].cpu().numpy().T
        buf = io.BytesIO()
        sf.write(buf, vocals, self.model.samplerate, format="WAV", subtype="PCM_16")
        return Response(content=buf.getvalue(), media_type="audio/wav")
