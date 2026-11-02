// Owned integration fixture, not game code. SPDX-License-Identifier: AGPL-3.0-only
#include <windows.h>
#include <cstdint>
#ifndef CLOUDGI_NO_SIGNATURES
extern "C" int32_t FixtureFov(void*, float);
extern "C" volatile LONG FixtureTouchMarker;
#endif
int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR command, int) {
#ifdef CLOUDGI_NO_SIGNATURES
    (void)command; // Unsupported-build fixture; the helper must cancel it before main runs.
#else
    // The production payload must hook FOV and choose the first of two touch signatures.
#ifdef CLOUDGI_NO_CALLBACK
    // Keep both functions in the image but simulate a login stage that does not call FOV.
    if (wcsstr(command, L"fixture-callback")) FixtureFov(nullptr, 1.0f);
#else
    (void)command;
    FixtureFov(nullptr, 1.0f);
    if (FixtureTouchMarker != 1) return 99;
#endif
#endif
    Sleep(300000);
    return 0;
}
