# Photino dead zones note

Date: 2026-06-25

## Symptom

On Windows, parts of the Photino launcher behaved like dead zones:

- clicking the bottom buttons (`Skin`, `Background`, `Mods`, `Friends`) made the launcher look like it moved behind another window;
- some titlebar/account buttons had the same behavior;
- button commands did not reliably reach the backend.

This happened after the window rounding / cross-platform shell work.

## Working fix state

Keep these parts together:

- `Program.cs`: `PhotinoWindow.Transparent = false`.
- `bridge.ts`: commands try the local HTTP bridge first (`POST /bridge-message`) and fall back to `window.external.sendMessage`.
- `LocalStaticFileServer.cs`: serves `/bridge-message`.
- `Program.cs`: handles HTTP bridge messages and returns a fresh snapshot envelope.
- `App.tsx`: important shell buttons use `pointerup` with `stopPropagation` / `preventDefault`.

Do not reintroduce transparent Photino windows casually. The CSS can still provide the rounded visual shell through `#root` / launcher frame styling.

## What did not fix it

Do not repeat these as the primary fix:

- changing button colors / opacity only;
- making all button child elements `pointer-events: none`;
- disabling `SupportsNativeWindowShaping` for Windows entirely.

The last one made the build compile, but it was not the known-good state. The confirmed working state kept Windows native shaping enabled while `Transparent` stayed disabled and commands used the HTTP bridge.

## Why it likely happened

The bug was probably a combination of:

- Photino transparent/chromeless window hit testing;
- native rounding / region shaping;
- glass/liquid overlay layers;
- fragile `window.external.sendMessage` click path.

When `Transparent = true`, some areas around the visually rounded/glass UI could behave like holes in the window or lose focus during click handling. The HTTP bridge makes command delivery independent from the fragile Photino web message path, and `Transparent = false` removes the window-level hit-test holes.

## Before touching this again

After any change to window chrome, transparency, shaping, drag, liquid glass, or bridge messaging, manually test on Windows:

1. Bottom buttons: `Skin`, `Background`, `Mods`, `Friends`.
2. Top account button.
3. Titlebar minimize and close.
4. Version dropdown.
5. Window drag from titlebar.

If any click sends the launcher behind another window, first check `Transparent`, `/bridge-message`, and the `pointerup` handlers before changing visual CSS.
