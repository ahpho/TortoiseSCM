// SPDX-License-Identifier: GPL-2.0-or-later
#pragma once
#include <cstdint>
#include <cstring>
#include <map>
#include <mutex>
#include <utility>

namespace PlasticOverlay
{
constexpr uint32_t MaxBytes = 32u * 1024u * 1024u;
constexpr uint32_t MaxEntries = 200000;
constexpr uint32_t MaxPathChars = 32767;
constexpr uint64_t Second = 10000000;
enum State : uint32_t { None = 0, Normal = 1, Modified = 2, Conflict = 3 };

struct OrdinalLess
{
    bool operator()(const std::wstring& left, const std::wstring& right) const
    { return CompareStringOrdinal(left.data(), static_cast<int>(left.size()), right.data(), static_cast<int>(right.size()), TRUE) == CSTR_LESS_THAN; }
};
using Entries = std::map<std::wstring, State, OrdinalLess>;

inline uint64_t UtcNow()
{
    FILETIME time{}; GetSystemTimeAsFileTime(&time);
    return (static_cast<uint64_t>(time.dwHighDateTime) << 32) | time.dwLowDateTime;
}

inline bool Fresh(uint64_t generated, uint64_t now)
{ return generated > 0 && (generated <= now ? now - generated <= 120 * Second : generated - now <= 5 * Second); }

// Pure string checks: overlay lookup never touches the requested workspace path.
inline bool ValidPath(const std::wstring& path)
{
    if (path.size() < 3 || path.size() > MaxPathChars) return false;
    const bool drive = ((path[0] >= L'A' && path[0] <= L'Z') || (path[0] >= L'a' && path[0] <= L'z')) && path[1] == L':' && path[2] == L'\\';
    const bool unc = path.size() > 4 && path[0] == L'\\' && path[1] == L'\\' && path[2] != L'?' && path[2] != L'.';
    if (!drive && !unc) return false;
    for (size_t i = 0; i < path.size(); ++i)
    {
        const wchar_t ch = path[i];
        if (ch < 32 || ch == L'/' || ch == L'"' || ch == L'<' || ch == L'>' || ch == L'|' || ch == L'*' || ch == L'?' || (ch == L':' && !(drive && i == 1))) return false;
        if (ch >= 0xd800 && ch <= 0xdbff)
        { if (++i >= path.size() || path[i] < 0xdc00 || path[i] > 0xdfff) return false; }
        else if (ch >= 0xdc00 && ch <= 0xdfff) return false;
    }
    size_t begin = drive ? 3 : 2;
    unsigned components = 0;
    while (begin < path.size())
    {
        const size_t end = path.find(L'\\', begin);
        const auto part = path.substr(begin, end == std::wstring::npos ? end : end - begin);
        if (part.empty() || part == L"." || part == L".." || _wcsicmp(part.c_str(), L".plastic") == 0 || part.back() == L'.' || part.back() == L' ') return false;
        ++components;
        if (end == std::wstring::npos) break;
        begin = end + 1;
        if (begin == path.size()) return false; // canonical entries omit trailing slash except drive roots
    }
    return drive || components >= 2;
}

// Wire v1: ASCII TSCMOVL1, LE uint32 version/count, LE int64 UTC FILETIME,
// followed by { LE uint32 state/UTF16-count, UTF16LE path (no NUL) } records.
inline bool Parse(const std::vector<unsigned char>& bytes, uint64_t now, Entries& output, uint64_t& generated)
{
    output.clear(); generated = 0;
    if (bytes.size() < 24 || bytes.size() > MaxBytes || std::memcmp(bytes.data(), "TSCMOVL1", 8) != 0) return false;
    size_t offset = 8;
    auto read32 = [&]() { uint32_t value = 0; std::memcpy(&value, bytes.data() + offset, 4); offset += 4; return value; };
    if (read32() != 1) return false;
    const uint32_t count = read32();
    uint64_t stamp = 0; std::memcpy(&stamp, bytes.data() + offset, 8); offset += 8;
    if (count > MaxEntries || !Fresh(stamp, now)) return false;
    Entries parsed;
    for (uint32_t i = 0; i < count; ++i)
    {
        if (bytes.size() - offset < 8) return false;
        const uint32_t state = read32(), chars = read32();
        if (state < Normal || state > Conflict || chars == 0 || chars > MaxPathChars || chars > (bytes.size() - offset) / sizeof(wchar_t)) return false;
        std::wstring path(chars, L'\0');
        std::memcpy(path.data(), bytes.data() + offset, chars * sizeof(wchar_t)); offset += chars * sizeof(wchar_t);
        if (!ValidPath(path) || !parsed.emplace(std::move(path), static_cast<State>(state)).second) return false;
    }
    if (offset != bytes.size()) return false;
    output.swap(parsed); generated = stamp; return true;
}

inline std::wstring CachePath()
{
    PWSTR directory = nullptr;
    if (FAILED(SHGetKnownFolderPath(FOLDERID_LocalAppData, KF_FLAG_DONT_VERIFY, nullptr, &directory))) return {};
    const std::wstring root(directory); CoTaskMemFree(directory);
    // A redirected/network profile must never cause network I/O in Explorer.
    if (root.size() < 3 || root[1] != L':' || root[2] != L'\\' || GetDriveTypeW(root.substr(0, 3).c_str()) != DRIVE_FIXED) return {};
    return root + L"\\TortoiseSCM\\overlay-cache-v1.bin";
}

class Cache
{
    std::wstring path;
    std::mutex gate;
    Entries entries;
    uint64_t generated = 0;
    ULONGLONG lastPoll = 0;
    bool polled = false;
    FILETIME lastWrite{};
    DWORD lastSize = 0;
    DWORD lastVolume = 0, lastIndexHigh = 0, lastIndexLow = 0;
    bool identityKnown = false;

