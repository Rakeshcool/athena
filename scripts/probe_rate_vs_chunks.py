# Isolate the live word-loss: the server's own web client streams NATIVE-rate
# (48k) mono PCM16 and is flawless; the app resamples to 16k, declares 16000,
# and drops words on long dictations. Vary ONE factor at a time on the same
# session audio:
#   A: declared 48000, 100ms chunks   (probe baseline — known good)
#   B: declared 48000, 10ms chunks    (app cadence, native rate)
#   C: declared 16000, 100ms chunks   (app rate, web cadence)
#   D: declared 16000, 10ms chunks    (exact app mirror)
# uv run --with websockets --with numpy scripts/probe_rate_vs_chunks.py

import asyncio
import json
import os
import struct
import sys
import time
from pathlib import Path

import numpy as np
import websockets

URL = "ws://127.0.0.1:8080/v1/audio/transcriptions/realtime"
SID = "fbb84b53fb6c47d98077b7f036ba99ad"
WAV = Path(os.environ["APPDATA"]) / "Athena" / "sessions" / SID / "audio.wav"


def load_mono48(path: Path) -> tuple[np.ndarray, int]:
    with open(path, "rb") as f:
        data = f.read()
    off, fmt, body = 12, None, None
    while off + 8 <= len(data):
        cid = data[off : off + 4]
        (size,) = struct.unpack("<I", data[off + 4 : off + 8])
        chunk = data[off + 8 : off + 8 + size]
        if cid == b"fmt ":
            fmt = chunk
        elif cid == b"data":
            body = chunk
        off += 8 + size + (size & 1)
    ch = struct.unpack("<H", fmt[2:4])[0]
    a = np.frombuffer(body, dtype="<f4").copy()
    if ch == 2:
        a = a[0::2]
    return np.clip(a, -1, 1), 48000


def decimate16(mono48: np.ndarray) -> np.ndarray:
    # Crude but word-preserving 48k→16k: average each 3-sample triplet
    # (box lowpass) then take every third sample.
    n = len(mono48) - len(mono48) % 3
    return mono48[:n].reshape(-1, 3).mean(axis=1).astype(np.float32)


async def stream_once(mono: np.ndarray, declared_rate: int, chunk_ms: int) -> str:
    pcm16 = (np.clip(mono, -1, 1) * 32767).astype("<i2")
    raw = pcm16.tobytes()
    chunk_bytes = int(declared_rate * chunk_ms / 1000) * 2

    async with websockets.connect(URL, max_size=2**23) as ws:
        await ws.send(json.dumps({"type": "session.update", "session": {
            "sample_rate": declared_rate, "language": "en-US",
            "automatic_punctuation": True}}))
        finals: list[str] = []
        partial = ""
        ncompleted = 0

        async def reader() -> None:
            nonlocal partial, ncompleted
            try:
                async for msg in ws:
                    if isinstance(msg, bytes):
                        continue
                    ev = json.loads(msg)
                    t = ev.get("type", "")
                    if t.endswith(".delta"):
                        partial += ev.get("delta") or ""
                    elif t.endswith(".completed"):
                        ncompleted += 1
                        finals.append(ev.get("transcript") or ev.get("text") or "")
                        partial = ""
            except websockets.ConnectionClosed:
                pass

        task = asyncio.create_task(reader())
        for off in range(0, len(raw), chunk_bytes):
            await ws.send(raw[off : off + chunk_bytes])
            await asyncio.sleep(0.02)  # app pump cadence
        await ws.send(json.dumps({"type": "input_audio_buffer.commit"}))
        await asyncio.sleep(5)
        task.cancel()
        text = " ".join(x for x in finals if x).strip()
        if partial.strip():
            text = (text + " " + partial).strip()
        return text, ncompleted


async def main() -> None:
    mono48, _ = load_mono48(WAV)
    mono16 = decimate16(mono48)
    print(f"audio: {len(mono48) / 48000:.1f}s\n")

    cases = [
        ("A  rate=48000 chunk=100ms", mono48, 48000, 100),
        ("B  rate=48000 chunk=10ms  ", mono48, 48000, 10),
        ("C  rate=16000 chunk=100ms ", mono16, 16000, 100),
        ("D  rate=16000 chunk=10ms  ", mono16, 16000, 10),
    ]
    for label, mono, rate, chunk_ms in cases:
        text, ncomp = await stream_once(mono, rate, chunk_ms)
        print(f"== {label}  (completed={ncomp})")
        print(text[:220], "\n")


if __name__ == "__main__":
    asyncio.run(main())
