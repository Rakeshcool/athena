# Diagnostic: dump the most recent history rows.
# Run: uv run scripts/last_history.py

import sqlite3
import os

db = os.path.join(os.environ["APPDATA"], "Athena", "history.db")
conn = sqlite3.connect(db)
conn.row_factory = sqlite3.Row
rows = conn.execute(
    "SELECT started_at, status, target_app_name, raw_transcript, cleaned_transcript"
    " FROM dictations ORDER BY started_at DESC LIMIT 5").fetchall()
for r in rows:
    print(dict(r))
conn.close()
