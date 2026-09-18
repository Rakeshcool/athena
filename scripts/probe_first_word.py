# A/B probe: the homophone first-word problem. Stream the same ambiguous
# sentence ("write a program to find the magic number") with and without
# speech_contexts boosting, and compare the finals. Verifies that dictionary
# boosting disambiguates utterance-initial homophones that a streaming
# recognizer — which sees the first word with zero left context — guesses
# wrong unaided ("Right program…", "Ryder program…").
#   uv run --with websockets scripts/probe_first_word.py

import asyncio
import json
import subprocess
import sys
import tempfile
import wave
from pathlib import Path

import websockets

SENTENCES = [
    "Write a program to find the magic number.",
    "Write a program to find the match number.",
]
URL = "ws://127.0.0.1:8080/v1/audio/transcriptions/realtime"
BOOST = 3.0


def synth_wav(text: str) -> tuple[bytes, int]:
    wav = Path(tempfile.gettempdir()) / f"athena-probe-{abs(hash(text))}.wav"
    ps = (
        "Add-Type -AssemblyName System.Speech;"
        "$s=New-Object System.Speech.Synthesis.SpeechSynthesizer;"
        f"$s.SetOutputToWaveFile('{wav}');"
        f"$s.Speak('{text}');"
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


async def stream_once(pcm: bytes, rate: int, boost: list[str] | None) -> str:
    async with websockets.connect(URL, max_size=2**23) as ws:
        session = {
            "type": "session.update",
            "session": {
                "sample_rate": rate,
                "language": "en-US",
                "automatic_punctuation": True,
            },
        }
        if boost:
            session["session"]["speech_contexts"] = {"phrases": boost, "boost": BOOST}
        await ws.send(json.dumps(session))

        finals: list[str] = []

        async def reader() -> None:
            try:
                async for msg in ws:
                    if isinstance(msg, bytes):
                        continue
                    ev = json.loads(msg)
                    t = ev.get("type", "")
                    if "completed" in t:
                        finals.append(ev.get("transcript") or ev.get("delta") or "")
            except websockets.ConnectionClosed:
                pass

        read_task = asyncio.create_task(reader())
        chunk = 2 * rate // 10  # 100ms — same cadence as the app
        for off in range(0, len(pcm), chunk):
            await ws.send(pcm[off : off + chunk])
            await asyncio.sleep(0.02)
        await ws.send(json.dumps({"type": "input_audio_buffer.commit"}))
        await asyncio.sleep(4)
        read_task.cancel()
        return " ".join(x for x in finals if x).strip()


async def main() -> None:
    for sentence in SENTENCES:
        pcm, rate = synth_wav(sentence)
        plain = await stream_once(pcm, rate, None)
        boosted = await stream_once(pcm, rate, ["write a program", "magic number"])
        print(f"\nSAID   : {sentence}")
        print(f"plain  : {plain}")
        print(f"boosted: {boosted}")


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        sys.exit(0)