    void Invalidate() { entries.clear(); generated = 0; identityKnown = false; }
    void Reload(uint64_t now)
    {
        if (path.empty()) { Invalidate(); return; }
        // Examine only the short cache path, never the requested repository path.
        // Do not follow a reparse point that redirects the local cache elsewhere.
        for (auto parent = std::filesystem::path(path).parent_path(); !parent.empty();)
        {
            const DWORD attributes = GetFileAttributesW(parent.c_str());
            if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_REPARSE_POINT)) { Invalidate(); return; }
            const auto next = parent.parent_path(); if (next == parent) break; parent = next;
        }
        HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
        if (file == INVALID_HANDLE_VALUE) { Invalidate(); return; }
        struct Close { HANDLE handle; ~Close() { CloseHandle(handle); } } closer{file};
        BY_HANDLE_FILE_INFORMATION info{};
        if (!GetFileInformationByHandle(file, &info) || (info.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) ||
            info.nFileSizeHigh != 0 || info.nFileSizeLow < 24 || info.nFileSizeLow > MaxBytes) { Invalidate(); return; }
        if (identityKnown && info.nFileSizeLow == lastSize && CompareFileTime(&info.ftLastWriteTime, &lastWrite) == 0 &&
            info.dwVolumeSerialNumber == lastVolume && info.nFileIndexHigh == lastIndexHigh && info.nFileIndexLow == lastIndexLow) return;
        std::vector<unsigned char> bytes(info.nFileSizeLow);
        DWORD read = 0;
        if (!ReadFile(file, bytes.data(), info.nFileSizeLow, &read, nullptr) || read != info.nFileSizeLow || !Parse(bytes, now, entries, generated))
        { Invalidate(); return; }
        lastWrite = info.ftLastWriteTime; lastSize = info.nFileSizeLow; identityKnown = true;
        lastVolume = info.dwVolumeSerialNumber; lastIndexHigh = info.nFileIndexHigh; lastIndexLow = info.nFileIndexLow;
    }
public:
    explicit Cache(std::wstring value) : path(std::move(value)) {}
    State Lookup(const std::wstring& value, uint64_t now = UtcNow(), ULONGLONG tick = GetTickCount64())
    {
        if (!ValidPath(value)) return None;
        std::unique_lock<std::mutex> lock(gate, std::try_to_lock);
        if (!lock.owns_lock()) return None;
        if (!polled || tick - lastPoll >= 1000)
        { polled = true; lastPoll = tick; Reload(now); }
        if (!Fresh(generated, now)) return None;
        const auto item = entries.find(value);
        return item == entries.end() ? None : item->second;
    }
};
inline Cache& SharedCache() { static Cache cache(CachePath()); return cache; }
}
