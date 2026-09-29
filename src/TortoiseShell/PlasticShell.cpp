// SPDX-License-Identifier: GPL-2.0-or-later
// TortoiseSCM's lightweight Plastic SCM Explorer context menu.
// No Plastic CLI process is executed inside Explorer.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shlobj.h>
#include <shellapi.h>
#include <wrl/client.h>
#include <strsafe.h>
#include <atomic>
#include <algorithm>
#include <filesystem>
#include <new>
#include <string>
#include <vector>
#include "PlasticOverlay.h"

namespace
{
constexpr CLSID ShellClsid = {0xb1da45f9, 0x4cd4, 0x4857, {0xa5, 0x91, 0x96, 0xb0, 0x69, 0x53, 0xa0, 0xd2}};
constexpr CLSID ExplorerCommandClsid = {0xb1da45f9, 0x4cd4, 0x4857, {0xa5, 0x91, 0x96, 0xb0, 0x69, 0x53, 0xa0, 0xdb}};
constexpr CLSID OverlayClsids[] = {
    {0xb1da45f9, 0x4cd4, 0x4857, {0xa5, 0x91, 0x96, 0xb0, 0x69, 0x53, 0xa0, 0xd3}},
    {0xb1da45f9, 0x4cd4, 0x4857, {0xa5, 0x91, 0x96, 0xb0, 0x69, 0x53, 0xa0, 0xd4}},
    {0xb1da45f9, 0x4cd4, 0x4857, {0xa5, 0x91, 0x96, 0xb0, 0x69, 0x53, 0xa0, 0xd5}},
    {0xb1da45f9, 0x4cd4, 0x4857, {0xa5, 0x91, 0x96, 0xb0, 0x69, 0x53, 0xa0, 0xd6}},
    {0xb1da45f9, 0x4cd4, 0x4857, {0xa5, 0x91, 0x96, 0xb0, 0x69, 0x53, 0xa0, 0xd7}},
    {0xb1da45f9, 0x4cd4, 0x4857, {0xa5, 0x91, 0x96, 0xb0, 0x69, 0x53, 0xa0, 0xd8}},
    {0xb1da45f9, 0x4cd4, 0x4857, {0xa5, 0x91, 0x96, 0xb0, 0x69, 0x53, 0xa0, 0xd9}},
    {0xb1da45f9, 0x4cd4, 0x4857, {0xa5, 0x91, 0x96, 0xb0, 0x69, 0x53, 0xa0, 0xda}}
};
HINSTANCE moduleInstance;
std::atomic<long> moduleReferences{0};

HBITMAP CreateMenuBitmap(HICON icon, int width, int height)
{
    if (!icon || width <= 0 || height <= 0) return nullptr;
    HDC dc = CreateCompatibleDC(nullptr);
    if (!dc) return nullptr;
    BITMAPINFO info{};
    info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    info.bmiHeader.biWidth = width;
    info.bmiHeader.biHeight = -height;
    info.bmiHeader.biPlanes = 1;
    info.bmiHeader.biBitCount = 32;
    info.bmiHeader.biCompression = BI_RGB;
    void* pixels = nullptr;
    HBITMAP bitmap = CreateDIBSection(dc, &info, DIB_RGB_COLORS, &pixels, nullptr, 0);
    bool drawn = false;
    if (bitmap)
    {
        HGDIOBJ previous = SelectObject(dc, bitmap);
        if (previous && previous != HGDI_ERROR)
        {
            // The bundled icon has an alpha channel. Drawing onto a transparent
            // 32-bit DIB produces the premultiplied alpha MIIM_BITMAP requires.
            // GetIconInfo().hbmColor contains straight alpha and gives pale fringes.
            ZeroMemory(pixels, static_cast<size_t>(width) * height * 4);
            drawn = DrawIconEx(dc, 0, 0, icon, width, height, 0, nullptr, DI_NORMAL) != FALSE;
            GdiFlush();
            SelectObject(dc, previous);
        }
        if (!drawn) { DeleteObject(bitmap); bitmap = nullptr; }
    }
    DeleteDC(dc);
    return bitmap;
}

struct Command { const wchar_t* name; const wchar_t* label; const wchar_t* chineseLabel; };
constexpr Command commands[] = {
    {L"status", L"Pending changes...", L"待处理更改..."}, {L"checkin", L"Check in...", L"签入..."},
    {L"update", L"Update...", L"更新..."}, {L"add", L"Add...", L"添加..."},
    {L"checkout", L"Check out", L"签出"}, {L"undo", L"Undo changes...", L"撤销更改..."},
    {L"diff", L"Diff...", L"比较差异..."}, {L"history", L"History...", L"历史记录..."},
    {L"gluon", L"Open Gluon", L"打开 Gluon"}, {L"settings", L"Settings...", L"设置..."},
    // Keep command identities stable (including modern canonical GUIDs).
    // Display order is defined separately below.
    {L"move", L"Move / rename...", L"\u79fb\u52a8 / \u91cd\u547d\u540d..."},
    {L"remove", L"Remove controlled item...", L"\u5220\u9664\u53d7\u63a7\u9879..."},
    {L"ignore", L"Add to ignore list", L"\u52a0\u5165\u5ffd\u7565\u5217\u8868"},
    {L"locks", L"Locks...", L"\u9501\u7ba1\u7406..."},
    {L"unlock", L"Unlock...", L"\u91ca\u653e\u9501..."},
    {L"merge", L"Merge changesets...", L"\u5408\u5e76\u53d8\u66f4\u96c6..."},
    {L"export", L"Export historical version...", L"\u5bfc\u51fa\u5386\u53f2\u7248\u672c..."},
    {L"rollback", L"Rollback to historical version...", L"\u56de\u6eda\u5230\u5386\u53f2\u7248\u672c..."},
    {L"recover", L"Recover historical version...", L"\u6062\u590d\u5386\u53f2\u7248\u672c..."},
    {L"branches", L"Branches...", L"分支..."},
    {L"shelves", L"Shelvesets...", L"暂存集..."},
    {L"blame", L"Annotate / Blame...", L"Annotate / Blame..."},
    {L"repository-browser", L"Repository browser...", L"仓库浏览器..."},
    {L"labels", L"Labels...", L"标签..."},
    {L"revision-graph", L"Revision graph...", L"提交关系图..."},
    {L"version", L"Version information...", L"版本信息..."},
    {L"create-workspace", L"Check out repository...", L"\u62c9\u53d6\u4ed3\u5e93..."},
    {L"checkout-recursive", L"Recursive check out", L"递归签出"}
};

// Shared presentation order for classic and modern Explorer menus. The values
// refer to stable command identities above, not visible menu offsets.
// Status (identity 0) shares the check-in window and has no separate menu entry.
constexpr size_t MenuSeparator = static_cast<size_t>(-1);
constexpr size_t menuOrder[] = {
    2, 1, 6, 7,             // Update, check in, diff, history.
    MenuSeparator,
    3, 4, 27, 5, 10, 11, 12, // Add, checkout, recursive checkout, undo, move, remove, ignore.
    19, 15, 20, 23,         // Branches, merge, shelvesets, labels.
    22, 24, 21, 16, 18, 17, // Repository, graph, blame, export, recover, rollback.
    13, 14, 8, 9, 25,       // Locks, unlock, Gluon, settings, version.
    26                      // Checkout outside an existing workspace.
};
constexpr bool ValidMenuOrder()
{
    if constexpr (ARRAYSIZE(menuOrder) != ARRAYSIZE(commands)) return false;
    bool seen[ARRAYSIZE(commands)]{};
    seen[0] = true; // Reserved status identity must not appear in either menu.
    size_t separators = 0;
    for (const size_t index : menuOrder)
    {
        if (index == MenuSeparator) { ++separators; continue; }
        if (index >= ARRAYSIZE(commands) || seen[index]) return false;
        seen[index] = true;
    }
    return separators == 1;
}
static_assert(ValidMenuOrder(), "Menu order must contain each command except status exactly once");

const wchar_t* Label(const Command& command)
{
    return PRIMARYLANGID(GetUserDefaultUILanguage()) == LANG_CHINESE ? command.chineseLabel : command.label;
}

std::wstring WorkspaceRoot(const std::wstring& input);
bool CheckoutParent(const std::vector<std::wstring>& paths);

bool CommandVisible(size_t index, const std::vector<std::wstring>& paths)
{
    if (index >= ARRAYSIZE(commands) || paths.empty()) return false;
    if (wcscmp(commands[index].name, L"create-workspace") == 0) return CheckoutParent(paths);
    if (WorkspaceRoot(paths.front()).empty()) return false;
    if (wcscmp(commands[index].name, L"checkout") == 0)
    {
        return std::all_of(paths.begin(), paths.end(), [](const std::wstring& path)
        {
            const DWORD attributes = GetFileAttributesW(path.c_str());
            return attributes != INVALID_FILE_ATTRIBUTES && !(attributes & FILE_ATTRIBUTE_DIRECTORY);
        });
    }
    if (wcscmp(commands[index].name, L"checkout-recursive") == 0)
    {
        if (paths.size() != 1) return false;
        const DWORD attributes = GetFileAttributesW(paths.front().c_str());
        return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY);
    }
    const bool singlePathOnly = index == 6 || index == 7 || index >= 10;
    if (singlePathOnly && paths.size() != 1) return false;
    if (index == 6 || wcscmp(commands[index].name, L"blame") == 0)
    {
        const DWORD attributes = GetFileAttributesW(paths.front().c_str());
        if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY)) return false;
    }
    return true;
}

