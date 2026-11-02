/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the AGPL-3.0 License.
*/
#include "Scanner.h"
#include <Windows.h>
#include <Psapi.h>
#include <algorithm>
#include <cstdint>
#include <cstring>
#include <sstream>
#include <vector>

namespace Scanner
{
    static std::vector<int> ParsePattern(const std::string& signature)
    {
        std::vector<int> pattern;
        std::istringstream stream(signature);
        std::string word;
        while (stream >> word)
        {
            if (word == "?" || word == "??") pattern.push_back(-1);
            else
            {
                try { pattern.push_back(std::stoi(word, nullptr, 16)); }
                catch (...) { pattern.push_back(-1); }
            }
        }
        return pattern;
    }

    namespace
    {
        // Stop at the first matching instruction window in readable executable pages.
        bool ScanRegions(uintptr_t start, uintptr_t end, const std::vector<int>& pattern, void*& found)
        {
            for (auto current = start; current < end;)
            {
                MEMORY_BASIC_INFORMATION memory{};
                if (!VirtualQuery(reinterpret_cast<void*>(current), &memory, sizeof(memory))) return false;
                const auto next = (std::min)(end,
                    reinterpret_cast<uintptr_t>(memory.BaseAddress) + memory.RegionSize);
                if (next <= current) return false;
                const DWORD readable = PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;
                if (memory.State == MEM_COMMIT && !(memory.Protect & PAGE_GUARD) && (memory.Protect & readable))
                {
                    auto cursor = reinterpret_cast<const uint8_t*>(current);
                    const auto finish = reinterpret_cast<const uint8_t*>(next);
                    while (finish - cursor >= static_cast<ptrdiff_t>(pattern.size()))
                    {
                        const auto hit = pattern.front() < 0 ? cursor : static_cast<const uint8_t*>(
                            std::memchr(cursor, pattern.front(), finish - cursor - pattern.size() + 1));
                        if (!hit) break;
                        bool match = true;
                        for (size_t j = 0; j < pattern.size(); ++j)
                            if (pattern[j] != -1 && pattern[j] != hit[j]) { match = false; break; }
                        if (match)
                        {
                            found = const_cast<uint8_t*>(hit);
                            return true;
                        }
                        cursor = hit + 1;
                    }
                }
                current = next;
            }
            return true;
        }

        bool MainModule(MODULEINFO& info)
        {
            return GetModuleInformation(GetCurrentProcess(), GetModuleHandleW(nullptr), &info, sizeof(info)) != FALSE;
        }
    }

    void* ScanFirstMainMod(const std::string& signature)
    {
        MODULEINFO info{};
        if (!MainModule(info)) return nullptr;
        const auto pattern = ParsePattern(signature);
        if (pattern.empty()) return nullptr;
        const auto base = reinterpret_cast<uintptr_t>(info.lpBaseOfDll);
        const auto imageEnd = base + info.SizeOfImage;
        const auto dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
        if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew < 0 ||
            static_cast<size_t>(dos->e_lfanew) > info.SizeOfImage - sizeof(IMAGE_NT_HEADERS)) return nullptr;
        const auto nt = reinterpret_cast<const IMAGE_NT_HEADERS*>(base + dos->e_lfanew);
        if (nt->Signature != IMAGE_NT_SIGNATURE) return nullptr;
        const auto sections = IMAGE_FIRST_SECTION(nt);
        void* found = nullptr;
        for (unsigned i = 0; i < nt->FileHeader.NumberOfSections; ++i)
        {
            if (!(sections[i].Characteristics & IMAGE_SCN_MEM_EXECUTE)) continue;
            const auto begin = base + sections[i].VirtualAddress;
            const auto finish = (std::min)(imageEnd, base + sections[i].VirtualAddress + sections[i].Misc.VirtualSize);
            if (begin < finish && !ScanRegions(begin, finish, pattern, found)) return nullptr;
            if (found) return found;
        }
        return found;
    }

}
