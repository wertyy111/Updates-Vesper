import fs from "node:fs/promises";
import path from "node:path";

function assert(condition, message) {
  if (!condition) {
    throw new Error(message);
  }
}

async function main() {
  const syncConfigPath = path.resolve("..", "..", "MinecraftCheatLauncher", "account-sync.json");
  const syncConfigText = (await fs.readFile(syncConfigPath, "utf8")).replace(/^\uFEFF/, "");
  const syncConfig = JSON.parse(syncConfigText);
  const registerUrl = String(syncConfig?.RegisterUrl || "").trim();

  assert(registerUrl.startsWith("https://"), "launcher account-sync.json must point to https API");

  const baseUrl = registerUrl.replace(/\/api\/v1\/auth\/register$/i, "");
  assert(baseUrl.length > 0, "failed to derive Cloudflare API base url");

  const response = await fetch(`${baseUrl}/health`);
  const body = await response.json();

  assert(response.ok, "health status must be ok");
  assert(body.runtime === "cloudflare", "runtime must be cloudflare");
  assert(body.storageMode === "d1", "storage mode must be d1");

  console.log("Cloudflare API checks passed.");
}

main().catch((error) => {
  console.error("Cloudflare API checks failed:", error);
  process.exitCode = 1;
});