std::wstring WorkspaceRoot(const std::wstring& input)
{
    if (input.empty() || input.find_first_of(L"\r\n") != std::wstring::npos)
        return {};
    std::filesystem::path path(input);
    if (!path.is_absolute())
        return {};
    path = path.lexically_normal();
    for (const auto& part : path)
        if (_wcsicmp(part.c_str(), L".plastic") == 0)
            return {};
    const DWORD attributes = GetFileAttributesW(path.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES)
        return {};
    if (!(attributes & FILE_ATTRIBUTE_DIRECTORY))
        path = path.parent_path();
    for (;;)
    {
        const auto marker = path / L".plastic" / L"plastic.workspace";
        const DWORD markerAttributes = GetFileAttributesW(marker.c_str());
        if (markerAttributes != INVALID_FILE_ATTRIBUTES && !(markerAttributes & FILE_ATTRIBUTE_DIRECTORY))
            return path.native();
        const auto parent = path.parent_path();
        if (parent.empty() || parent == path)
            return {};
        path = parent;
    }
}

// Checkout creates a workspace beneath one existing ordinary directory. Keep
// nested workspaces and Plastic metadata out of this entry point.
bool CheckoutParent(const std::vector<std::wstring>& paths)
{
    if (paths.size() != 1 || paths.front().empty() || paths.front().find_first_of(L"\r\n") != std::wstring::npos) return false;
    const std::filesystem::path path(paths.front());
    if (!path.is_absolute()) return false;
    for (const auto& part : path.lexically_normal())
        if (_wcsicmp(part.c_str(), L".plastic") == 0) return false;
    const DWORD attributes = GetFileAttributesW(path.c_str());
    return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) && WorkspaceRoot(path.native()).empty();
}

