# Diagnose word loss in the realtime path: replay a session's audio.wav three
# ways and compare transcripts —
#   1. realtime WebSocket at the app's exact pacing (100ms chunks every ~20ms,
#      i.e. ~5x realtime — faster than speech, which is how the live pump runs)
#   2. realtime WebSocket with no pacing at all
#   3. the file endpoint (ground truth: whole-utterance decode)
# If 1/2 drop words that 3 gets right, the loss is in the realtime path
# (server-side buffering under faster-than-realtime delivery, most likely);
# if 1 matches 3, the live loss was client-side audio delivery.
#   uv run --with websockets --with httpx scripts/probe_stream_loss.py [wav]

import asyncio
import json
import os
import sys
import time
from pathlib import Path

import httpx
import numpy as np
import websockets

URL = "ws://127.0.0.1:8080/v1/audio/transcriptions/realtime"
FILE_URL = "http://127.0.0.1:8080/v1/audio/transcriptions"

SID = "fbb84b53fb6c47d98077b7f036ba99ad"
DEFAULT_WAV = Path(os.environ["APPDATA"]) / "Athena" / "sessions" / SID / "audio.wav"


import struct


def load_wav(path: Path) -> tuple[bytes, int]:
    """Return PCM16 MONO bytes + source rate. Session audio.wav files are the
    raw capture: 48k IEEE-float32 stereo — stdlib wave can't read fmt tag 3,
    so parse RIFF chunks directly. Downmix and convert to 16-bit, the format
    the app streams, letting the server resample."""
    with open(path, "rb") as f:
        data = f.read()
    if data[:4] != b"RIFF" or data[8:12] != b"WAVE":
        raise ValueError("not a RIFF/WAVE file")
    off = 12
    fmt = datach = None
    while off + 8 <= len(data):
        cid = data[off : off + 4]
        (size,) = struct.unpack("<I", data[off + 4 : off + 8])
        body = data[off + 8 : off + 8 + size]
        if cid == b"fmt ":
            fmt = body
        elif cid == b"data":
            datach = body
        off += 8 + size + (size & 1)
    if fmt is None or datach is None:
        raise ValueError("missing fmt/data chunk")
    tag, ch, rate = struct.unpack("<HHI", fmt[0:8])
    bits = struct.unpack("<H", fmt[14:16])[0]
    if tag == 3 and bits == 32:  # IEEE float32
        a = np.frombuffer(datach, dtype="<f4").copy()
    elif bits == 16:
        a = np.frombuffer(datach, dtype="<i2").astype(np.float32) / 32768.0
    else:
        raise ValueError(f"unsupported format tag={tag} bits={bits}")
    if ch == 2:
        a = a[0::2]  # downmix: drop R (WASAPI shared mixes L=R)
    a = np.clip(a, -1.0, 1.0)
    return (a * 32767).astype("<i2").tobytes(), rate


async def stream_once(pcm: bytes, rate: int, pace_s: float | None, label: str) -> str:
    async with websockets.connect(URL, max_size=2**23) as ws:
        await ws.send(json.dumps({"type": "session.update", "session": {
            "sample_rate": rate, "language": "en-US",
            "automatic_punctuation": True}}))

        finals: list[str] = []
        partial = ""
        events = {"delta": 0, "completed": 0}
        completed_times: list[float] = []
        t0 = time.monotonic()

        async def reader() -> None:
            nonlocal partial
            try:
                async for msg in ws:
                    if isinstance(msg, bytes):
                        continue
                    ev = json.loads(msg)
                    t = ev.get("type", "")
                    if t.endswith(".delta"):
                        events["delta"] += 1
                        partial += ev.get("delta") or ""
                    elif t.endswith(".completed"):
                        events["completed"] += 1
                        completed_times.append(time.monotonic() - t0)
                        finals.append(ev.get("transcript") or ev.get("text") or "")
                        partial = ""
            except websockets.ConnectionClosed:
                pass

        read_task = asyncio.create_task(reader())
        chunk = 3200 * max(1, rate // 16000)  # 100ms of mono 16-bit
        sent = 0
        for off in range(0, len(pcm), chunk):
            await ws.send(pcm[off : off + chunk])
            sent += min(chunk, len(pcm) - off)
            if pace_s:
                await asyncio.sleep(pace_s)
        await ws.send(json.dumps({"type": "input_audio_buffer.commit"}))
        await asyncio.sleep(5)
        read_task.cancel()
        text = " ".join(x for x in finals if x).strip()
        if partial.strip():
            text = (text + " " + partial).strip()
        print(f"  [{label}] sent {sent / 1024:.0f} KB, "
              f"events {events}, finals at "
              f"{[f'{t:.1f}s' for t in completed_times]}")
        return text


async def file_once(path: Path) -> str:
    async with httpx.AsyncClient(timeout=60) as c:
        r = await c.post(
            FILE_URL,
            files={"file": (path.name, path.read_bytes(), "audio/wav")},
            data={"language": "en-US"},
        )
        r.raise_for_status()
        return r.json().get("text", "")


async def main() -> None:
    wav = Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT_WAV
    pcm, rate = load_wav(wav)
    print(f"audio: {wav.name} — {len(pcm) / 2 / rate:.1f}s at {rate} Hz\n")

    print("== realtime, app pacing (20ms sleep per 100ms chunk) ==")
    paced = await stream_once(pcm, rate, 0.02, "paced")
    print(paced, "\n")

    print("== realtime, no pacing ==")
    fast = await stream_once(pcm, rate, None, "fast")
    print(fast, "\n")

    print("== file endpoint (ground truth) ==")
    truth = await file_once(wav)
    print(truth)


if __name__ == "__main__":
    asyncio.run(main())
