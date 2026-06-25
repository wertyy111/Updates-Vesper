# Vesper Account API

Cloudflare Worker backend for launcher accounts.

Account storage now works only through Cloudflare D1.
Avatar files and skin manifests can additionally be mirrored to a private GitHub repository.

## Routes

- `POST /api/v1/auth/register`
- `POST /api/v1/auth/login`
- `GET /api/v1/auth/credential-info`
- `GET /api/v1/auth/me`
- `POST /api/v1/auth/logout`
- `POST /api/v1/profile/avatar`
- `GET /api/v1/profile/avatar/by-username`
- `POST /api/v1/presence/ping`
- `GET /api/v1/friends`
- `POST /api/v1/friends/request`
- `POST /api/v1/friends/respond`
- `POST /api/v1/friends/remove`
- `GET /health`

Passwords are stored as `PBKDF2-SHA256` hash + salt.
Session tokens are random, and only token hashes are stored.
Friends, sessions and presence stay in D1.
Avatars and skin manifests can be stored in GitHub-backed asset storage.

## Setup

1. Install Wrangler:

```bash
npm install
```

2. Make sure `wrangler.toml` contains the correct `account_id`, `database_name` and `database_id`.

3. Apply the D1 schema:

```bash
npm run d1:init
```

4. Deploy the Worker:

```bash
npm run deploy
```

5. Optional: enable private GitHub asset storage for avatars and skin manifests:

```bash
wrangler secret put GITHUB_STORAGE_TOKEN
```

Set non-secret vars in `wrangler.toml` or local environment:

```text
PUBLIC_API_BASE_URL=https://vesper-account-api-3516.vesperlauncher3516.workers.dev
GITHUB_STORAGE_OWNER=<your-github-owner>
GITHUB_STORAGE_REPO=vesper-private-nicknames
GITHUB_STORAGE_BRANCH=main
GITHUB_STORAGE_COMMITTER_NAME=Vesper Launcher
GITHUB_STORAGE_COMMITTER_EMAIL=vesper-launcher@users.noreply.github.com
```

For a private GitHub repository the token must be valid for the target repo and must be able to read/write repository contents. A classic token needs the `repo` scope; a fine-grained token needs Contents: Read and write for `GITHUB_STORAGE_OWNER/GITHUB_STORAGE_REPO`.

For local dev, `.env` can use the same `GITHUB_STORAGE_*` names. Legacy local names are still accepted for compatibility: `GITHUB_TOKEN`, `GITHUB_REPO_OWNER`, `GITHUB_REPO_NAME`, and `GITHUB_REPO_BRANCH`.

Check the local GitHub storage credentials without printing the token:

```bash
npm run check:github-storage
```

Or bootstrap everything in one command from PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\enable-github-asset-storage.ps1 `
  -GitHubOwner "<your-github-owner>" `
  -GitHubToken "<fine-grained-token-with-contents-write>" `
  -GitHubRepo "vesper-private-nicknames"
```

6. Apply the GitHub asset storage migration when updating an existing D1 database:

```bash
wrangler d1 execute vesper-account-db --remote --file migrations/0004_github_asset_storage.sql --config wrangler.toml
```

Current production URL:

```text
https://vesper-account-api-3516.vesperlauncher3516.workers.dev
```

## Development

Run remote dev against Cloudflare:

```bash
npm run dev
```

Check production health:

```bash
npm test
```

## Launcher Config

`account-sync.json` should point to the Cloudflare Worker:

```json
{
  "RegisterUrl": "https://vesper-account-api-3516.vesperlauncher3516.workers.dev/api/v1/auth/register",
  "LoginUrl": "https://vesper-account-api-3516.vesperlauncher3516.workers.dev/api/v1/auth/login",
  "CredentialInfoUrl": "https://vesper-account-api-3516.vesperlauncher3516.workers.dev/api/v1/auth/credential-info",
  "MeUrl": "https://vesper-account-api-3516.vesperlauncher3516.workers.dev/api/v1/auth/me",
  "LogoutUrl": "https://vesper-account-api-3516.vesperlauncher3516.workers.dev/api/v1/auth/logout",
  "AuthorizationHeaderName": "",
  "AuthorizationHeaderValue": ""
}
```

You can regenerate it with:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\prepare-account-sync.ps1 `
  -ApiBaseUrl "https://vesper-account-api-3516.vesperlauncher3516.workers.dev"
```

## Notes

- Auth, passwords, sessions, friends and presence remain in Cloudflare D1.
- GitHub storage is intended only for non-sensitive user assets such as avatar images and skin manifests.
- Do not ship a GitHub token inside the launcher client.
- Local account backend mode is no longer supported.
- The launcher is configured to use the Cloudflare Worker URL.
