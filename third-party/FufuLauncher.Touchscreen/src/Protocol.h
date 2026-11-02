// CloudGI adaptation, 2026-11-01. SPDX-License-Identifier: AGPL-3.0-only
#pragma once
#include <windows.h>
#include <string>

namespace CloudGiTouch {
constexpr LONG ProtocolVersion = 2;
enum State : LONG { Loading = 0, HookReady = 1, Applied = 2, Failed = 3 };
enum Error : LONG { None = 0, UnsupportedGame = 1, HookFailed = 2, SwitchFailed = 3 };
struct Status { LONG version; volatile LONG state; volatile LONG error; volatile LONG stage; volatile LONG detail; };
inline std::wstring MappingName(DWORD pid) {
    return L"Local\\CloudGI.MobileUI." + std::to_wstring(pid);
}
inline void Publish(Status* status, State state, Error error = None) {
    InterlockedExchange(&status->error, error);
    InterlockedExchange(&status->state, state);
}
}
