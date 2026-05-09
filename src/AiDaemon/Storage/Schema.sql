CREATE TABLE IF NOT EXISTS branches (
  branch          TEXT PRIMARY KEY,
  session_id      TEXT NOT NULL,
  worktree        TEXT NOT NULL,
  mode            TEXT NOT NULL,
  rc_pid          INTEGER,
  rc_claude_pid   INTEGER,
  rc_claude_start INTEGER,
  rc_bridge_id    TEXT,
  rc_url          TEXT,
  last_event_at   INTEGER NOT NULL,
  pr_number       INTEGER,
  issue_number    INTEGER
);

CREATE TABLE IF NOT EXISTS processed (
  thread_id       TEXT NOT NULL,
  comment_id      TEXT NOT NULL,
  processed_at    INTEGER NOT NULL,
  outcome         TEXT NOT NULL,
  PRIMARY KEY (thread_id, comment_id)
);

CREATE INDEX IF NOT EXISTS ix_processed_processed_at ON processed (processed_at);

CREATE TABLE IF NOT EXISTS rate_limits (
  thread_id       TEXT NOT NULL,
  day             TEXT NOT NULL,
  count           INTEGER NOT NULL,
  PRIMARY KEY (thread_id, day)
);
