## Setting up the NUT server

On the machine that runs `upsd`:

- In `upsd.conf`, listen on the LAN: `LISTEN 0.0.0.0 3493`, or the server's IP.
- In `upsd.users`, add a user for UPS Hub:
  ```
  [upshub]
    password = change-me
    upsmon secondary
  ```
- Restart upsd. Then add the server in UPS Hub, under Servers, and click Test connection.

If the firewall blocks TCP 3493, or upsd only listens on localhost, discovery won't find it. The Servers page has a troubleshooting panel for that.

## How discovery works

Find servers checks these places, in order:
- this PC;
- saved hosts and hosts found before;
- mDNS (`_nut._tcp`);
- the ARP table;
- every active IPv4 subnet.

Subnets up to /22 are swept in full. On bigger networks it only tries the /24 around this PC plus the ARP hits. VPN, WSL/Hyper-V NAT, Docker, VMware and VirtualBox adapters are skipped unless you turn them on in Options. Options is also where you add extra ports or hosts (for example `10.0.0.5-40, nas.lan`).

Every open port 3493 is confirmed with `VER` and `LIST UPS` before it's shown. A server that answers on several addresses is merged into one entry, but only when the UPS serials, the host name or the mDNS instance match.

## Shutdown rules

UPS Hub can act on these triggers:
- battery percent;
- runtime left;
- the UPS low-battery flag;
- time on battery;
- a forced shutdown (FSD) from the NUT primary;
- optionally, a lost connection during an outage.

A countdown window comes first: you can cancel it, or extend it once. Power coming back cancels it too. The action can be shut down, hibernate or sleep.

Shutdown rules → Test shutdown… really does the configured action at the end of the countdown. It asks for confirmation first, and Cancel stops it. With `--demo`, power actions are only logged.

## Direct USB (experimental)

USB support is a C# port of NUT's `usbhid-ups` driver: the HID parser, the value scaling, and the apc, cps and mge subdrivers. It reads the UPS through `hid.dll`. If that doesn't work, it falls back to the Windows battery driver. There's also optional Megatec Q1 over a COM port. It only reads: no beeper, test or shutdown commands are ever sent to the UPS.

Things that get in the way:
- **Other UPS software.** Close PowerChute, PowerPanel or any NUT driver on the same PC first.
- **Zadig.** If you switched the UPS to WinUSB/libusb with Zadig, Windows no longer sees it as a HID device. Put it back with Device Manager → Update driver → Let me pick → USB Input Device.
- **Linux preview.** The build reads `/dev/hidraw*`. Your user needs access, for example: `SUBSYSTEM=="hidraw", ATTRS{idVendor}=="051d", MODE="0660", GROUP="plugdev"`.

## Updates and release signing

The app checks GitHub Releases at most once a day. You can also check from Settings → About, or turn the checks off there.

An update is installed only if two checks pass:
- `SHA256SUMS.txt` has a valid minisign signature from the key built into the app (`minisign.pub`, `RWTkuntIbxaEgIdipWngZd/lNqKd4Bvp2H+uqxGQy6/fA2sgF+XcJxZ7`);
- the installer's SHA-256 matches that file.

The installer then runs silently, and the app restarts.

To check a release yourself:
```
minisign -Vm SHA256SUMS.txt -P RWTkuntIbxaEgIdipWngZd/lNqKd4Bvp2H+uqxGQy6/fA2sgF+XcJxZ7
sha256sum -c SHA256SUMS.txt
```

`tools/UpsHub.ReleaseTool` makes the sums and signs them (`keygen`, `sums`, `sign`, `verify`).

`ci/release.yml` is a GitHub Actions workflow for tagged releases. It isn't enabled yet: it needs to be moved to `.github/workflows/`, and it needs the `UPSHUB_SIGNING_KEY` secret.

Code signing (Authenticode) is covered in [CODE-SIGNING.md](CODE-SIGNING.md).

## Installer

Inno Setup 6.4+, script in `installer/UpsHub.iss`:
```
ISCC installer\UpsHub.iss /DAppVersion=0.4.0
```

What it does:
- Installs per-user to `%LOCALAPPDATA%\Programs\UPS Hub` and adds a Start menu shortcut.
- Optionally starts UPS Hub at sign-in.
- Closes a running copy before upgrading.
- On uninstall, asks whether to delete settings. A silent uninstall keeps them unless you pass `/REMOVESETTINGS`.

Settings live in `%APPDATA%\UPS Hub\settings.json`. Passwords in it are encrypted with DPAPI.

## Command line

| Option | What it does |
|---|---|
| `--minimized` | start in the tray |
| `--demo` | simulated NUT server, USB UPS and network; temporary settings; no real power actions |
| `--wizard` | demo with the setup wizard open |
| `--screenshots DIR`, `--screenshots-discovery DIR`, `--screenshots-local DIR`, `--screenshots-phase4 DIR` | scripted demo runs that save screenshots |

## Layout

- `src/UpsHub.Core`: the core library, with no UI:
  - NUT client;
  - monitor;
  - shutdown logic;
  - settings;
  - discovery;
  - USB HID port.
- `src/UpsHub.App`: the Avalonia app. Windows-specific code is in `Platform/Windows`.
- `tools/UpsHub.FakeNut`: a fake upsd for tests and demo mode.
- `tools/UpsHub.ReleaseTool`: checksums and signing.
- `tools/icon`: icon and installer image generators.
- `installer`: the Inno Setup script.
- `tests/UpsHub.Tests`: xUnit tests.


