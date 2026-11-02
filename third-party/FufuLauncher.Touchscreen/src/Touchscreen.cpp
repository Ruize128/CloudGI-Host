/*
 * Copyright (c) FufuLauncher Dev Team.
 * CloudGI touch-only adaptation, 2026-11-01. SPDX-License-Identifier: AGPL-3.0-only
 * Derived from Core/Hooks.cpp and Patterns/Patterns.h at upstream bdb424a44bc13379cc5cdca388b477ac1ed1e158.
 * Keeps the ChangeFOV callback timing and SwitchInputDeviceToTouchScreen call only.
 */
#include "Protocol.h"
#include "Scanner/Scanner.h"
#include <MinHook.h>

namespace {
using ChangeFov = int32_t (WINAPI*)(void*, float);
using SwitchInput = void (WINAPI*)(void*);
ChangeFov originalFov = nullptr;
SwitchInput switchInput = nullptr;
CloudGiTouch::Status* status = nullptr;
volatile LONG attempted = 0;

// These signatures are retained verbatim from the pinned upstream Patterns.h.
constexpr auto FovPattern = "40 53 48 83 EC 60 0F 29 74 24 ? 48 8B D9 0F 28 F1 E8 ? ? ? ? 48 85 C0 0F 84 ? ? ? ? E8 ? ? ? ? 48 8B C8";
constexpr auto TouchPattern = "56 57 48 83 EC ? 48 89 CE 80 3D ? ? ? ? 00 48 8B 05 ? ? ? ? 0F 85 ? ? ? ? 48 8B 88 ? ? ? ? 48 85 C9 0F 84 ? ? ? ? 48 8B 15 ? ? ? ? E8 ? ? ? ? 48 89 C7 48 8B 05 ? ? ? ? 48 8B 88 ? ? ? ? 48 85 C9 0F 84 ? ? ? ? 31 D2";

int32_t WINAPI OnChangeFov(void* instance, float value) {
    // As in upstream, switch on the game callback thread, once initialization reaches ChangeFOV.
    // Do not alter FOV, frame rate, input events, game files, or protection software.
    if (InterlockedCompareExchange(&attempted, 1, 0) == 0) {
        __try {
            InterlockedExchange(&status->stage, 8);
            switchInput(nullptr);
            InterlockedExchange(&status->stage, 9);
            CloudGiTouch::Publish(status, CloudGiTouch::Applied);
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            CloudGiTouch::Publish(status, CloudGiTouch::Failed, CloudGiTouch::SwitchFailed);
        }
    }
    return originalFov(instance, value);
}

DWORD WINAPI Initialize(void*) {
    const auto name = CloudGiTouch::MappingName(GetCurrentProcessId());
    HANDLE mapping = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, name.c_str());
    if (!mapping) return 1; // Only run when launched by our owning helper.
    status = static_cast<CloudGiTouch::Status*>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(*status)));
    if (!status || status->version != CloudGiTouch::ProtocolVersion) {
        if (status) UnmapViewOfFile(status);
        status = nullptr;
        CloseHandle(mapping);
        return 1;
    }
    // Keep this mapping open for the callback lifetime (the game process releases it at exit).
    InterlockedExchange(&status->stage, 2);
    void* fov = Scanner::ScanFirstMainMod(FovPattern);
    InterlockedExchange(&status->stage, 3);
    switchInput = reinterpret_cast<SwitchInput>(Scanner::ScanFirstMainMod(TouchPattern));
    if (!fov || !switchInput) {
        CloudGiTouch::Publish(status, CloudGiTouch::Failed, CloudGiTouch::UnsupportedGame);
        return 1; // No guessed offsets or arbitrary-address fallback on unknown versions.
    }
    InterlockedExchange(&status->stage, 4);
    auto result = MH_Initialize();
    if (result == MH_OK) {
        InterlockedExchange(&status->stage, 5);
        result = MH_CreateHook(fov, reinterpret_cast<void*>(OnChangeFov), reinterpret_cast<void**>(&originalFov));
    }
    if (result == MH_OK) {
        InterlockedExchange(&status->stage, 6);
        result = MH_EnableHook(fov);
    }
    if (result != MH_OK) {
        InterlockedExchange(&status->detail, static_cast<LONG>(result));
        CloudGiTouch::Publish(status, CloudGiTouch::Failed, CloudGiTouch::HookFailed);
        return 1;
    }
    InterlockedExchange(&status->stage, 7);
    CloudGiTouch::Publish(status, CloudGiTouch::HookReady);
    return 0;
}
}

BOOL WINAPI DllMain(HMODULE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(module);
        HANDLE thread = CreateThread(nullptr, 0, Initialize, nullptr, 0, nullptr);
        if (thread) CloseHandle(thread);
    }
    return TRUE;
}