// Windows argv quoting: backslashes preceding quotes and the closing quote double.
std::wstring Quote(const std::wstring& value)
{
    std::wstring result = L"\"";
    size_t slashes = 0;
    for (wchar_t ch : value)
    {
        if (ch == L'\\') { ++slashes; continue; }
        result.append(slashes * (ch == L'"' ? 2 : 1), L'\\');
        slashes = 0;
        if (ch == L'"') result += L'\\';
        result += ch;
    }
    result.append(slashes * 2, L'\\');
    return result + L'"';
}

std::string Ascii(const std::wstring& value)
{
    std::string result;
    for (const wchar_t ch : value) result += static_cast<char>(ch);
    return result;
}

size_t ResolveCommand(const CMINVOKECOMMANDINFO* info, const std::vector<size_t>& visible)
{
    if (!info) return ARRAYSIZE(commands);
    const bool unicode = info->cbSize >= sizeof(CMINVOKECOMMANDINFOEX) && (info->fMask & CMIC_MASK_UNICODE);
    const wchar_t* wideVerb = unicode ? reinterpret_cast<const CMINVOKECOMMANDINFOEX*>(info)->lpVerbW : nullptr;
    // CMIC_MASK_UNICODE selects lpVerbW only for a Unicode string verb.
    // Explorer still passes numeric menu offsets in lpVerb, often leaving
    // lpVerbW null even when the Unicode flag is present.
    const bool wideString = unicode && reinterpret_cast<UINT_PTR>(wideVerb) > 0xffff;
    const auto rawVerb = reinterpret_cast<UINT_PTR>(info->lpVerb);
    if (!wideString && rawVerb <= 0xffff)
    {
        const size_t offset = LOWORD(rawVerb);
        return offset < visible.size() ? visible[offset] : ARRAYSIZE(commands);
    }
    for (const size_t index : visible)
    {
        const std::wstring verb = L"tortoisescm." + std::wstring(commands[index].name);
        if (wideString ? _wcsicmp(verb.c_str(), wideVerb) == 0 : _stricmp(Ascii(verb).c_str(), info->lpVerb) == 0)
            return index;
    }
    return ARRAYSIZE(commands);
}

