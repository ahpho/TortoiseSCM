// SPDX-License-Identifier: GPL-2.0-or-later
#include "../../src/TortoiseShell/PlasticShell.cpp"
#include <iostream>
#include <fstream>
#include <cassert>
#include <chrono>
#include <algorithm>

class Selection : public IDataObject {
    std::vector<std::wstring> entries;
public:
    explicit Selection(std::vector<std::wstring> value) : entries(std::move(value)) {}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID, void**) override { return E_NOINTERFACE; }
    ULONG STDMETHODCALLTYPE AddRef() override { return 1; }
    ULONG STDMETHODCALLTYPE Release() override { return 1; }
    HRESULT STDMETHODCALLTYPE GetData(FORMATETC* format, STGMEDIUM* result) override {
        if (format->cfFormat != CF_HDROP) return DV_E_FORMATETC;
        size_t chars = 1; for (auto& entry : entries) chars += entry.size() + 1;
        HGLOBAL handle = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, sizeof(DROPFILES) + chars * sizeof(wchar_t));
        auto data = static_cast<DROPFILES*>(GlobalLock(handle));
        data->pFiles = sizeof(DROPFILES); data->fWide = TRUE;
        auto buffer = reinterpret_cast<wchar_t*>(reinterpret_cast<char*>(data) + sizeof(DROPFILES));
        for (auto& entry : entries) { memcpy(buffer, entry.c_str(), (entry.size() + 1) * sizeof(wchar_t)); buffer += entry.size() + 1; }
        GlobalUnlock(handle); result->tymed = TYMED_HGLOBAL; result->hGlobal = handle; result->pUnkForRelease = nullptr; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetDataHere(FORMATETC*, STGMEDIUM*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE QueryGetData(FORMATETC*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetCanonicalFormatEtc(FORMATETC*, FORMATETC*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE SetData(FORMATETC*, STGMEDIUM*, BOOL) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE EnumFormatEtc(DWORD, IEnumFORMATETC**) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE DAdvise(FORMATETC*, DWORD, IAdviseSink*, DWORD*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE DUnadvise(DWORD) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE EnumDAdvise(IEnumSTATDATA**) override { return E_NOTIMPL; }
};

void require(bool condition, const char* label) {
    if (!condition) { std::cerr << "FAIL " << label << '\n'; std::exit(1); }
    std::cout << "PASS " << label << '\n';
}

#pragma comment(lib, "msimg32.lib")

void CheckMenuBitmap(HMENU menu, HMODULE resourceModule)
{
    MENUITEMINFOW item{sizeof(item)};
    item.fMask = MIIM_BITMAP;
    require(GetMenuItemInfoW(menu, 0, TRUE, &item) && item.hbmpItem, "production menu supplies an icon bitmap");
    DIBSECTION dib{};
    require(GetObjectW(item.hbmpItem, sizeof(dib), &dib) == sizeof(dib) &&
        dib.dsBm.bmBitsPixel == 32 && dib.dsBm.bmWidth == 16 && dib.dsBm.bmHeight == 16,
        "menu icon is a 16x16 32-bit DIB");
    const auto pixels = static_cast<const RGBQUAD*>(dib.dsBm.bmBits);
    unsigned transparent = 0, partial = 0, opaque = 0;
    bool premultiplied = true;
    for (int i = 0; i < 256; ++i)
    {
        const auto p = pixels[i];
        transparent += p.rgbReserved == 0;
        partial += p.rgbReserved > 0 && p.rgbReserved < 255;
        opaque += p.rgbReserved == 255;
        premultiplied &= p.rgbRed <= p.rgbReserved && p.rgbGreen <= p.rgbReserved && p.rgbBlue <= p.rgbReserved;
    }
    require(transparent && partial && opaque, "menu icon retains transparent antialiased and opaque pixels");
    require(premultiplied, "menu bitmap has premultiplied channels and zero transparent RGB");

    // Compare the actual MIIM_BITMAP using Windows AlphaBlend against drawing
    // the resource icon directly, on both light and dark menu backgrounds.
    HICON icon = static_cast<HICON>(LoadImageW(resourceModule, MAKEINTRESOURCEW(1), IMAGE_ICON, 16, 16, 0));
    require(icon != nullptr, "load production icon for independent native rendering");
    ICONINFO raw{};
    require(GetIconInfo(icon, &raw) != FALSE, "extract old menu bitmap for regression control");
    HDC source = CreateCompatibleDC(nullptr), target = CreateCompatibleDC(nullptr);
    require(source && target, "create native icon comparison DCs");
    BITMAPINFO info{};
    info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    info.bmiHeader.biWidth = 48; info.bmiHeader.biHeight = -32;
    info.bmiHeader.biPlanes = 1; info.bmiHeader.biBitCount = 32;
    void* bits = nullptr;
    HBITMAP canvas = CreateDIBSection(target, &info, DIB_RGB_COLORS, &bits, nullptr, 0);
    require(canvas != nullptr, "create native icon comparison canvas");
    const auto oldTarget = SelectObject(target, canvas);
    const auto oldSource = SelectObject(source, item.hbmpItem);
    auto output = static_cast<RGBQUAD*>(bits);
    for (int y = 0; y < 32; ++y)
        for (int x = 0; x < 48; ++x)
        {
            const BYTE shade = y < 16 ? 32 : 240;
            output[y * 48 + x] = {shade, shade, shade, 255};
        }
    const BLENDFUNCTION blend{AC_SRC_OVER, 0, 255, AC_SRC_ALPHA};
    for (int y : {0, 16})
    {
        require(AlphaBlend(target, 16, y, 16, 16, source, 0, 0, 16, 16, blend) != FALSE,
            "composite actual menu bitmap with Windows AlphaBlend");
        require(DrawIconEx(target, 32, y, icon, 16, 16, 0, nullptr, DI_NORMAL) != FALSE,
            "draw independent resource icon reference");
    }
    SelectObject(source, raw.hbmColor);
    for (int y : {0, 16})
        require(AlphaBlend(target, 0, y, 16, 16, source, 0, 0, 16, 16, blend) != FALSE,
            "render old straight-alpha path as regression control");
    GdiFlush();
    unsigned oldDifferences = 0;
    bool compositesMatch = true;
    for (int y = 0; y < 32; ++y)
        for (int x = 0; x < 16; ++x)
        {
            const auto actual = output[y * 48 + 16 + x], expected = output[y * 48 + 32 + x];
            compositesMatch &= std::abs(int(actual.rgbRed) - expected.rgbRed) <= 1 &&
                std::abs(int(actual.rgbGreen) - expected.rgbGreen) <= 1 &&
                std::abs(int(actual.rgbBlue) - expected.rgbBlue) <= 1;
            const auto old = output[y * 48 + x];
            oldDifferences += std::abs(int(old.rgbRed) - expected.rgbRed) > 1 ||
                std::abs(int(old.rgbGreen) - expected.rgbGreen) > 1 || std::abs(int(old.rgbBlue) - expected.rgbBlue) > 1;
        }
    require(compositesMatch, "menu composite matches native icon on light/dark background");
    require(oldDifferences > 0, "regression control reproduces old icon fringe");
    wchar_t evidence[32768]{};
    if (GetEnvironmentVariableW(L"TORTOISESCM_MENU_BITMAP_QA", evidence, ARRAYSIZE(evidence)))
    {
        BITMAPFILEHEADER header{};
        header.bfType = 0x4d42; header.bfOffBits = sizeof(header) + sizeof(BITMAPINFOHEADER);
        header.bfSize = header.bfOffBits + 48 * 32 * 4;
        std::ofstream file(evidence, std::ios::binary);
        file.write(reinterpret_cast<const char*>(&header), sizeof(header));
        file.write(reinterpret_cast<const char*>(&info.bmiHeader), sizeof(BITMAPINFOHEADER));
        file.write(static_cast<const char*>(bits), 48 * 32 * 4);
        require(file.good(), "save old/fixed/reference light-dark native rendering evidence");
    }
    SelectObject(source, oldSource); SelectObject(target, oldTarget);
    DeleteDC(source); DeleteDC(target); DeleteObject(canvas);
    DeleteObject(raw.hbmColor); DeleteObject(raw.hbmMask); DestroyIcon(icon);
}

// UX contract, independent of the production presentation array. Identities
// retain their pre-reorder values even though visible offsets have changed.
constexpr const wchar_t* expectedMenuVerbs[] = {
    L"update", L"checkin", L"diff", L"history",
    L"add", L"checkout", L"undo", L"move", L"remove", L"ignore",
    L"branches", L"merge", L"shelves", L"labels", L"repository-browser",
    L"revision-graph", L"blame", L"export", L"recover", L"rollback",
    L"locks", L"unlock", L"gluon", L"settings", L"version", L"create-workspace"
};
constexpr size_t expectedMenuIdentities[] = {2,1,6,7,3,4,5,10,11,12,19,15,20,23,22,24,21,16,18,17,13,14,8,9,25,26};

void CheckClassicOrder(IContextMenu* context, HMENU submenu, unsigned selectionKind)
{
    UINT offset = 0;
    for (size_t index = 0; index < ARRAYSIZE(expectedMenuVerbs); ++index)
    {
        const std::wstring name = expectedMenuVerbs[index];
        if (selectionKind >= 3 ? name != L"create-workspace" : name == L"create-workspace") continue;
        if ((selectionKind == 1 || selectionKind == 2) && (name == L"diff" || name == L"blame")) continue;
        wchar_t verb[80]{}, label[128]{};
        require(SUCCEEDED(context->GetCommandString(offset, GCS_VERBW, nullptr, reinterpret_cast<char*>(verb), ARRAYSIZE(verb))) &&
            verb == L"tortoisescm." + name, "classic frequent-first visible verb order");
        require(GetMenuStringW(submenu, offset, label, ARRAYSIZE(label), MF_BYPOSITION) > 0 &&
            wcscmp(label, Label(commands[expectedMenuIdentities[index]])) == 0, "classic visible label matches reordered verb");
        ++offset;
    }
    require(GetMenuItemCount(submenu) == static_cast<int>(offset), "classic menu has no missing or duplicate commands");
}

std::vector<unsigned char> Snapshot(const std::vector<std::pair<std::wstring, uint32_t>>& values, uint64_t now)
{
    std::vector<unsigned char> result{'T','S','C','M','O','V','L','1'};
    const auto append = [&](const void* data, size_t length) { const auto first = static_cast<const unsigned char*>(data); result.insert(result.end(), first, first + length); };
    const uint32_t version = 1, count = static_cast<uint32_t>(values.size());
    append(&version, 4); append(&count, 4); append(&now, 8);
    for (const auto& item : values)
    {
        const uint32_t chars = static_cast<uint32_t>(item.first.size());
        append(&item.second, 4); append(&chars, 4); append(item.first.data(), chars * sizeof(wchar_t));
    }
    return result;
}

void OverlayTests(const std::filesystem::path& directory)
{
    const uint64_t now = PlasticOverlay::UtcNow();
    const std::wstring path = L"D:\\fixture\\中文 file.txt";
    auto good = Snapshot({{path, 2}, {L"D:\\fixture", 3}, {L"D:\\fixture\\clean.txt", 1}}, now);
    PlasticOverlay::Entries parsed; uint64_t generated = 0;
    require(PlasticOverlay::Parse(good, now, parsed, generated) && parsed.size() == 3 && parsed.at(path) == PlasticOverlay::Modified, "overlay binary Unicode schema");
    require(parsed.find(L"d:\\FIXTURE\\中文 FILE.TXT") != parsed.end(), "overlay Windows ordinal case comparison");
    require(PlasticOverlay::Parse(Snapshot({}, now), now, parsed, generated) && parsed.empty(), "empty snapshot is valid");
    auto bad = good; bad[0] = 0; require(!PlasticOverlay::Parse(bad, now, parsed, generated), "bad overlay magic rejected");
    bad = good; bad[8] = 2; require(!PlasticOverlay::Parse(bad, now, parsed, generated), "unknown overlay version rejected");
    bad = good; bad.pop_back(); require(!PlasticOverlay::Parse(bad, now, parsed, generated), "truncated snapshot rejected");
    bad = good; bad.push_back(0); require(!PlasticOverlay::Parse(bad, now, parsed, generated), "trailing snapshot bytes rejected");
    bad = good; const uint32_t excessive = 200001; memcpy(bad.data() + 12, &excessive, 4);
    require(!PlasticOverlay::Parse(bad, now, parsed, generated), "excessive entry count rejected");
    bad = good; const uint32_t longPath = 32768; memcpy(bad.data() + 28, &longPath, 4);
    require(!PlasticOverlay::Parse(bad, now, parsed, generated), "excessive path length rejected");
    bad.assign(PlasticOverlay::MaxBytes + 1, 0);
    require(!PlasticOverlay::Parse(bad, now, parsed, generated), "oversized snapshot rejected before parsing");
    require(PlasticOverlay::Parse(Snapshot({{path + L"-added", 4}, {path + L"-deleted", 5}, {path + L"-ignored", 6},
        {path + L"-locked", 7}, {path + L"-unversioned", 8}}, now), now, parsed, generated) && parsed.size() == 5,
        "extended overlay states accepted");
    require(!PlasticOverlay::Parse(Snapshot({{path, 9}}, now), now, parsed, generated), "unknown overlay state rejected");
    require(!PlasticOverlay::Parse(Snapshot({{path, 1}, {L"d:\\FIXTURE\\中文 FILE.TXT", 3}}, now), now, parsed, generated), "duplicate case-insensitive path rejected");
    require(!PlasticOverlay::Parse(Snapshot({{path, 1}}, now - 121 * PlasticOverlay::Second), now, parsed, generated), "expired snapshot rejected");
    require(!PlasticOverlay::Parse(Snapshot({{path, 1}}, now + 6 * PlasticOverlay::Second), now, parsed, generated), "future timestamp rejected");
    for (const auto& invalid : std::vector<std::wstring>{L"relative.txt", L"D:\\fixture\\..\\file", L"D:\\fixture\\.plastic\\plastic.workspace", L"D:\\fixture\\bad.", L"\\\\?\\C:\\file", std::wstring(L"D:\\bad\0name", 11), L"D:\\bad\xd800"})
        require(!PlasticOverlay::Parse(Snapshot({{invalid, 1}}, now), now, parsed, generated), "noncanonical overlay path rejected");
    const auto cacheFile = directory / L"overlay.bin";
    const auto write = [&](const std::vector<unsigned char>& value) { std::ofstream stream(cacheFile, std::ios::binary | std::ios::trunc); stream.write(reinterpret_cast<const char*>(value.data()), static_cast<std::streamsize>(value.size())); };
    write(good);
    PlasticOverlay::Cache cache(cacheFile.native());
    require(cache.Lookup(path, now, 0) == PlasticOverlay::Modified, "cache reads explicit modified path");
    require(cache.Lookup(L"D:\\fixture\\absent.txt", now, 0) == PlasticOverlay::None, "absent entry never inferred normal");
    write(Snapshot({{path, 3}}, now));
    require(cache.Lookup(path, now, 999) == PlasticOverlay::Modified, "one second poll throttle retains prior snapshot");
    require(cache.Lookup(path, now, 1000) == PlasticOverlay::Conflict, "next poll observes replacement snapshot");
    require(cache.Lookup(path, now + 121 * PlasticOverlay::Second, 1001) == PlasticOverlay::None, "expiry enforced even between polls");
    write(std::vector<unsigned char>{1,2,3});
    require(cache.Lookup(path, now, 2000) == PlasticOverlay::None, "malformed refresh drops previous snapshot");
    std::filesystem::remove(cacheFile);
    require(cache.Lookup(path, now, 3000) == PlasticOverlay::None, "missing snapshot returns no overlay");
    write(good);
    require(cache.Lookup(path, now, 4000) == PlasticOverlay::Modified, "cache recovers after valid snapshot returns");
    const auto begin = std::chrono::steady_clock::now();
    bool lookupsMatch = true;
    for (unsigned i = 0; i < 10000; ++i) lookupsMatch &= cache.Lookup(path, now, 4001) == PlasticOverlay::Modified;
    const auto duration = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - begin).count();
    require(lookupsMatch && duration < 2000, "10000 cached lookups complete within two seconds");
    std::cout << "Overlay 10000 cached lookups: " << duration << " ms\n";
    for (size_t i = 0; i < ARRAYSIZE(OverlayClsids); ++i)
    {
        IClassFactory* factory = nullptr; IShellIconOverlayIdentifier* overlay = nullptr;
        require(SUCCEEDED(DllGetClassObject(OverlayClsids[i], IID_IClassFactory, reinterpret_cast<void**>(&factory))), "overlay factory available");
        require(SUCCEEDED(factory->CreateInstance(nullptr, IID_IShellIconOverlayIdentifier, reinterpret_cast<void**>(&overlay))), "factory creates overlay interface");
        require(overlay->IsMemberOf(nullptr, 0) == E_INVALIDARG && overlay->GetPriority(nullptr) == E_POINTER, "overlay validates COM pointers");
        const int expectedPriority[] = {4, 1, 0, 1, 1, 3, 2, 3};
        int priority = -1; require(SUCCEEDED(overlay->GetPriority(&priority)) && priority == expectedPriority[i], "overlay priority ordering");
        wchar_t file[32768]{}; int index = -1; DWORD flags = 0;
        require(SUCCEEDED(overlay->GetOverlayInfo(file, ARRAYSIZE(file), &index, &flags)) && index == static_cast<int>(i + 1) && flags == (ISIOI_ICONFILE | ISIOI_ICONINDEX), "overlay reports stable resource index");
        require(overlay->GetOverlayInfo(file, 1, &index, &flags) == HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER), "short icon buffer rejected");
        require(overlay->IsMemberOf(L"relative-path", 0) == S_FALSE, "invalid query requests no overlay");
        void* unsupported = nullptr;
        require(overlay->QueryInterface(IID_IContextMenu, &unsupported) == E_NOINTERFACE && !unsupported, "overlay exposes no context menu interface");
        overlay->Release(); factory->Release();
    }
    require(DllCanUnloadNow() == S_OK, "overlay objects release module references");
}

void RegisteredOverlayProbe(const wchar_t* path, int expected)
{
    for (size_t i = 0; i < ARRAYSIZE(OverlayClsids); ++i)
    {
        IShellIconOverlayIdentifier* overlay = nullptr;
        require(SUCCEEDED(CoCreateInstance(OverlayClsids[i], nullptr, CLSCTX_INPROC_SERVER, IID_IShellIconOverlayIdentifier,
            reinterpret_cast<void**>(&overlay))), "registered overlay activation");
        require(overlay->IsMemberOf(path, 0) == (expected == static_cast<int>(i + 1) ? S_OK : S_FALSE), "registered overlay returns expected explicit state");
        wchar_t file[32768]{}; int index = -1; DWORD flags = 0;
        require(SUCCEEDED(overlay->GetOverlayInfo(file, ARRAYSIZE(file), &index, &flags)), "registered overlay icon location");
        HICON icon = nullptr;
        require(ExtractIconExW(file, index, nullptr, &icon, 1) == 1 && icon != nullptr, "registered overlay resource extracts successfully");
        DestroyIcon(icon); overlay->Release();
    }
}

void RegisteredSmoke(const std::filesystem::path& first, const std::filesystem::path& second)
{
    // This branch calls COM activation, never the implementation compiled into this EXE.
    IShellExtInit* initialize = nullptr;
    const HRESULT activation = CoCreateInstance(ShellClsid, nullptr, CLSCTX_INPROC_SERVER,
        IID_IShellExtInit, reinterpret_cast<void**>(&initialize));
    if (FAILED(activation)) std::cerr << "CoCreateInstance HRESULT: 0x" << std::hex << static_cast<unsigned long>(activation) << '\n';
    require(SUCCEEDED(activation), "registered COM activation");
    IContextMenu* context = nullptr;
    require(SUCCEEDED(initialize->QueryInterface(IID_IContextMenu, reinterpret_cast<void**>(&context))), "registered IContextMenu interface");

    // Confirm the interface implementation lives in an actual loaded DLL, not this test EXE.
    const void* implementation = (*reinterpret_cast<void***>(context))[3];
    HMODULE loadedModule = nullptr;
    require(GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(implementation), &loadedModule) && loadedModule != GetModuleHandleW(nullptr), "registered interface comes from DLL");
    wchar_t loadedPath[32768]{};
    require(GetModuleFileNameW(loadedModule, loadedPath, ARRAYSIZE(loadedPath)) > 0 &&
        _wcsicmp(std::filesystem::path(loadedPath).filename().c_str(), L"TortoiseSCMShell.dll") == 0, "registered TortoiseSCMShell.dll loaded");
    std::wcout << L"Loaded: " << loadedPath << L'\n';

    Selection file({(first / L"child/file.txt").native()});
    require(SUCCEEDED(initialize->Initialize(nullptr, &file, nullptr)), "registered file initialization");
    HMENU menu = CreatePopupMenu();
    require(HRESULT_CODE(context->QueryContextMenu(menu, 0, 400, 499, CMF_NORMAL)) == 25, "registered file menu exposes all single-item commands");
    require(GetMenuItemCount(menu) == 1 && GetSubMenu(menu, 0) && GetMenuItemCount(GetSubMenu(menu, 0)) == 25, "registered submenu structure");
    require(GetMenuItemID(GetSubMenu(menu, 0), 0) == 400 && GetMenuItemID(GetSubMenu(menu, 0), 9) == 409, "registered menu command IDs");
    wchar_t verb[80]{};
    require(SUCCEEDED(context->GetCommandString(2, GCS_VERBW, nullptr, reinterpret_cast<char*>(verb), ARRAYSIZE(verb))) &&
        wcscmp(verb, L"tortoisescm.diff") == 0, "registered Unicode canonical verb");
    require(SUCCEEDED(context->GetCommandString(10, GCS_VERBW, nullptr, reinterpret_cast<char*>(verb), ARRAYSIZE(verb))) &&
        wcscmp(verb, L"tortoisescm.branches") == 0, "registered branches canonical verb");
    require(SUCCEEDED(context->GetCommandString(12, GCS_VERBW, nullptr, reinterpret_cast<char*>(verb), ARRAYSIZE(verb))) &&
        wcscmp(verb, L"tortoisescm.shelves") == 0, "registered shelves canonical verb");
    require(SUCCEEDED(context->GetCommandString(16, GCS_VERBW, nullptr, reinterpret_cast<char*>(verb), ARRAYSIZE(verb))) &&
        wcscmp(verb, L"tortoisescm.blame") == 0, "registered blame canonical verb");
    require(SUCCEEDED(context->GetCommandString(14, GCS_VERBW, nullptr, reinterpret_cast<char*>(verb), 80)) &&
        wcscmp(verb, L"tortoisescm.repository-browser") == 0, "registered repository browser canonical verb");
    require(SUCCEEDED(context->GetCommandString(13, GCS_VERBW, nullptr, reinterpret_cast<char*>(verb), ARRAYSIZE(verb))) &&
        wcscmp(verb, L"tortoisescm.labels") == 0, "registered labels canonical verb");
    require(SUCCEEDED(context->GetCommandString(15, GCS_VERBW, nullptr, reinterpret_cast<char*>(verb), ARRAYSIZE(verb))) &&
        wcscmp(verb, L"tortoisescm.revision-graph") == 0, "registered revision graph canonical verb");
    require(SUCCEEDED(context->GetCommandString(24, GCS_VERBW, nullptr, reinterpret_cast<char*>(verb), ARRAYSIZE(verb))) &&
        wcscmp(verb, L"tortoisescm.version") == 0, "registered version information canonical verb");
    char ansiVerb[80]{};
    require(SUCCEEDED(context->GetCommandString(3, GCS_VERBA, nullptr, ansiVerb, ARRAYSIZE(ansiVerb))) &&
        strcmp(ansiVerb, "tortoisescm.history") == 0, "registered ANSI canonical verb");
    DestroyMenu(menu);

    Selection directory({first.native()});
    require(SUCCEEDED(initialize->Initialize(nullptr, &directory, nullptr)), "registered directory initialization");
    menu = CreatePopupMenu();
    require(HRESULT_CODE(context->QueryContextMenu(menu, 0, 400, 499, CMF_NORMAL)) == 23, "registered directory menu excludes diff");
    require(SUCCEEDED(context->GetCommandString(2, GCS_VERBW, nullptr, reinterpret_cast<char*>(verb), ARRAYSIZE(verb))) &&
        wcscmp(verb, L"tortoisescm.history") == 0, "registered directory command mapping");
    DestroyMenu(menu);

    PIDLIST_ABSOLUTE pidl = nullptr;
    require(SUCCEEDED(SHParseDisplayName(first.c_str(), nullptr, &pidl, 0, nullptr)), "registered background PIDL");
    const HRESULT background = initialize->Initialize(pidl, nullptr, nullptr);
    CoTaskMemFree(pidl);
    require(SUCCEEDED(background), "registered directory background initialization");
    menu = CreatePopupMenu();
    require(HRESULT_CODE(context->QueryContextMenu(menu, 0, 400, 499, CMF_NORMAL)) == 23, "registered background menu");
    DestroyMenu(menu);

    Selection multiple({(first / L"child/file.txt").native(), (first / L"child/second.txt").native()});
    require(SUCCEEDED(initialize->Initialize(nullptr, &multiple, nullptr)), "registered multi-selection initialization");
    menu = CreatePopupMenu();
    require(HRESULT_CODE(context->QueryContextMenu(menu, 0, 400, 499, CMF_NORMAL)) == 7, "registered multi-selection menu");
    DestroyMenu(menu);

    Selection cross({first.native(), second.native()});
    initialize->Initialize(nullptr, &cross, nullptr); menu = CreatePopupMenu();
    require(HRESULT_CODE(context->QueryContextMenu(menu, 0, 400, 499, CMF_NORMAL)) == 0 && GetMenuItemCount(menu) == 0, "registered cross-workspace menu excluded");
    DestroyMenu(menu);
    Selection metadata({(first / L".plastic/plastic.workspace").native()});
    initialize->Initialize(nullptr, &metadata, nullptr); menu = CreatePopupMenu();
    require(HRESULT_CODE(context->QueryContextMenu(menu, 0, 400, 499, CMF_NORMAL)) == 0 && GetMenuItemCount(menu) == 0, "registered metadata menu excluded");
    DestroyMenu(menu);
    context->Release(); initialize->Release();
}

