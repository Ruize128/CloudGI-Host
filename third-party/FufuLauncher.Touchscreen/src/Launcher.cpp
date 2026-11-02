/*
 * Copyright (c) FufuLauncher Dev Team.
 * CloudGI adaptation, 2026-11-01. SPDX-License-Identifier: AGPL-3.0-only
 * Suspended launch / LoadLibraryW loading adapted from upstream Launcher/Launcher.cpp.
 * Loads exactly one adjacent touch-only DLL into a newly owned game, never an existing process.
 */
#include "Protocol.h"
#include <tlhelp32.h>
#include <filesystem>
#include <fstream>
#include <iostream>

namespace fs = std::filesystem;
namespace {
const CloudGiTouch::Status* diagnosticStatus = nullptr;
DWORD diagnosticPid = 0;
const ULONGLONG diagnosticStarted = GetTickCount64();

// Bounded last-attempt diagnostics in the existing Host data directory; no paths or account data.
void Record(int code) noexcept {
    try {
        wchar_t local[32768]{};
        const DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", local, 32768);
        if (!length || length >= 32768) return;
        auto directory = fs::path(local) / L"CloudGI" / L"Host";
        if (!fs::is_directory(directory)) return;
        std::ofstream file(directory / L"mobile-ui-status.json", std::ios::trunc);
        if (!file) return;
        file << "{\"processId\":" << diagnosticPid << ",\"code\":" << code
             << ",\"stage\":" << (diagnosticStatus ? diagnosticStatus->stage : 0)
             << ",\"state\":" << (diagnosticStatus ? diagnosticStatus->state : 0)
             << ",\"detail\":" << (diagnosticStatus ? diagnosticStatus->detail : 0)
             << ",\"elapsedMs\":" << (GetTickCount64() - diagnosticStarted) << "}\n";
    } catch (...) { } // Diagnostics must never change launch/cleanup semantics.
}

struct Handle {
    HANDLE value = nullptr;
    explicit Handle(HANDLE h = nullptr) : value(h) {}
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
};
struct Child {
    PROCESS_INFORMATION pi{};
    bool released = false;
    ~Child() {
        if (pi.hProcess && !released) {
            TerminateProcess(pi.hProcess, 1);
            WaitForSingleObject(pi.hProcess, 5000);
        }
        if (pi.hThread) CloseHandle(pi.hThread);
        if (pi.hProcess) CloseHandle(pi.hProcess);
    }
};
struct View {
    CloudGiTouch::Status* value;
    ~View() { diagnosticStatus = nullptr; if (value) UnmapViewOfFile(value); }
};

bool IsX64(const fs::path& path) {
    std::ifstream file(path, std::ios::binary);
    IMAGE_DOS_HEADER dos{};
    file.read(reinterpret_cast<char*>(&dos), sizeof(dos));
    if (!file || dos.e_magic != IMAGE_DOS_SIGNATURE || dos.e_lfanew < 0 || dos.e_lfanew > 16 * 1024 * 1024) return false;
    file.seekg(dos.e_lfanew);
    DWORD signature{};
    IMAGE_FILE_HEADER header{};
    file.read(reinterpret_cast<char*>(&signature), sizeof(signature));
    file.read(reinterpret_cast<char*>(&header), sizeof(header));
    return file && signature == IMAGE_NT_SIGNATURE && header.Machine == IMAGE_FILE_MACHINE_AMD64 &&
        !(header.Characteristics & IMAGE_FILE_DLL);
}

uintptr_t FindModule(DWORD pid, const fs::path& path, bool exactPath) {
    Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid));
    if (snapshot.value == INVALID_HANDLE_VALUE) return 0;
    MODULEENTRY32W module{sizeof(module)};
    if (Module32FirstW(snapshot.value, &module)) do {
        const auto actual = exactPath ? fs::path(module.szExePath) : fs::path(module.szModule);
        if (_wcsicmp(actual.c_str(), path.c_str()) == 0)
            return reinterpret_cast<uintptr_t>(module.modBaseAddr);
    } while (Module32NextW(snapshot.value, &module));
    return 0;
}

bool LoadTouchDll(Child& child, const fs::path& dll) {
    const auto load = GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW");
    HMODULE owner = nullptr;
    if (!load || !GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(load), &owner)) return false;
    wchar_t ownerPath[32768]{};
    if (!GetModuleFileNameW(owner, ownerPath, 32768)) return false;
    const auto remoteOwner = FindModule(child.pi.dwProcessId, fs::path(ownerPath).filename(), false);
    // Before the suspended primary thread enters the loader, Toolhelp can have no module list yet.
    // The x64 system DLL export address is shared for same-architecture processes on this boot,
    // as used by upstream. The new thread runs loader initialization before invoking the export.
    const auto remoteLoad = remoteOwner
        ? remoteOwner + reinterpret_cast<uintptr_t>(load) - reinterpret_cast<uintptr_t>(owner)
        : reinterpret_cast<uintptr_t>(load);
    const auto text = dll.wstring();
    const SIZE_T size = (text.size() + 1) * sizeof(wchar_t);
    void* memory = VirtualAllocEx(child.pi.hProcess, nullptr, size, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!memory) return false;
    SIZE_T written = 0;
    if (!WriteProcessMemory(child.pi.hProcess, memory, text.c_str(), size, &written) || written != size) {
        VirtualFreeEx(child.pi.hProcess, memory, 0, MEM_RELEASE);
        return false;
    }
    Handle thread(CreateRemoteThread(child.pi.hProcess, nullptr, 0,
        reinterpret_cast<LPTHREAD_START_ROUTINE>(remoteLoad), memory, 0, nullptr));
    if (!thread.value) {
        VirtualFreeEx(child.pi.hProcess, memory, 0, MEM_RELEASE);
        return false;
    }
    // On timeout Child terminates the owned game before its remaining allocation is reclaimed.
    if (WaitForSingleObject(thread.value, 15000) != WAIT_OBJECT_0) return false;
    VirtualFreeEx(child.pi.hProcess, memory, 0, MEM_RELEASE);
    // A thread exit code truncates HMODULE on x64; verify the real loaded module path instead.
    return FindModule(child.pi.dwProcessId, dll, true) != 0;
}

