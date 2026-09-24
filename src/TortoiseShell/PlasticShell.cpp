// SPDX-License-Identifier: GPL-2.0-or-later
// TortoiseSCM's lightweight Plastic SCM Explorer context menu.
// No Plastic CLI process is executed inside Explorer.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shlobj.h>
#include <shellapi.h>
#include <strsafe.h>
#include <atomic>
#include <filesystem>
#include <new>
#include <string>
#include <vector>

namespace
{
constexpr CLSID ShellClsid = {0xb1da45f9, 0x4cd4, 0x4857, {0xa5, 0x91, 0x96, 0xb0, 0x69, 0x53, 0xa0, 0xd2}};
HINSTANCE moduleInstance;
std::atomic<long> moduleReferences{0};
struct Command { const wchar_t* name; const wchar_t* label; const wchar_t* chineseLabel; };
constexpr Command commands[] = {
    {L"status", L"Pending changes...", L"待处理更改..."}, {L"checkin", L"Check in...", L"签入..."},
    {L"update", L"Update...", L"更新..."}, {L"add", L"Add...", L"添加..."},
    {L"checkout", L"Check out...", L"签出..."}, {L"undo", L"Undo changes...", L"撤销更改..."},
    {L"diff", L"Diff...", L"比较差异..."}, {L"history", L"History...", L"历史记录..."},
    {L"gluon", L"Open Gluon", L"打开 Gluon"}, {L"settings", L"Settings...", L"设置..."}
};

const wchar_t* Label(const Command& command)
{
    return PRIMARYLANGID(GetUserDefaultUILanguage()) == LANG_CHINESE ? command.chineseLabel : command.label;
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
    // The selected encoding determines BOTH ordinal and string interpretation.
    const auto rawVerb = unicode ? reinterpret_cast<UINT_PTR>(wideVerb) : reinterpret_cast<UINT_PTR>(info->lpVerb);
    if (rawVerb <= 0xffff)
    {
        const size_t offset = LOWORD(rawVerb);
        return offset < visible.size() ? visible[offset] : ARRAYSIZE(commands);
    }
    for (const size_t index : visible)
    {
        const std::wstring verb = L"tortoisescm." + std::wstring(commands[index].name);
        if (unicode ? _wcsicmp(verb.c_str(), wideVerb) == 0 : _stricmp(Ascii(verb).c_str(), info->lpVerb) == 0)
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
    wchar_t tempDirectory[MAX_PATH + 1]{};
    const DWORD tempLength = GetTempPathW(ARRAYSIZE(tempDirectory), tempDirectory);
    if (!tempLength || tempLength >= ARRAYSIZE(tempDirectory)) return E_FAIL;
    GUID unique{};
    if (FAILED(CoCreateGuid(&unique))) return E_FAIL;
    wchar_t uniqueText[40]{};
    StringFromGUID2(unique, uniqueText, ARRAYSIZE(uniqueText));
    const auto tempFilePath = std::filesystem::path(tempDirectory) / (L"tscm-" + std::wstring(uniqueText) + L".paths");
    const wchar_t* tempFile = tempFilePath.c_str();
    std::wstring contents;
    for (const auto& path : paths) contents += path + L"\n";
    const int length = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, contents.c_str(), static_cast<int>(contents.size()), nullptr, 0, nullptr, nullptr);
    std::string utf8(length > 0 ? length : 0, '\0');
    if (!length || !WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, contents.c_str(), static_cast<int>(contents.size()), utf8.data(), length, nullptr, nullptr))
    { DeleteFileW(tempFile); return E_FAIL; }
    HANDLE file = CreateFileW(tempFile, GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_TEMPORARY, nullptr);
    if (file == INVALID_HANDLE_VALUE) return HRESULT_FROM_WIN32(GetLastError());
    DWORD written = 0;
    const BOOL success = WriteFile(file, utf8.data(), static_cast<DWORD>(utf8.size()), &written, nullptr);
    const DWORD error = GetLastError();
    CloseHandle(file);
    if (!success || written != utf8.size()) { DeleteFileW(tempFile); return HRESULT_FROM_WIN32(success ? ERROR_WRITE_FAULT : error); }
    std::wstring arguments = Quote(executable.native()) + L" --command " + Quote(command.name) + L" --pathfile " + Quote(tempFile);
    STARTUPINFOW startup{sizeof(startup)};
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(executable.c_str(), arguments.data(), nullptr, nullptr, FALSE, 0, nullptr, directory.c_str(), &startup, &process))
    {
        const DWORD launchError = GetLastError();
        DeleteFileW(tempFile);
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
            if (root.empty()) { paths.clear(); return S_OK; }
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
            for (size_t index = 0; index < ARRAYSIZE(commands); ++index)
            {
                if ((index == 6 && (paths.size() != 1 || (GetFileAttributesW(paths.front().c_str()) & FILE_ATTRIBUTE_DIRECTORY))) || (index == 7 && paths.size() != 1)) continue;
                if (visibleCommands.size() > last - first) break;
                if (!AppendMenuW(submenu, MF_STRING, first + visibleCommands.size(), Label(commands[index]))) { DestroyMenu(submenu); return E_FAIL; }
                visibleCommands.push_back(index);
            }
            if (!InsertMenuW(menu, position, MF_BYPOSITION | MF_POPUP, reinterpret_cast<UINT_PTR>(submenu), L"TortoiseSCM")) { DestroyMenu(submenu); return E_FAIL; }
            if (!menuBitmap)
            {
                HICON icon = static_cast<HICON>(LoadImageW(moduleInstance, MAKEINTRESOURCEW(1), IMAGE_ICON, 16, 16, LR_DEFAULTCOLOR));
                if (icon)
                {
                    ICONINFO iconInfo{};
                    if (GetIconInfo(icon, &iconInfo))
                    {
                        menuBitmap = iconInfo.hbmColor;
                        if (iconInfo.hbmMask) DeleteObject(iconInfo.hbmMask);
                    }
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
            if (selected == ARRAYSIZE(commands)) return E_INVALIDARG;
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

class Factory final : public IClassFactory
{
    std::atomic<ULONG> references{1};
public:
    Factory() { ++moduleReferences; }
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
    if (clsid != ShellClsid) return CLASS_E_CLASSNOTAVAILABLE;
    auto factory = new (std::nothrow) Factory;
    if (!factory) return E_OUTOFMEMORY;
    const HRESULT result = factory->QueryInterface(iid, output); factory->Release(); return result;
}
