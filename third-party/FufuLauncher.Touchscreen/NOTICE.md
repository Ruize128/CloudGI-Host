# CloudGI touch-only adaptation

Upstream: https://github.com/FufuLauncher/FufuLauncher.UnlockerIsland
Pinned revision: bdb424a44bc13379cc5cdca388b477ac1ed1e158
Copyright (c) FufuLauncher Dev Team. License: AGPL-3.0-only (see LICENSE).
CloudGI adaptation: 2026-11-01 to 2026-11-02.

## Scope and behavior

This is a touch-only source adaptation, not an unmodified upstream binary.
It retains the scanner/signatures, ChangeFOV callback timing, internal touchscreen
switch call, and suspended launch / LoadLibrary approach. Other plugin features,
remote UID/authorization/announcement checks and fixed-address fallbacks are removed.
It does not change game files, bypass protections, unlock frame rate or alter FOV.

The x64 helper creates and owns one new game, loads only its adjacent touch DLL,
waits for hook readiness, resumes the game and returns its real PID to Host.
The internal UI switch runs on the later game callback; touchscreenPrepared
means preparation succeeded, not that the HUD has already changed.
An existing game is reused unchanged by Host. Setup failures clean up only the
newly owned game. Hook readiness has a 45-second bound.

First-match executable-section scanning preserves upstream selection order.
The current CN 7.1 file has two touch signature hits; its first matches the
pinned upstream evidence. No fixed RVA or uniqueness gate is used. Missing
signatures fail explicitly; future-version compatibility is not guaranteed.

## Dependencies and runtime

MinHook 1.3.4: https://github.com/TsudaKageyu/minhook/tree/v1.3.4
Its source and license are retained in minhook/. Static runtime notices from
LLVM-MinGW are retained in licenses/ (LLVM/libc++ and MinGW components).

Both ARM64 and x64 Host use runtime/win-x64/CloudGI.MobileUI.exe and
CloudGI.Touchscreen.dll. Windows ARM64 uses built-in x64 emulation; the DLL is
loaded into the x64 game, not the ARM64 Host. No separate VC runtime install is needed.
SHA256SUMS records the bundled binaries. Normal dotnet build/publish carries
these binaries, licenses and native corresponding source/build inputs.

## Rebuild and regression fixtures

Validated toolchain: LLVM-MinGW 20261020 (clang 22), static runtime:
https://github.com/mstorsjo/llvm-mingw/releases/tag/20261020

    python3 build-native.py --toolchain /path/to/llvm-mingw/bin

Add --tests to build the retained owned-process fixtures under obj/fixtures/:
HookFixture.exe (production hook and duplicate-signature selection),
HookFixtureNoCallback.exe (ready without a game callback), and
UnsupportedFixture.exe (missing signatures and failure cleanup).
No fixture executable or test DLL is shipped in runtime/. Generated obj/
content is not versioned. Regenerate binaries and SHA256SUMS after source edits.

An alternative production build uses a Windows Visual Studio C++ command prompt
with CMake, selecting x64 even for ARM64 Host:

    cmake -S . -B obj/msvc -A x64
    cmake --build obj/msvc --config Release

Copy the two production outputs into runtime/win-x64/ and update SHA256SUMS.
The assembly hook fixtures require LLVM-MinGW; the alternative MSVC build has
not been part of the recorded validation.

## Diagnostics and acceptance

The bounded last-attempt record is
%LOCALAPPDATA%\CloudGI\Host\mobile-ui-status.json (stage, state, code, PID, time only).
Stage 7/state 1 means prepared. The DLL retains per-process shared status;
stage 9/state 2 means the internal switch returned.

On 2026-11-02 the user confirmed actual mobile HUD and normal touch gameplay
through CloudGI on the tested Android/Parallels setup. Other game builds and
hosts require independent acceptance; component readiness alone is not HUD proof.

## Distribution

Retain upstream copyrights and AGPL-3.0 notices. Corresponding native source and
build inputs accompany output in MobileUI/Source/. Comply with applicable AGPL
source obligations for modified/combined covered works and remote use.
These notices do not relicense unrelated components or grant rights in the game.