HRESULT Launch(const Command& command, const std::vector<std::wstring>& paths, HWND parent)
{
    wchar_t modulePath[32768]{};
    const DWORD moduleLength = GetModuleFileNameW(moduleInstance, modulePath, ARRAYSIZE(modulePath));
    if (!moduleLength || moduleLength >= ARRAYSIZE(modulePath)) return E_FAIL;
    const auto directory = std::filesystem::path(modulePath).parent_path();
    const auto executable = directory / L"TortoiseSCM.exe";
    if (GetFileAttributesW(executable.c_str()) == INVALID_FILE_ATTRIBUTES)
    {
        MessageBoxW(parent, L"TortoiseSCM.exe must be installed beside TortoiseSCMShell.dll.", L"TortoiseSCM", MB_OK | MB_ICONERROR);
        return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
    }
    std::wstring arguments = Quote(executable.native()) + L" --command " + Quote(command.name);
    std::wstring tempFile;
    if (wcscmp(command.name, L"create-workspace") == 0)
    {
        if (!CheckoutParent(paths)) return E_INVALIDARG;
        arguments += L" --parent-path " + Quote(paths.front());
    }
    else
    {
        wchar_t tempDirectory[MAX_PATH + 1]{};
        const DWORD tempLength = GetTempPathW(ARRAYSIZE(tempDirectory), tempDirectory);
        if (!tempLength || tempLength >= ARRAYSIZE(tempDirectory)) return E_FAIL;
        GUID unique{};
        if (FAILED(CoCreateGuid(&unique))) return E_FAIL;
        wchar_t uniqueText[40]{};
        StringFromGUID2(unique, uniqueText, ARRAYSIZE(uniqueText));
        const auto tempFilePath = std::filesystem::path(tempDirectory) / (L"tscm-" + std::wstring(uniqueText) + L".paths");
        tempFile = tempFilePath.native();
        std::wstring contents;
        for (const auto& path : paths) contents += path + L"\n";
        const int length = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, contents.c_str(), static_cast<int>(contents.size()), nullptr, 0, nullptr, nullptr);
        std::string utf8(length > 0 ? length : 0, '\0');
        if (!length || !WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, contents.c_str(), static_cast<int>(contents.size()), utf8.data(), length, nullptr, nullptr))
        { DeleteFileW(tempFile.c_str()); return E_FAIL; }
        HANDLE file = CreateFileW(tempFile.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_TEMPORARY, nullptr);
        if (file == INVALID_HANDLE_VALUE) return HRESULT_FROM_WIN32(GetLastError());
        DWORD written = 0;
        const BOOL success = WriteFile(file, utf8.data(), static_cast<DWORD>(utf8.size()), &written, nullptr);
        const DWORD error = GetLastError();
        CloseHandle(file);
        if (!success || written != utf8.size()) { DeleteFileW(tempFile.c_str()); return HRESULT_FROM_WIN32(success ? ERROR_WRITE_FAULT : error); }
        arguments += L" --pathfile " + Quote(tempFile);
    }
    STARTUPINFOW startup{sizeof(startup)};
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(executable.c_str(), arguments.data(), nullptr, nullptr, FALSE, 0, nullptr, directory.c_str(), &startup, &process))
    {
        const DWORD launchError = GetLastError();
        if (!tempFile.empty()) DeleteFileW(tempFile.c_str());
        MessageBoxW(parent, L"Unable to start TortoiseSCM. Check the installation and required .NET desktop runtime.", L"TortoiseSCM", MB_OK | MB_ICONERROR);
        return HRESULT_FROM_WIN32(launchError);
    }
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return S_OK;
}

