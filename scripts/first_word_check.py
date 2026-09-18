"""Diagnose 'first word guessed wrong': compare raw ASR vs LLM-cleaned first
words across recent history rows to localize the fault (ASR vs cleanup)."""

import os
import sqlite3

db = os.path.join(os.environ["APPDATA"], "Athena", "history.db")
conn = sqlite3.connect(db)
conn.row_factory = sqlite3.Row

rows = conn.execute(
    "SELECT started_at, raw_transcript, cleaned_transcript, status "
    "FROM dictations WHERE raw_transcript IS NOT NULL AND cleaned_transcript IS NOT NULL "
    "ORDER BY started_at DESC LIMIT 25"
).fetchall()

print(f"{len(rows)} recent rows with both transcripts\n")
for r in reversed(rows):  # chronological
    raw = (r["raw_transcript"] or "").strip()
    clean = (r["cleaned_transcript"] or "").strip()
    first_raw = raw.split()[0] if raw.split() else "(empty)"
    first_clean = clean.split()[0] if clean.split() else "(empty)"
    flag = ("  <-- FIRST WORD DIFFERS"
            if first_raw.lower().strip(".,!?") != first_clean.lower().strip(".,!?") else "")
    print(f"{r['started_at'][:19]}  [{r['status']}]")
    print(f"  raw  : {raw}")
    print(f"  clean: {clean}{flag}")
