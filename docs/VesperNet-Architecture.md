# VesperNet

## Goal

`VesperNet` is the launcher's own private overlay network for multiplayer sessions.
It is meant to behave closer to `Radmin VPN` than to the current direct-connect flow.

The important difference:

- Current launcher flow:
  - host opens LAN world
  - launcher tries to discover `joinHost/joinPort`
  - friend launches Minecraft with `--server --port`
  - transport is still ordinary direct TCP/IP
- VesperNet goal:
  - launcher users join the same virtual private network
  - each user gets a stable virtual address
  - friend connection works through the overlay, not through the host's home router alone

## Why This Is A Separate System

The existing Cloudflare API is only a coordinator for:

- account auth
- friend list
- presence
- published join metadata

It is not a packet transport layer.

A real launcher-owned overlay needs three extra parts:

1. A local background service on Windows
2. A virtual network adapter or tunnel driver
3. A peer/relay transport layer outside Cloudflare Worker request handlers

## Recommended Stack

### Windows side

- `VesperNet.Service`:
  - background Windows service
  - runs with elevated install rights
  - manages tunnel lifecycle independently from WPF launcher UI
- `Wintun`:
  - virtual Layer-3 adapter
  - preferred over writing a custom driver
  - still requires installer integration and admin rights
- `Launcher UI`:
  - login/session
  - friend controls
  - diagnostics
  - sends commands to `VesperNet.Service`

### Server side

- `Cloudflare Worker` stays as signaling/auth coordinator only:
  - issue short-lived overlay session tokens
  - friend authorization
  - peer discovery metadata
  - relay session setup metadata
- Separate relay host:
  - required for NAT fallback
  - Cloudflare Worker is not the right place for a full overlay packet relay

## Connection Model

### Peer states

Each player may be in one of three transport modes:

1. `p2p-direct`
2. `p2p-hole-punched`
3. `relay`

### Join flow

1. User logs into launcher.
2. Launcher asks Cloudflare for a short-lived VesperNet session.
3. Local `VesperNet.Service` brings up the virtual adapter.
4. Service receives the player's virtual IP.
5. Friend discovery returns the friend's virtual IP instead of raw home-router address.
6. Minecraft connects to the friend's VesperNet IP.
7. If direct tunnel cannot be formed, traffic falls back to relay.

## Addressing

- Reserve a private overlay range, for example `100.96.0.0/11` or `10.x.x.x`.
- Every authenticated user gets:
  - stable virtual IPv4
  - optional stable virtual DNS name such as `<username>.vesper`

The launcher should display:

- `VesperNet: connected`
- assigned virtual IP
- transport mode: `direct` or `relay`

## Security

- Overlay session token must be short-lived and bound to launcher account session.
- Only accepted friends should be allowed to discover direct join targets by default.
- Relay traffic must be encrypted end-to-end between peers.
- Driver/service commands must be local-only and authenticated.

## What Must Change In This Repo

### New components

- new Windows service project:
  - `windows/VesperNet.Service`
- local control API between launcher and service:
  - named pipe or localhost-only HTTP
- installer changes:
  - install/remove service
  - install/remove `Wintun`
  - admin elevation flow

### Existing launcher changes

- replace current `joinHost/joinPort` preference with virtual address preference
- friend card should show:
  - `VesperNet connected`
  - `Direct`
  - `Relay`
  - or a diagnostic error
- direct connect button should target overlay address when available

### Existing backend changes

- keep Cloudflare Worker for signaling
- add endpoints for:
  - overlay token issue
  - peer candidate publish
  - peer candidate lookup
  - relay reservation

## Hard Truth

This is not a small patch.

It is closer to building a compact gaming VPN product than to fixing a UI bug.
The minimum shippable path is:

1. service + launcher control channel
2. virtual adapter bring-up
3. signaling API
4. relay fallback
5. diagnostics and recovery

## Recommended Delivery Order

### Phase 1

- scaffold `VesperNet.Service`
- launcher can detect whether service is installed and online
- add diagnostic panel in launcher

### Phase 2

- integrate virtual adapter
- issue a virtual IP after login
- show VesperNet status in UI

### Phase 3

- add signaling API in Cloudflare
- peer discovery over overlay identities

### Phase 4

- add relay node
- fall back automatically when direct path fails

### Phase 5

- switch friend connect flow from raw public/LAN address to VesperNet address

## Practical Recommendation

If the project truly needs "works like Radmin VPN", build `VesperNet` as a separate subsystem.
Do not keep layering more heuristics on top of `Open to LAN + direct connect`.
