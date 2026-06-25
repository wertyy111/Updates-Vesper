ALTER TABLE user_game_activity ADD COLUMN relay_room_id TEXT;
ALTER TABLE user_game_activity ADD COLUMN relay_transport_mode TEXT;

CREATE TABLE IF NOT EXISTS relay_sessions (
  room_id TEXT PRIMARY KEY,
  host_user_id INTEGER NOT NULL,
  transport_mode TEXT NOT NULL,
  is_active INTEGER NOT NULL DEFAULT 1,
  created_at_utc TEXT NOT NULL,
  updated_at_utc TEXT NOT NULL,
  FOREIGN KEY (host_user_id) REFERENCES users(id) ON DELETE CASCADE
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_relay_sessions_host_user
  ON relay_sessions(host_user_id);

CREATE TABLE IF NOT EXISTS relay_connections (
  connection_id TEXT PRIMARY KEY,
  room_id TEXT NOT NULL,
  host_user_id INTEGER NOT NULL,
  guest_user_id INTEGER NOT NULL,
  status TEXT NOT NULL,
  created_at_utc TEXT NOT NULL,
  updated_at_utc TEXT NOT NULL,
  closed_at_utc TEXT,
  FOREIGN KEY (host_user_id) REFERENCES users(id) ON DELETE CASCADE,
  FOREIGN KEY (guest_user_id) REFERENCES users(id) ON DELETE CASCADE,
  FOREIGN KEY (room_id) REFERENCES relay_sessions(room_id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_relay_connections_host_status
  ON relay_connections(host_user_id, status, created_at_utc);