class PlasticShell final : public IShellExtInit, public IContextMenu
{
    std::atomic<ULONG> references{1};
    std::vector<std::wstring> paths;
    std::vector<size_t> visibleCommands;
    HBITMAP menuBitmap = nullptr;
public:
    PlasticShell() { ++moduleReferences; }
    ~PlasticShell() { if (menuBitmap) DeleteObject(menuBitmap); --moduleReferences; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** output) override
    {
        if (!output) return E_POINTER;
        *output = nullptr;
        if (iid == IID_IUnknown || iid == IID_IShellExtInit) *output = static_cast<IShellExtInit*>(this);
        else if (iid == IID_IContextMenu) *output = static_cast<IContextMenu*>(this);
        else return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++references; }
    ULONG STDMETHODCALLTYPE Release() override { const ULONG count = --references; if (!count) delete this; return count; }
    HRESULT STDMETHODCALLTYPE Initialize(PCIDLIST_ABSOLUTE folder, IDataObject* data, HKEY) override
    {
        try
        {
            paths.clear(); visibleCommands.clear();
            if (data)
            {
                FORMATETC format{CF_HDROP, nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL};
                STGMEDIUM medium{};
                if (FAILED(data->GetData(&format, &medium))) return E_INVALIDARG;
                // Keep the storage medium alive while DragQueryFile reads its handle.
                struct MediumGuard { STGMEDIUM* value; ~MediumGuard() { ReleaseStgMedium(value); } } guard{&medium};
                const auto drop = static_cast<HDROP>(medium.hGlobal);
                const UINT count = DragQueryFileW(drop, 0xffffffff, nullptr, 0);
                if (!count || count > 10000) return E_INVALIDARG;
                for (UINT index = 0; index < count; ++index)
                {
                    const UINT length = DragQueryFileW(drop, index, nullptr, 0);
                    std::wstring path(length + 1, L'\0');
                    if (!DragQueryFileW(drop, index, path.data(), length + 1)) return E_INVALIDARG;
                    path.resize(length); paths.push_back(std::move(path));
                }
            }
            else if (folder)
            {
                wchar_t path[32768]{};
                if (!SHGetPathFromIDListEx(folder, path, ARRAYSIZE(path), GPFIDL_DEFAULT)) return E_INVALIDARG;
                paths.emplace_back(path);
            }
            if (paths.empty()) return E_INVALIDARG;
            const auto root = WorkspaceRoot(paths.front());
            if (root.empty()) { if (!CheckoutParent(paths)) paths.clear(); return S_OK; }
            for (const auto& path : paths)
                if (_wcsicmp(WorkspaceRoot(path).c_str(), root.c_str()) != 0) { paths.clear(); break; }
            return S_OK;
        }
        catch (...) { paths.clear(); return E_FAIL; }
    }
    HRESULT STDMETHODCALLTYPE QueryContextMenu(HMENU menu, UINT position, UINT first, UINT last, UINT flags) override
    {
        try
        {
            visibleCommands.clear();
            if (paths.empty() || (flags & CMF_DEFAULTONLY) || last < first) return MAKE_HRESULT(SEVERITY_SUCCESS, 0, 0);
            HMENU submenu = CreatePopupMenu();
            if (!submenu) return E_OUTOFMEMORY;
            bool separatorPending = false;
            for (const size_t index : menuOrder)
            {
                if (index == MenuSeparator) { separatorPending = true; continue; }
                // Path-specific dialogs are intentionally limited to one item.
                // This keeps move/remove/ignore and history actions safe for
                // Explorer multi-selection while retaining the full GUI flow.
                if (!CommandVisible(index, paths)) continue;
                if (visibleCommands.size() > last - first) break;
                // Add only between two visible groups, without consuming a verb ID.
                if (separatorPending && !visibleCommands.empty() && !AppendMenuW(submenu, MF_SEPARATOR, 0, nullptr))
                { DestroyMenu(submenu); return E_FAIL; }
                separatorPending = false;
                if (!AppendMenuW(submenu, MF_STRING, first + visibleCommands.size(), Label(commands[index]))) { DestroyMenu(submenu); return E_FAIL; }
                visibleCommands.push_back(index);
            }
            if (visibleCommands.empty()) { DestroyMenu(submenu); return MAKE_HRESULT(SEVERITY_SUCCESS, 0, 0); }
            if (!InsertMenuW(menu, position, MF_BYPOSITION | MF_POPUP, reinterpret_cast<UINT_PTR>(submenu), L"TortoiseSCM")) { DestroyMenu(submenu); return E_FAIL; }
            if (!menuBitmap)
            {
                HICON icon = static_cast<HICON>(LoadImageW(moduleInstance, MAKEINTRESOURCEW(1), IMAGE_ICON, 16, 16, LR_DEFAULTCOLOR));
                if (icon)
                {
                    menuBitmap = CreateMenuBitmap(icon, 16, 16);
                    DestroyIcon(icon);
                }
            }
            if (menuBitmap)
            {
                MENUITEMINFOW item{sizeof(item)};
                item.fMask = MIIM_BITMAP; item.hbmpItem = menuBitmap;
                SetMenuItemInfoW(menu, position, TRUE, &item);
            }
            return MAKE_HRESULT(SEVERITY_SUCCESS, 0, static_cast<USHORT>(visibleCommands.size()));
        }
        catch (...) { return E_OUTOFMEMORY; }
    }
    HRESULT STDMETHODCALLTYPE InvokeCommand(CMINVOKECOMMANDINFO* info) override
    {
        if (!info || paths.empty()) return E_INVALIDARG;
        try
        {
            const size_t selected = ResolveCommand(info, visibleCommands);
            if (selected == ARRAYSIZE(commands) || !CommandVisible(selected, paths)) return E_INVALIDARG;
            return Launch(commands[selected], paths, info->hwnd);
        }
        catch (...) { return E_FAIL; }
    }
    HRESULT STDMETHODCALLTYPE GetCommandString(UINT_PTR offset, UINT flags, UINT*, LPSTR output, UINT capacity) override
    {
        if (offset >= visibleCommands.size()) return E_INVALIDARG;
        if (flags == GCS_VALIDATEA || flags == GCS_VALIDATEW) return S_OK;
        if (!output || !capacity) return E_INVALIDARG;
        try
        {
            const auto& command = commands[visibleCommands[offset]];
            const std::wstring text = (flags == GCS_VERBA || flags == GCS_VERBW) ? L"tortoisescm." + std::wstring(command.name) : Label(command);
            if (flags & GCS_UNICODE) return StringCchCopyW(reinterpret_cast<wchar_t*>(output), capacity, text.c_str());
            if (!WideCharToMultiByte(CP_ACP, 0, text.c_str(), -1, output, static_cast<int>(capacity), nullptr, nullptr)) return HRESULT_FROM_WIN32(GetLastError());
            return S_OK;
        }
        catch (...) { return E_FAIL; }
    }
};

