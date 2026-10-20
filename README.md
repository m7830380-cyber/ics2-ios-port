# Internet Cafe Simulator 2 iOS port (TrollStore)

Runs the retail Steam build of Internet Cafe Simulator 2 (Unity 2020.2.2f1, Mono) natively on iPhone. It does
not rebuild the game: the game's own assemblies are patched and AOT-compiled into an IL2CPP player built with
the game's exact Unity version, and the game's own data files are converted for Metal/ASTC and dropped in.

**This repo contains no game files.** Everything derived from the game (patched DLLs, converted data, IPAs)
lives in a private repo. You need your own copy of the game.

* `tools/patcher` – Mono.Cecil patcher: Steam stubbed (offline achievements), no network calls (weather / IP
  lookup, I2 Google sheet), every keyboard/mouse read goes through `ICS2Shim.VInput` (driven by touch).
* `shim/` – `ICS2Shim.dll`: virtual input + platform stand-ins the patched game calls.
* `mod/` – `ICS2iOS.dll`: touch controls, log file + on-screen log viewer, safe area, offline weather.
* `shell/` – minimal Unity 2020.2.2f1 project; CI builds the unsigned shell IPA from it (fakesigned with ldid).
* `tools/build_plugins.py` – builds shim + mod and runs the patcher.
* `tools/convert_data.py` – converts the retail data (DX11 shaders → Metal, BCn → ASTC with downscaling).
* `tools/package_ipa.py` – merges the shell IPA with the converted data.

Log: `Documents/InternetCafeSim2-iOS.log` (previous run: `-prev.log`). Tap the FPS counter to see it on screen.

## Windows co-op build

`windows/` holds an installer that makes a co-op copy of the player's own Steam install next to it (hard links,
the Steam install is only read). `python tools/make_windows_pack.py --build build-N` zips the installer with the
patched assemblies; the zip contains game code and goes to the private repo's releases only. Co-op works on the
same LAN and over Radmin VPN (discovery broadcasts on every network, the panel lists every address).
