# Security Policy

## Supported versions

Only the latest released version receives security fixes.

## Reporting a vulnerability

Please do **not** open a public issue. Use GitHub's private reporting:
<https://github.com/Christian9005/CodeBridge/security/advisories/new>

Include the affected version, reproduction steps and impact. You will get an answer within 7 days.

## What CodeBridge does on your machine

- Downloads `esptool` from GitHub Releases over HTTPS the first time you flash an ESP32, and verifies a pinned SHA-256 before using it.
  Files are stored under `%LOCALAPPDATA%\CodeBridge\tools`.
- Runs `esptool`, `arduino-cli` and `dotnet` as child processes (flash and package install commands only).
- Does not collect telemetry and does not send data anywhere.

## Network access to the board (firmware 0.9+)

- USB is trusted (physical access). The TCP/Wi-Fi port is **closed by default**: every client must send `AUTH:<token>` first, and the
  token can only be set over USB (`WTOK`). Without a token the board answers every network command with `ERR:NO_TOKEN`.
- The ESP-NOW mesh authenticates every frame with an HMAC-SHA256 tag made with a shared 128-bit key (written over USB only), requires a single-use challenge in every command
  (no replay) and never relays Wi-Fi, token, OTA or MQTT commands. Frames are authenticated, not encrypted. Details in [docs/mesh.md](docs/mesh.md).
- Unauthenticated connections are dropped after 5 seconds, three wrong tokens drop the connection, and the token is compared in constant time.
- Tokens are random 128-bit values. CodeBridge stores them encrypted with the Windows user's DPAPI key (`%LOCALAPPDATA%\CodeBridgeoard-tokens.json`)
  and passes them to the helper process through its environment, never on a command line. The Wi-Fi password is sent over USB only, through standard input.
- Traffic on the local network is **not encrypted** (plain TCP). Use a network you trust, or a VPN, for anything sensitive.
- Over-the-air updates (`OTAB`) require an authenticated session and only accept `http(s)` URLs; they time out when the server stalls.
- Every command line is limited to 512 characters on the board and 4096 on the PC; longer lines are discarded, never executed truncated.