#include "PlasticExplorerCommand.h"

class Overlay final : public IShellIconOverlayIdentifier
{
    std::atomic<ULONG> references{1};
    const PlasticOverlay::State state;
public:
    explicit Overlay(PlasticOverlay::State value) : state(value) { ++moduleReferences; }
    ~Overlay() { --moduleReferences; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** output) override
    {
        if (!output) return E_POINTER;
        *output = nullptr;
        if (iid != IID_IUnknown && iid != IID_IShellIconOverlayIdentifier) return E_NOINTERFACE;
        *output = static_cast<IShellIconOverlayIdentifier*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++references; }
    ULONG STDMETHODCALLTYPE Release() override { const ULONG count = --references; if (!count) delete this; return count; }
    HRESULT STDMETHODCALLTYPE IsMemberOf(LPCWSTR path, DWORD) override
    {
        if (!path) return E_INVALIDARG;
        try
        {
            const size_t length = wcsnlen_s(path, PlasticOverlay::MaxPathChars + 1);
            if (!length || length > PlasticOverlay::MaxPathChars) return S_FALSE;
            return PlasticOverlay::SharedCache().Lookup(std::wstring(path, length)) == state ? S_OK : S_FALSE;
        }
        catch (...) { return S_FALSE; }
    }
    HRESULT STDMETHODCALLTYPE GetPriority(int* priority) override
    {
        if (!priority) return E_POINTER;
        // Explorer asks overlays independently; lower numbers win when more
        // than one state applies to the same path. Conflicts must always be
        // visible, while normal inventory remains the least specific state.
        switch (state)
        {
            case PlasticOverlay::Conflict: *priority = 0; break;
            case PlasticOverlay::Added:
            case PlasticOverlay::Deleted:
            case PlasticOverlay::Modified: *priority = 1; break;
            case PlasticOverlay::Locked: *priority = 2; break;
            case PlasticOverlay::Ignored:
            case PlasticOverlay::Unversioned: *priority = 3; break;
            default: *priority = 4; break;
        }
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetOverlayInfo(LPWSTR file, int capacity, int* index, DWORD* flags) override
    {
        if (!file || !index || !flags) return E_POINTER;
        if (capacity <= 0) return E_INVALIDARG;
        *index = 0; *flags = 0;
        const DWORD length = GetModuleFileNameW(moduleInstance, file, static_cast<DWORD>(capacity));
        if (!length || length >= static_cast<DWORD>(capacity)) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
        // Resource order: context-menu icon 1, then overlay resources 101-108.
        *index = static_cast<int>(state);
        *flags = ISIOI_ICONFILE | ISIOI_ICONINDEX;
        return S_OK;
    }
};

class Factory final : public IClassFactory
{
    std::atomic<ULONG> references{1};
    const PlasticOverlay::State overlayState;
    const bool modern;
public:
    explicit Factory(PlasticOverlay::State state = PlasticOverlay::None, bool explorer = false) : overlayState(state), modern(explorer) { ++moduleReferences; }
    ~Factory() { --moduleReferences; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** output) override
    {
        if (!output) return E_POINTER;
        *output = nullptr;
        if (iid != IID_IUnknown && iid != IID_IClassFactory) return E_NOINTERFACE;
        *output = static_cast<IClassFactory*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++references; }
    ULONG STDMETHODCALLTYPE Release() override { const ULONG count = --references; if (!count) delete this; return count; }
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID iid, void** output) override
    {
        if (!output) return E_POINTER;
        *output = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        if (modern)
        {
            auto command = new (std::nothrow) PlasticExplorerCommand;
            if (!command) return E_OUTOFMEMORY;
            const HRESULT result = command->QueryInterface(iid, output); command->Release(); return result;
        }
        if (overlayState != PlasticOverlay::None)
        {
            auto overlay = new (std::nothrow) Overlay(overlayState);
            if (!overlay) return E_OUTOFMEMORY;
            const HRESULT result = overlay->QueryInterface(iid, output); overlay->Release(); return result;
        }
        auto shell = new (std::nothrow) PlasticShell;
        if (!shell) return E_OUTOFMEMORY;
        const HRESULT result = shell->QueryInterface(iid, output); shell->Release(); return result;
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override { if (lock) ++moduleReferences; else --moduleReferences; return S_OK; }
};
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) moduleInstance = instance;
    return TRUE;
}
extern "C" HRESULT WINAPI DllCanUnloadNow() { return moduleReferences == 0 ? S_OK : S_FALSE; }
extern "C" HRESULT WINAPI DllGetClassObject(REFCLSID clsid, REFIID iid, void** output)
{
    if (!output) return E_POINTER;
    *output = nullptr;
    PlasticOverlay::State state = PlasticOverlay::None;
    if (clsid != ShellClsid && clsid != ExplorerCommandClsid)
    {
        for (size_t i = 0; i < ARRAYSIZE(OverlayClsids); ++i)
            if (clsid == OverlayClsids[i]) state = static_cast<PlasticOverlay::State>(i + 1);
        if (state == PlasticOverlay::None) return CLASS_E_CLASSNOTAVAILABLE;
    }
    auto factory = new (std::nothrow) Factory(state, clsid == ExplorerCommandClsid);
    if (!factory) return E_OUTOFMEMORY;
    const HRESULT result = factory->QueryInterface(iid, output); factory->Release(); return result;
}
