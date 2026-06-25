CREATE TABLE IF NOT EXISTS user_skin_profiles (
  user_id INTEGER PRIMARY KEY,
  published_uuid TEXT NOT NULL,
  offline_uuid TEXT NOT NULL,
  texture_value TEXT NOT NULL,
  texture_signature TEXT,
  texture_url TEXT,
  updated_at_utc TEXT NOT NULL,
  FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_user_skin_profiles_published_uuid
  ON user_skin_profiles(published_uuid);

CREATE UNIQUE INDEX IF NOT EXISTS idx_user_skin_profiles_offline_uuid
  ON user_skin_profiles(offline_uuid);
