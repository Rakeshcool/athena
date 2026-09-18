# Diagnostic: try the knobs that disable/limit reasoning on llama.cpp server.
# Run: uv run scripts/probe_llm_nothink.py

import json
import urllib.request

SYSTEM = "You clean up dictated transcripts. Output ONLY the cleaned text. No preamble, no quotes, no commentary."
USER = """You clean up dictated transcripts. Rewrite the raw transcript below into polished written text.

Examples:
RAW: um so let's meet at 2 actually no 3 on thursday
CLEAN: Let's meet at 3 on Thursday.

RAW: um lets meet at 1pm actually no make it 2pm
CLEAN:"""

def try_payload(name, extra):
    payload = {
        "messages": [
            {"role": "system", "content": SYSTEM},
            {"role": "user", "content": USER},
        ],
        "temperature": 0.0,
        "max_tokens": 512,
        "stream": False,
    }
    payload.update(extra)
    req = urllib.request.Request(
        "http://127.0.0.1:3000/v1/chat/completions",
        data=json.dumps(payload).encode(),
        headers={"Content-Type": "application/json"},
    )
    try:
        with urllib.request.urlopen(req, timeout=120) as resp:
            data = json.load(resp)
        msg = data["choices"][0]["message"]
        reason = msg.get("reasoning_content") or ""
        print(f"--- {name}: finish={data['choices'][0].get('finish_reason')} "
              f"content={json.dumps(msg.get('content'))[:120]} reasoning_len={len(reason)}")
    except Exception as e:
        print(f"--- {name}: ERROR {e}")

try_payload("baseline", {})
try_payload("enable_thinking=false", {"chat_template_kwargs": {"enable_thinking": False}})
try_payload("reasoning_budget=0", {"reasoning_budget": 0})
try_payload("chat_template_kwargs thinking=false", {"chat_template_kwargs": {"thinking": False}})
