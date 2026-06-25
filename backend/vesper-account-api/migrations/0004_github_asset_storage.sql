ALTER TABLE user_avatars ADD COLUMN storage_provider TEXT;
ALTER TABLE user_avatars ADD COLUMN storage_path TEXT;
ALTER TABLE user_avatars ADD COLUMN storage_sha TEXT;

ALTER TABLE user_skin_profiles ADD COLUMN storage_provider TEXT;
ALTER TABLE user_skin_profiles ADD COLUMN storage_path TEXT;
ALTER TABLE user_skin_profiles ADD COLUMN storage_sha TEXT;

CREATE INDEX IF NOT EXISTS idx_user_avatars_storage_provider
  ON user_avatars(storage_provider);

CREATE INDEX IF NOT EXISTS idx_user_skin_profiles_storage_provider
  ON user_skin_profiles(storage_provider);
