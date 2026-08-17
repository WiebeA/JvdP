# Security

The repository contains source code, not deployment credentials.

## Repository secrets

Configure these GitHub Actions secrets before creating a release:

- JVDP_AP_PASSWORD: operator password for the ESP Wi-Fi access point.
- JVDP_OTA_PASSWORD: separate administrative password for ESP OTA updates.

The two values must be different. The release workflow refuses to publish when
either value is missing or when they are equal.

## Booth update access

Each booth needs a fine-grained GitHub token with read-only Contents access to
WiebeA/JvdP. The updater encrypts this token with Windows DPAPI for the current
Windows user. The plaintext token is never written to disk or committed.

Prefer one revocable token per booth. Do not send tokens through chat, email or
command-line arguments.

## Release integrity

The updater downloads release assets through authenticated HTTPS and verifies
the installer against SHA256SUMS.txt before executing it.

Windows code signing is not configured yet. Add an Authenticode certificate
before distributing the installer outside the managed JvdP booth fleet.
