# Remote GPU for vocal extraction (optional)

Off by default. When `NEBULA_REMOTE_DEMUCS_URL` is not set, NepulaServer runs Demucs locally exactly as before.

## Settings (environment variables on the server)

| Variable | Meaning |
| --- | --- |
| `NEBULA_REMOTE_DEMUCS_URL` | HTTPS endpoint. Must start with `https://`. Leave empty to keep the feature off. |
| `NEBULA_REMOTE_DEMUCS_TOKEN` | Optional. Sent as `Authorization: Bearer <token>`. |
| `NEBULA_REMOTE_DEMUCS_TIMEOUT` | Optional. Seconds to wait for the result. Default 300, minimum 10. |

Keep the token out of Git. Set it in the server's environment or secret store.

## Endpoint contract

`POST` multipart form with one file field named `audio` (WAV or MP3). The response body is the vocals as a PCM WAV. Any error status, timeout, or invalid audio makes the server log the reason and fall back to local Demucs for that job.

## Example: Modal

`modal_demucs.py` is a reference endpoint. It has not been run yet, so test it once before relying on it.

1. `pip install modal` and `modal setup`.
2. `modal secret create nebula-demucs-token DEMUCS_TOKEN=<long random value>`
3. `modal deploy Python/remote_gpu/modal_demucs.py` and copy the printed HTTPS URL.
4. Set `NEBULA_REMOTE_DEMUCS_URL` and `NEBULA_REMOTE_DEMUCS_TOKEN` on the server.

Modal bills per second of GPU time and scales to zero when idle. Set a spending limit in the Modal workspace settings.