#include "ModernShellTests.h"

// Exercise Explorer's actual numeric invocation contract against the shipping
// DLL, with a recorder executable instead of the GUI. No registration or SCM
// operations are performed; every selected path belongs to this test fixture.
void ClassicHandoffTest(const std::filesystem::path& first, const wchar_t* binaryDirectory)
{
    const auto stage = first.parent_path() / L"classic handoff";
    std::filesystem::create_directory(stage);
    const auto dll = stage / L"TortoiseSCMShell.dll";
    std::filesystem::copy_file(std::filesystem::path(binaryDirectory) / L"TortoiseSCMShell.dll", dll);
    wchar_t self[32768]{}; GetModuleFileNameW(nullptr, self, ARRAYSIZE(self));
    const auto executable = stage / L"TortoiseSCM.exe";
    std::filesystem::copy_file(self, executable);
    HMODULE loaded = LoadLibraryExW(dll.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    require(loaded != nullptr, "classic handoff production DLL staged");
    auto getClass = reinterpret_cast<HRESULT(WINAPI*)(REFCLSID, REFIID, void**)>(GetProcAddress(loaded, "DllGetClassObject"));
    {
        ComPtr<IClassFactory> factory;
        require(getClass && SUCCEEDED(getClass(ShellClsid, IID_PPV_ARGS(&factory))), "classic handoff production factory");
        ComPtr<IContextMenu> context;
        require(SUCCEEDED(factory->CreateInstance(nullptr, IID_PPV_ARGS(&context))), "classic handoff production context menu");
        ComPtr<IShellExtInit> initialize;
        require(SUCCEEDED(context.As(&initialize)), "classic handoff shell initialization interface");
        const auto file = (first / L"child/selected \u4e2d\u6587 & item.txt").make_preferred();
        std::ofstream(file) << "fixture";
        for (unsigned selectionKind = 0; selectionKind != 6; ++selectionKind)
        {
            const auto selectedPath = selectionKind == 0 ? file : selectionKind < 3 ? first : selectionKind < 5 ? first.parent_path() : first.root_path();
            Selection selection({selectedPath.native()});
            PIDLIST_ABSOLUTE folder = nullptr;
            if (selectionKind == 2 || selectionKind == 4)
                require(SUCCEEDED(SHParseDisplayName(selectedPath.c_str(), nullptr, &folder, 0, nullptr)), "classic handoff background folder PIDL");
            const HRESULT initialized = initialize->Initialize(folder, (selectionKind == 2 || selectionKind == 4) ? nullptr : &selection, nullptr);
            CoTaskMemFree(folder);
            require(SUCCEEDED(initialized), "classic handoff file directory or background initialized");
            HMENU menu = CreatePopupMenu();
            const HRESULT queried = context->QueryContextMenu(menu, 0, 400, 499, CMF_NORMAL);
            const unsigned expectedCount = selectionKind == 0 ? 25 : selectionKind < 3 ? 23 : 1;
            if (FAILED(queried) || HRESULT_CODE(queried) != expectedCount)
                std::cerr << "Classic menu selection=" << selectionKind << " expected=" << expectedCount
                    << " actual=" << HRESULT_CODE(queried) << " HRESULT=" << queried << '\n';
            require(SUCCEEDED(queried) && HRESULT_CODE(queried) == expectedCount, "classic handoff filtered menu populated");
            if (selectionKind == 0) CheckMenuBitmap(menu, loaded);
            CheckClassicOrder(context.Get(), GetSubMenu(menu, 0), selectionKind);
            for (const wchar_t* command : (selectionKind < 3 ? std::vector<const wchar_t*>{L"update", L"checkin", L"history", L"version", L"add", L"remove"} : std::vector<const wchar_t*>{L"create-workspace"}))
            {
                const std::wstring canonical = L"tortoisescm." + std::wstring(command);
                UINT offset = 0;
                for (; offset < HRESULT_CODE(queried); ++offset)
                {
                    wchar_t verb[80]{};
                    require(SUCCEEDED(context->GetCommandString(offset, GCS_VERBW, nullptr, reinterpret_cast<char*>(verb), ARRAYSIZE(verb))), "classic handoff canonical menu verb");
                    if (canonical == verb) break;
                }
                require(offset < HRESULT_CODE(queried), "classic handoff requested command is visible");
                const auto capturePath = stage / (std::to_wstring(selectionKind) + L"-" + command + L".txt");
                wchar_t oldCapture[32768]{};
                const DWORD oldLength = GetEnvironmentVariableW(L"TORTOISESCM_SHELL_TEST_CAPTURE", oldCapture, ARRAYSIZE(oldCapture));
                require(SetEnvironmentVariableW(L"TORTOISESCM_SHELL_TEST_CAPTURE", capturePath.c_str()) != FALSE, "classic handoff capture environment");
                CMINVOKECOMMANDINFOEX invocation{};
                invocation.cbSize = sizeof(invocation);
                invocation.fMask = CMIC_MASK_UNICODE;
                invocation.lpVerb = MAKEINTRESOURCEA(offset);
                invocation.lpVerbW = nullptr;
                invocation.nShow = SW_SHOWNORMAL;
                const HRESULT invoked = context->InvokeCommand(reinterpret_cast<CMINVOKECOMMANDINFO*>(&invocation));
                SetEnvironmentVariableW(L"TORTOISESCM_SHELL_TEST_CAPTURE", oldLength && oldLength < ARRAYSIZE(oldCapture) ? oldCapture : nullptr);
                require(SUCCEEDED(invoked), "classic handoff invokes production process launcher");
                const std::wstring wideExpected = std::wstring(command) + L"\n" + selectedPath.native() + L"\n";
                const int length = WideCharToMultiByte(CP_UTF8, 0, wideExpected.c_str(), static_cast<int>(wideExpected.size()), nullptr, 0, nullptr, nullptr);
                std::string expected(length, '\0');
                WideCharToMultiByte(CP_UTF8, 0, wideExpected.c_str(), static_cast<int>(wideExpected.size()), expected.data(), length, nullptr, nullptr);
                std::string contents;
                const auto deadline = GetTickCount64() + 10000;
                do
                {
                    std::ifstream capture(capturePath, std::ios::binary);
                    contents.assign(std::istreambuf_iterator<char>(capture), std::istreambuf_iterator<char>());
                    if (contents == expected) break;
                    Sleep(20);
                } while (GetTickCount64() < deadline);
                const std::string label = "classic production dispatch " + Ascii(command) + " selection=" + std::to_string(selectionKind);
                if (contents != expected) std::cerr << "Expected: " << expected << "Actual: " << contents;
                require(contents == expected, label.c_str());
            }
            DestroyMenu(menu);
        }
    }
    FreeLibrary(loaded);
    const auto deadline = GetTickCount64() + 10000;
    while (!DeleteFileW(executable.c_str()) && GetTickCount64() < deadline) Sleep(20);
    require(GetFileAttributesW(executable.c_str()) == INVALID_FILE_ATTRIBUTES, "classic handoff recorder exits");
}

int wmain(int argc, wchar_t** argv) {
    const int recorder = ModernHandoffRecorder(argc, argv);
    if (recorder >= 0) return recorder;
    if (argc == 4 && wcscmp(argv[1], L"--overlay-probe") == 0)
    {
        const int expected = _wtoi(argv[3]);
        if (expected < 0 || expected > 8) return 2;
        CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
        RegisteredOverlayProbe(argv[2], expected); CoUninitialize(); return 0;
    }
    const bool registered = argc == 2 && wcscmp(argv[1], L"--registered") == 0;
    const bool modernRegistered = argc == 3 && wcscmp(argv[1], L"--modern-registered") == 0;
    const bool modernDll = argc == 3 && wcscmp(argv[1], L"--modern-dll") == 0;
    const bool classicDll = argc == 3 && wcscmp(argv[1], L"--classic-dll") == 0;
    if (argc > 1 && !registered && !modernRegistered && !modernDll && !classicDll) { std::cerr << "Usage: ShellTests.exe [--registered | --modern-registered <binary-directory> | --modern-dll <binary-directory> | --classic-dll <binary-directory> | --overlay-probe <path> <state 0..8>]\n"; return 2; }
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    auto base = std::filesystem::temp_directory_path() / (L"TortoiseSCMShellTest-" + std::to_wstring(GetCurrentProcessId()));
    std::filesystem::create_directories(base / L"first/.plastic");
    std::filesystem::create_directories(base / L"second/.plastic");
    std::filesystem::create_directories(base / L"first/child");
    std::ofstream(base / L"first/.plastic/plastic.workspace") << "test";
    std::ofstream(base / L"second/.plastic/plastic.workspace") << "test";
    std::ofstream(base / L"first/child/file.txt") << "test";
    std::ofstream(base / L"first/child/second.txt") << "test";
    auto first = base / L"first", second = base / L"second";
    if (modernRegistered || modernDll)
    {
        ModernShellTests(first, second, argv[2], modernDll);
        if (modernDll) { ModernHandoffTest(first, argv[2]); ClassicHandoffTest(first, argv[2]); }
    }
    else if (classicDll)
        ClassicHandoffTest(first, argv[2]);
    else if (registered)
        RegisteredSmoke(first, second);
    else
    {
    OverlayTests(base);
    ModernShellTests(first, second);
    require(DllCanUnloadNow() == S_OK, "modern commands enumerators and sites release module references");
    require(WorkspaceRoot((first / L"child/file.txt").native()) == first.native(), "nested file workspace");
    require(WorkspaceRoot((first / L".plastic/plastic.workspace").native()).empty(), "metadata excluded");
    require(WorkspaceRoot(base.native()).empty(), "outside workspace excluded");
    require(WorkspaceRoot(L"relative/path").empty(), "relative path excluded");
    require(Quote(L"D:\\folder with space\\") == L"\"D:\\folder with space\\\\\"", "trailing slash quoting");
    require(Quote(L"a\"b") == L"\"a\\\"b\"", "embedded quote escaping");
    const std::vector<size_t> visible{0,1,2,3,4,5,6,7,8,9};
    CMINVOKECOMMANDINFOEX invocation{};
    invocation.cbSize = sizeof(invocation); invocation.fMask = CMIC_MASK_UNICODE;
    invocation.lpVerb = nullptr; invocation.lpVerbW = L"tortoisescm.diff";
    require(ResolveCommand(reinterpret_cast<CMINVOKECOMMANDINFO*>(&invocation),visible)==6,"Unicode verb with null ANSI field");
    invocation.lpVerb = MAKEINTRESOURCEA(2); invocation.lpVerbW = nullptr;
    require(ResolveCommand(reinterpret_cast<CMINVOKECOMMANDINFO*>(&invocation),visible)==2,"Explorer Unicode invocation reads ordinal from ANSI field");
    invocation.lpVerb = MAKEINTRESOURCEA(4); invocation.lpVerbW = MAKEINTRESOURCEW(1);
    require(ResolveCommand(reinterpret_cast<CMINVOKECOMMANDINFO*>(&invocation),visible)==4,"Unicode numeric wide field does not override ANSI ordinal");
    invocation.lpVerb = MAKEINTRESOURCEA(2); invocation.lpVerbW = L"tortoisescm.history";
    require(ResolveCommand(reinterpret_cast<CMINVOKECOMMANDINFO*>(&invocation),visible)==7,"Unicode verb overrides ANSI ordinal");
    invocation.lpVerbW = L"not-a-command";
    require(ResolveCommand(reinterpret_cast<CMINVOKECOMMANDINFO*>(&invocation),visible)==ARRAYSIZE(commands),"unknown Unicode verb rejected");
    invocation.lpVerb = MAKEINTRESOURCEA(99); invocation.lpVerbW = nullptr;
    require(ResolveCommand(reinterpret_cast<CMINVOKECOMMANDINFO*>(&invocation),visible)==ARRAYSIZE(commands),"invalid ANSI ordinal rejected with Unicode flag");
    invocation.lpVerb = "tortoisescm.undo";
    require(ResolveCommand(reinterpret_cast<CMINVOKECOMMANDINFO*>(&invocation),visible)==5,"Unicode flag without wide verb permits ANSI canonical verb");
    const std::vector<size_t> filtered{0,1,2,3,4,5,7,25};
    invocation.lpVerb = MAKEINTRESOURCEA(6);
    require(ResolveCommand(reinterpret_cast<CMINVOKECOMMANDINFO*>(&invocation),filtered)==7,"Unicode ordinal maps through filtered directory commands");
    invocation.fMask = 0; invocation.lpVerb = "tortoisescm.undo";
    require(ResolveCommand(reinterpret_cast<CMINVOKECOMMANDINFO*>(&invocation),visible)==5,"ANSI canonical verb");
    invocation.lpVerb = MAKEINTRESOURCEA(3);
    require(ResolveCommand(reinterpret_cast<CMINVOKECOMMANDINFO*>(&invocation),visible)==3,"ANSI ordinal ignores Unicode field");
    auto shell = new PlasticShell;
    Selection one({(first / L"child/file.txt").native()});
    require(SUCCEEDED(shell->Initialize(nullptr, &one, nullptr)), "single file init");
    auto menu = CreatePopupMenu();
    require(HRESULT_CODE(shell->QueryContextMenu(menu, 0, 100, 200, CMF_NORMAL)) == 25, "single file exposes all commands");
    CheckClassicOrder(shell, GetSubMenu(menu, 0), 0);
    wchar_t verb[80]{};
    require(SUCCEEDED(shell->GetCommandString(2, GCS_VERBW, nullptr, reinterpret_cast<char*>(verb), 80)) && wcscmp(verb,L"tortoisescm.diff") == 0, "diff canonical verb");
    require(SUCCEEDED(shell->GetCommandString(7, GCS_VERBW, nullptr, reinterpret_cast<char*>(verb), 80)) && wcscmp(verb,L"tortoisescm.move") == 0, "move canonical verb");
    require(SUCCEEDED(shell->GetCommandString(18, GCS_VERBW, nullptr, reinterpret_cast<char*>(verb), 80)) && wcscmp(verb,L"tortoisescm.recover") == 0, "recover canonical verb");
    require(SUCCEEDED(shell->GetCommandString(10, GCS_VERBW, nullptr, reinterpret_cast<char*>(verb), 80)) && wcscmp(verb,L"tortoisescm.branches") == 0, "branches canonical verb");
    require(SUCCEEDED(shell->GetCommandString(12, GCS_VERBW, nullptr, reinterpret_cast<char*>(verb), 80)) && wcscmp(verb,L"tortoisescm.shelves") == 0, "shelves canonical verb");
    require(SUCCEEDED(shell->GetCommandString(16, GCS_VERBW, nullptr, reinterpret_cast<char*>(verb), 80)) && wcscmp(verb,L"tortoisescm.blame") == 0, "blame canonical verb");
    require(SUCCEEDED(shell->GetCommandString(14, GCS_VERBW, nullptr, reinterpret_cast<char*>(verb), 80)) && wcscmp(verb,L"tortoisescm.repository-browser") == 0, "repository browser canonical verb");
    require(SUCCEEDED(shell->GetCommandString(13, GCS_VERBW, nullptr, reinterpret_cast<char*>(verb), 80)) && wcscmp(verb,L"tortoisescm.labels") == 0, "labels canonical verb");
    require(SUCCEEDED(shell->GetCommandString(15, GCS_VERBW, nullptr, reinterpret_cast<char*>(verb), 80)) && wcscmp(verb,L"tortoisescm.revision-graph") == 0, "revision graph canonical verb");
    require(SUCCEEDED(shell->GetCommandString(24, GCS_VERBW, nullptr, reinterpret_cast<char*>(verb), 80)) && wcscmp(verb,L"tortoisescm.version") == 0, "version information canonical verb");
    DestroyMenu(menu);
    Selection multi({(first / L"child/file.txt").native(), (first / L"child/second.txt").native()});
    shell->Initialize(nullptr, &multi, nullptr); menu = CreatePopupMenu();
    require(HRESULT_CODE(shell->QueryContextMenu(menu,0,100,200,CMF_NORMAL)) == 7, "multi selection excludes diff and history");
    const wchar_t* multiVerbs[] = {L"update", L"checkin", L"add", L"checkout", L"undo", L"gluon", L"settings"};
    for (UINT offset = 0; offset < ARRAYSIZE(multiVerbs); ++offset)
        require(SUCCEEDED(shell->GetCommandString(offset, GCS_VERBW, nullptr, reinterpret_cast<char*>(verb), ARRAYSIZE(verb))) &&
            verb == L"tortoisescm." + std::wstring(multiVerbs[offset]), "multi selection retains frequent-first filtered order");
    DestroyMenu(menu);
    Selection cross({first.native(),second.native()}); shell->Initialize(nullptr,&cross,nullptr); menu=CreatePopupMenu();
    require(HRESULT_CODE(shell->QueryContextMenu(menu,0,100,200,CMF_NORMAL))==0, "cross workspace excluded"); DestroyMenu(menu);
    PIDLIST_ABSOLUTE pidl{}; SHParseDisplayName(first.c_str(),nullptr,&pidl,0,nullptr);
    shell->Initialize(pidl,nullptr,nullptr); CoTaskMemFree(pidl); menu=CreatePopupMenu();
    const auto directoryCount = HRESULT_CODE(shell->QueryContextMenu(menu,0,100,200,CMF_NORMAL));
    require(directoryCount==23, "directory background menu"); CheckClassicOrder(shell, GetSubMenu(menu, 0), 2); DestroyMenu(menu);
    menu=CreatePopupMenu(); require(HRESULT_CODE(shell->QueryContextMenu(menu,0,100,200,CMF_DEFAULTONLY))==0, "default-only query ignored"); DestroyMenu(menu);
    menu=CreatePopupMenu(); require(HRESULT_CODE(shell->QueryContextMenu(menu,0,100,102,CMF_NORMAL))==3, "command id limit respected"); DestroyMenu(menu);
    for (const auto& parent : {base, base.root_path()})
    {
        Selection ordinary({parent.native()}); shell->Initialize(nullptr, &ordinary, nullptr); menu = CreatePopupMenu();
        require(HRESULT_CODE(shell->QueryContextMenu(menu,0,100,200,CMF_NORMAL)) == 1, "ordinary directory or drive exposes checkout only");
        require(SUCCEEDED(shell->GetCommandString(0,GCS_VERBW,nullptr,reinterpret_cast<char*>(verb),80)) && wcscmp(verb,L"tortoisescm.create-workspace") == 0, "checkout uses filtered ordinal zero");
        DestroyMenu(menu);
    }
    std::ofstream(base / L"outside.txt") << "test";
    for (const auto& selected : std::vector<std::vector<std::wstring>>{
        {(base / L"outside.txt").native()}, {base.native(), first.native()}, {(first / L".plastic").native()}})
    {
        Selection invalid(selected); shell->Initialize(nullptr,&invalid,nullptr); menu = CreatePopupMenu();
        require(HRESULT_CODE(shell->QueryContextMenu(menu,0,100,200,CMF_NORMAL)) == 0 && GetMenuItemCount(menu) == 0, "checkout rejects file multiselection or metadata"); DestroyMenu(menu);
    }
    require(!CheckoutParent({L"relative"}) && !CheckoutParent({first.native()}) && !CheckoutParent({(base / L"missing").native()}), "checkout rejects relative nested and missing directories");
    require(DllCanUnloadNow()==S_FALSE,"COM objects keep DLL loaded"); shell->Release();
    require(DllCanUnloadNow()==S_OK,"COM release allows unloading");
    IClassFactory* factory=nullptr; require(SUCCEEDED(DllGetClassObject(ShellClsid,IID_IClassFactory,reinterpret_cast<void**>(&factory))),"class factory export");
    IContextMenu* context=nullptr; require(SUCCEEDED(factory->CreateInstance(nullptr,IID_IContextMenu,reinterpret_cast<void**>(&context))),"factory creates context menu"); context->Release(); factory->Release();
    require(DllCanUnloadNow()==S_OK,"factory release allows unloading");
    }
    if (base.is_absolute() && base.parent_path() == std::filesystem::temp_directory_path() && base.filename().native().rfind(L"TortoiseSCMShellTest-",0)==0)
        std::filesystem::remove_all(base);
    CoUninitialize(); return 0;
}