bool WaitState(Child& child, CloudGiTouch::Status* status, CloudGiTouch::State wanted, DWORD timeout) {
    const auto started = GetTickCount64();
    LONG previousStage = -1;
    while (GetTickCount64() - started < timeout) {
        if (WaitForSingleObject(child.pi.hProcess, 50) == WAIT_OBJECT_0) return false;
        LONG state = InterlockedCompareExchange(&status->state, 0, 0);
        if (status->stage != previousStage) { previousStage = status->stage; Record(-1); }
        if (state == CloudGiTouch::Failed) return false;
        if (state == wanted) return true;
    }
    return false;
}
int Error(int code) { Record(code); std::cerr << "CLOUDGI_MOBILE_UI_ERROR " << code << '\n'; return code; }
}

int wmain(int argc, wchar_t** argv) {
    if (argc == 2 && std::wstring(argv[1]) == L"--help") {
        std::cout << "CloudGI.MobileUI --game <YuanShen.exe|GenshinImpact.exe> --arguments <raw arguments>\n";
        return 0;
    }
    if (argc != 5 || std::wstring(argv[1]) != L"--game" || std::wstring(argv[3]) != L"--arguments") return Error(2);
    try {
        const auto game = fs::absolute(argv[2]).lexically_normal();
        const auto name = game.filename().wstring();
        if ((_wcsicmp(name.c_str(), L"YuanShen.exe") && _wcsicmp(name.c_str(), L"GenshinImpact.exe")) || !IsX64(game)) return Error(3);
        const std::wstring arguments = argv[4];
        if (arguments.size() > 8192 || arguments.find_first_of(L"\r\n") != std::wstring::npos) return Error(2);
        wchar_t ownPath[32768]{};
        if (!GetModuleFileNameW(nullptr, ownPath, 32768)) return Error(4);
        const auto dll = fs::path(ownPath).parent_path() / L"CloudGI.Touchscreen.dll";
        if (!fs::is_regular_file(dll)) return Error(4);

        // Kill the newly owned game if this helper dies before the handoff to Host completes.
        Handle job(CreateJobObjectW(nullptr, nullptr));
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!job.value || !SetInformationJobObject(job.value, JobObjectExtendedLimitInformation, &limits, sizeof(limits))) return Error(5);
        Child child;
        std::wstring command = L"\"" + game.wstring() + L"\" " + arguments;
        STARTUPINFOW si{sizeof(si)};
        if (!CreateProcessW(game.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_SUSPENDED | DETACHED_PROCESS,
            nullptr, game.parent_path().c_str(), &si, &child.pi)) return Error(6);
        if (!AssignProcessToJobObject(job.value, child.pi.hProcess)) return Error(5);

        const auto mappingName = CloudGiTouch::MappingName(child.pi.dwProcessId);
        Handle mapping(CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(CloudGiTouch::Status), mappingName.c_str()));
        if (!mapping.value || GetLastError() == ERROR_ALREADY_EXISTS) return Error(7);
        View status{static_cast<CloudGiTouch::Status*>(MapViewOfFile(mapping.value, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(CloudGiTouch::Status)))};
        if (!status.value) return Error(7);
        diagnosticStatus = status.value;
        diagnosticPid = child.pi.dwProcessId;
        status.value->version = CloudGiTouch::ProtocolVersion;
        CloudGiTouch::Publish(status.value, CloudGiTouch::Loading);
        if (!LoadTouchDll(child, dll)) return Error(8);
        if (!WaitState(child, status.value, CloudGiTouch::HookReady, 45000)) {
            std::cerr << "state=" << status.value->state << " stage=" << status.value->stage
                << " detail=" << status.value->detail << '\n';
            if (status.value->error == CloudGiTouch::UnsupportedGame) return Error(9);
            if (status.value->error == CloudGiTouch::HookFailed)
                return Error((status.value->stage == 5 ? 20 : status.value->stage == 6 ? 40 : 60) + status.value->detail);
            if (status.value->stage == 2 || status.value->stage == 3) return Error(14);
            if (status.value->stage == 0) return Error(15);
            return Error(16);
        }
        if (ResumeThread(child.pi.hThread) == DWORD(-1)) return Error(11);
        // Match upstream: let the game initialize and stream after hook setup.
        // ChangeFOV may not run at the login screen; waiting for it here blocks the client.
        limits.BasicLimitInformation.LimitFlags = 0;
        if (!SetInformationJobObject(job.value, JobObjectExtendedLimitInformation, &limits, sizeof(limits))) return Error(5);
        Record(0);
        child.released = true;
        std::cout << "{\"processId\":" << child.pi.dwProcessId << ",\"touchscreenPrepared\":true}\n";
        return 0;
    } catch (...) { return Error(13); }
}
