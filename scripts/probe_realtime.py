# Probe the ASR realtime WebSocket: connect, session.update, stream SAPI
# speech PCM, print EVERY event, commit, drain. Finds the exact handshake
# the server expects so the C# client can mirror it.
#   uv run --with websockets scripts/probe_realtime.py

import asyncio
import json
import subprocess
import sys
import tempfile
import wave
from pathlib import Path

import websockets

TEXT = "The quick brown fox jumps over the lazy dog."
URL = "ws://127.0.0.1:8080/v1/realtime"


def synth_wav() -> tuple[bytes, int]:
    wav = Path(tempfile.gettempdir()) / f"athena-probe-{asyncio.get_event_loop().time()}.wav"
    ps = (
        "Add-Type -AssemblyName System.Speech;"
        "$s=New-Object System.Speech.Synthesis.SpeechSynthesizer;"
        f"$s.SetOutputToWaveFile('{wav}');"
        f"$s.Speak('{TEXT}');"
        "$s.Dispose()"
    )
    subprocess.run(
        ["powershell", "-NoProfile", "-Command", ps],
        check=True, capture_output=True, timeout=60,
    )
    with wave.open(str(wav), "rb") as w:
        rate = w.getframerate()
        pcm = w.readframes(w.getnframes())
    wav.unlink(missing_ok=True)
    return pcm, rate


async def main() -> None:
    pcm, rate = synth_wav()
    print(f"pcm bytes={len(pcm)} rate={rate} dur={len(pcm)/(2*rate):.2f}s")

    async with websockets.connect(URL, max_size=2**23) as ws:
        session = {
            "type": "session.update",
            "session": {
                "sample_rate": rate,
                "language": "en-US",
                "automatic_punctuation": True,
            },
        }
        print(f">>> {json.dumps(session)}")
        await ws.send(json.dumps(session))

        # Stream audio in 100ms chunks while reading events concurrently.
        async def reader() -> None:
            try:
                async for msg in ws:
                    if isinstance(msg, bytes):
                        print(f"<<< [binary {len(msg)}B]")
                    else:
                        print(f"<<< {msg[:400]}")
            except websockets.ConnectionClosed as e:
                print(f"<<< closed: {e}")

        read_task = asyncio.create_task(reader())

        chunk = 2 * rate // 10  # 100ms
        for off in range(0, len(pcm), chunk):
            await ws.send(pcm[off : off + chunk])
            await asyncio.sleep(0.02)

        await asyncio.sleep(0.5)  # let deltas flow live
        print(">>> commit")
        await ws.send(json.dumps({"type": "input_audio_buffer.commit"}))
        await asyncio.sleep(6)  # drain finals
        read_task.cancel()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        sys.exit(0)
