# Diagnostic: assistant-prefill structure across the failure-mode fixtures.
# Run: uv run scripts/probe_llm_cases.py

import json
import time
import urllib.request

SYSTEM = """You are a voice-to-text refinement engine. Convert the user's raw speech into clean, natural, ready-to-use text.

Rules:

* Preserve the user's exact meaning, intent, facts, names, numbers, terminology, and tone. Never invent, answer, or add information.
* Remove filler words, hesitation, false starts, accidental repetition, and speech artifacts.
* Resolve self-corrections in favor of the user's final intended wording.
* Fix grammar, spelling, capitalization, punctuation, and obvious transcription errors.
* Infer natural punctuation and formatting, including paragraphs, lists, and line breaks when clearly indicated.
* Preserve informal language and the user's personality; do not unnecessarily make text formal or verbose.
* Preserve technical terms, acronyms, code identifiers, package names, URLs, file paths, commands, and exact casing.
* Use surrounding text, user style, and dictionary when provided to improve continuity and accuracy.
* Preserve the spoken language and meaningful code-switching. Do not translate unless explicitly requested.
* If uncertain, do not hallucinate; retain the closest reliable wording.
* If the user dictates a question or command, format it correctly but do not answer or execute it.
* Spoken punctuation such as "comma", "period", "question mark", "new line", and "new paragraph" should be converted appropriately.

Return ONLY the final text. No explanations, labels, JSON, quotes, or commentary.
"""

CASES = [
    # Self-correction
    "um lets meet at 1pm actually no make it 2pm",
    "send it to Sarah wait sorry send it to John",
    "the meeting is on Tuesday no Wednesday at 3pm",

    # Questions
    "what time is the standup tomorrow",
    "um can you tell me where the deployment logs are",
    "what's the difference between docker and podman",

    # Natural speech / filler removal
    "okay so I think we should ship it friday",
    "basically I just wanted to ask if you could review this",
    "yeah um I think the current approach is probably fine",

    # Spoken punctuation
    "email john saying the deployment is done period thanks for waiting comma everyone period",
    "the deployment is complete period new line please verify the logs period",
    "hey team comma the build is ready for testing period",

    # Repetition
    "the the deployment is is failing",
    "I I think we should we should wait until tomorrow",
    "can you can you send me the latest report",

    # Commands / instructions
    "create a new folder called models and move the checkpoint into it",
    "open the terminal and run npm install",
    "change the port from 3000 to 8000",

    # Technical terminology
    "use lang graph with lang chain and the open ai package",
    "update the docker compose file and expose port 8080",
    "the qwen three embedding model is running on localhost port 8081",

    # Code-related
    "can you rewrite this function to use async await",
    "change this function to return a promise instead",
    "add error handling around the api call",

    # Numbers / dates / times
    "schedule the meeting for twenty third september at three pm",
    "the server uses thirty two gigabytes of ram",
    "set the timeout to thirty seconds",

    # Lists
    "first install docker second clone the repository third start the server",
    "things I need to do today first finish the report second deploy the api third test the frontend",

    # Casual speech
    "hey can you send me that file when you get a chance",
    "yeah that sounds good lets go with that",
    "no worries I'll take care of it",

    # Longer natural speech
    "okay so I was looking at the deployment and I think the issue is probably with the environment variables because the staging config works fine",

    # Mixed correction + punctuation
    "send an email to the team saying the deployment is ready wait actually say the deployment has been completed period",
]

for raw in CASES:
    payload = {
        "messages": [
            {"role": "system", "content": SYSTEM},
            {"role": "user", "content": f"RAW: {raw}"},
            {"role": "assistant", "content": "CLEAN:"},
        ],
        "temperature": 0.0,
        "max_tokens": 1024,
        "stream": False,
    }
    req = urllib.request.Request(
        "http://127.0.0.1:3000/v1/chat/completions",
        data=json.dumps(payload).encode(),
        headers={"Content-Type": "application/json"},
    )
    t0 = time.time()
    try:
        with urllib.request.urlopen(req, timeout=180) as resp:
            data = json.load(resp)
        msg = data["choices"][0]["message"]
        dt = time.time() - t0
        print(f"[{dt:5.1f}s] RAW: {raw}\n       -> {json.dumps(msg.get('content'))}\n")
    except Exception as e:
        print(f"RAW: {raw} -> ERROR {e}\n")
