"""A/B on the newest session's audio.wav (16k mono PCM16):
  A) file endpoint  -> ground truth: what the audio actually contains
  B) realtime replay -> upsample 16k->48k, declare 48000, 100ms frames,
     real-time paced (the app's exact wire shape)
If B reproduces the live truncation, the server drops audio mid-stream at
today's load; if B is complete, the fault is inside the live process.
"""

import asyncio
import io
import json
import os
import sys
import wave

import httpx
import numpy as np
import websockets

BASE = "http://127.0.0.1:8080"
SESSIONS = os.path.join(os.environ["APPDATA"], "Athena", "sessions")


def load_wav(path):
    data = open(path, "rb").read()
    pos = 12
    fmt = None
    pcm = None
    while pos + 8 <= len(data):
        tag = data[pos:pos + 4]
        size = int.from_bytes(data[pos + 4:pos + 8], "little")
        body = data[pos + 8:pos + 8 + size]
        if tag == b"fmt ":
            fmt = struct_unpack_fmt(body)
        elif tag == b"data":
            pcm = body
        pos += 8 + size + (size % 2)
    assert fmt and fmt["bits"] == 16, f"unexpected fmt {fmt}"
    x = np.frombuffer(pcm, dtype="<i2").astype(np.float32) / 32768.0
    return x, fmt["rate"]


def struct_unpack_fmt(body):
    import struct
    tag, ch, rate = struct.unpack("<HHI", body[:8])
    bits = int.from_bytes(body[14:16], "little")
    return {"tag": tag, "ch": ch, "rate": rate, "bits": bits}


def upsample_linear(x, src, dst):
    if src == dst:
        return x
    n = int(round(len(x) * dst / src))
    return np.interp(np.linspace(0, len(x) - 1, n), np.arange(len(x)), x).astype(np.float32)


async def file_transcribe(path):
    wav = open(path, "rb").read()
    async with httpx.AsyncClient(timeout=120) as c:
        r = await c.post(f"{BASE}/v1/audio/transcriptions",
                         files={"file": ("audio.wav", wav, "audio/wav")},
                         data={"model": "asr"})
        return r.text.strip()


async def realtime_replay(x16, declared_rate=48000, paced=True):
    x = upsample_linear(x16, 16000, declared_rate)
    pcm16 = np.clip(x * 32767, -32768, 32767).astype("<i2").tobytes()
    frame = int(declared_rate / 10) * 2  # 100ms
    chunks = [pcm16[i:i + frame] for i in range(0, len(pcm16), frame)]
    final = []
    async with websockets.connect(
        BASE.replace("http", "ws", 1) + "/v1/audio/transcriptions/realtime",
        max_size=None,
    ) as ws:
        await ws.send(json.dumps({"type": "session.update", "session": {
            "sample_rate": declared_rate, "language": "en-US",
            "automatic_punctuation": True}}))
        async for ev in ws:
            if json.loads(ev).get("type") == "session.updated":
                break
        async def send():
            for c in chunks:
                await ws.send(c)
                if paced:
                    await asyncio.sleep(0.1)
        async def recv():
            try:
                async for ev in ws:
                    d = json.loads(ev)
                    t = d.get("type", "?")
                    if t.endswith(".delta"):
                        deltas.append(d.get("delta") or "")
                        if len(deltas) % 20 == 1:
                            print(f"  [delta #{len(deltas)}: {d.get('audio_processed', '?')}s decoded]")
                    elif t == "error":
                        print(f"  [server error: {d}]")
                    else:
                        print(f"  [event: {t}]")
                    if t.endswith(".completed"):
                        final.append(d.get("transcript") or d.get("text") or "")
            except websockets.ConnectionClosed as e:
                print(f"  [socket closed: code={e.code} reason={e.reason!r}]")
        deltas = []
        r = asyncio.create_task(recv())
        await send()
        await ws.send(json.dumps({"type": "input_audio_buffer.commit"}))
        try:
            await asyncio.wait_for(r, timeout=60)
        except asyncio.TimeoutError:
            print(f"  [recv timeout — {len(deltas)} deltas seen]")
        await ws.close()
    return " ".join(final).strip()


async def main():
    newest = max((os.path.join(SESSIONS, d) for d in os.listdir(SESSIONS)),
                 key=lambda d: os.path.getmtime(d))
    wav = os.path.join(newest, "audio.wav")
    x, rate = load_wav(wav)
    dur = len(x) / rate
    print(f"session: {os.path.basename(newest)}  audio.wav: {dur:.2f}s @ {rate}Hz")

    print("\nA) file endpoint:")
    a = await file_transcribe(wav)
    print(f"  {len(a.split())} words: {a}")

    print("\nB) realtime replay (declared 48000, 100ms frames, paced):")
    b = await realtime_replay(x)
    print(f"  {len(b.split())} words: {b}")

    print("\nC) realtime replay (same audio, UNPACED burst):")
    c = await realtime_replay(x, paced=False)
    print(f"  {len(c.split())} words: {c}")

    wa, wb, wc = len(a.split()), len(b.split()), len(c.split())
    print(f"\nverdict: file={wa}w  paced-replay={wb}w  unpaced-replay={wc}w")


if __name__ == "__main__":
    asyncio.run(main())
