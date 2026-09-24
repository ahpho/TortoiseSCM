// SPDX-License-Identifier: GPL-2.0-or-later
#include "../../src/TortoiseShell/PlasticShell.cpp"
#include <iostream>
#include <fstream>
#include <cassert>

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
    require(HRESULT_CODE(context->QueryContextMenu(menu, 0, 400, 499, CMF_NORMAL)) == 10, "registered file menu has ten commands");
    require(GetMenuItemCount(menu) == 1 && GetSubMenu(menu, 0) && GetMenuItemCount(GetSubMenu(menu, 0)) == 10, "registered submenu structure");
    require(GetMenuItemID(GetSubMenu(menu, 0), 0) == 400 && GetMenuItemID(GetSubMenu(menu, 0), 9) == 409, "registered menu command IDs");
    wchar_t verb[80]{};
    require(SUCCEEDED(context->GetCommandString(6, GCS_VERBW, nullptr, reinterpret_cast<char*>(verb), ARRAYSIZE(verb))) &&
        wcscmp(verb, L"tortoisescm.diff") == 0, "registered Unicode canonical verb");
    char ansiVerb[80]{};
    require(SUCCEEDED(context->GetCommandString(7, GCS_VERBA, nullptr, ansiVerb, ARRAYSIZE(ansiVerb))) &&
        strcmp(ansiVerb, "tortoisescm.history") == 0, "registered ANSI canonical verb");
    DestroyMenu(menu);

    Selection directory({first.native()});
    require(SUCCEEDED(initialize->Initialize(nullptr, &directory, nullptr)), "registered directory initialization");
    menu = CreatePopupMenu();
    require(HRESULT_CODE(context->QueryContextMenu(menu, 0, 400, 499, CMF_NORMAL)) == 9, "registered directory menu excludes diff");
    require(SUCCEEDED(context->GetCommandString(6, GCS_VERBW, nullptr, reinterpret_cast<char*>(verb), ARRAYSIZE(verb))) &&
        wcscmp(verb, L"tortoisescm.history") == 0, "registered directory command mapping");
    DestroyMenu(menu);

    PIDLIST_ABSOLUTE pidl = nullptr;
    require(SUCCEEDED(SHParseDisplayName(first.c_str(), nullptr, &pidl, 0, nullptr)), "registered background PIDL");
    const HRESULT background = initialize->Initialize(pidl, nullptr, nullptr);
    CoTaskMemFree(pidl);
    require(SUCCEEDED(background), "registered directory background initialization");
    menu = CreatePopupMenu();
    require(HRESULT_CODE(context->QueryContextMenu(menu, 0, 400, 499, CMF_NORMAL)) == 9, "registered background menu");
    DestroyMenu(menu);

    Selection multiple({(first / L"child/file.txt").native(), (first / L"child/second.txt").native()});
    require(SUCCEEDED(initialize->Initialize(nullptr, &multiple, nullptr)), "registered multi-selection initialization");
    menu = CreatePopupMenu();
    require(HRESULT_CODE(context->QueryContextMenu(menu, 0, 400, 499, CMF_NORMAL)) == 8, "registered multi-selection menu");
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

int wmain(int argc, wchar_t** argv) {
    const bool registered = argc == 2 && wcscmp(argv[1], L"--registered") == 0;
    if (argc > 1 && !registered) { std::cerr << "Usage: ShellTests.exe [--registered]\n"; return 2; }
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
    if (registered)
        RegisteredSmoke(first, second);
    else
    {
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
    invocation.lpVerb = "unrelated"; invocation.lpVerbW = MAKEINTRESOURCEW(4);
    require(ResolveCommand(reinterpret_cast<CMINVOKECOMMANDINFO*>(&invocation),visible)==4,"Unicode ordinal with ANSI string");
    invocation.lpVerb = MAKEINTRESOURCEA(2); invocation.lpVerbW = L"tortoisescm.history";
    require(ResolveCommand(reinterpret_cast<CMINVOKECOMMANDINFO*>(&invocation),visible)==7,"Unicode verb overrides ANSI ordinal");
    invocation.lpVerbW = L"not-a-command";
    require(ResolveCommand(reinterpret_cast<CMINVOKECOMMANDINFO*>(&invocation),visible)==ARRAYSIZE(commands),"unknown Unicode verb rejected");
    invocation.lpVerbW = MAKEINTRESOURCEW(99);
    require(ResolveCommand(reinterpret_cast<CMINVOKECOMMANDINFO*>(&invocation),visible)==ARRAYSIZE(commands),"invalid Unicode ordinal rejected");
    invocation.fMask = 0; invocation.lpVerb = "tortoisescm.undo";
    require(ResolveCommand(reinterpret_cast<CMINVOKECOMMANDINFO*>(&invocation),visible)==5,"ANSI canonical verb");
    invocation.lpVerb = MAKEINTRESOURCEA(3);
    require(ResolveCommand(reinterpret_cast<CMINVOKECOMMANDINFO*>(&invocation),visible)==3,"ANSI ordinal ignores Unicode field");
    auto shell = new PlasticShell;
    Selection one({(first / L"child/file.txt").native()});
    require(SUCCEEDED(shell->Initialize(nullptr, &one, nullptr)), "single file init");
    auto menu = CreatePopupMenu();
    require(HRESULT_CODE(shell->QueryContextMenu(menu, 0, 100, 200, CMF_NORMAL)) == 10, "single file ten commands");
    wchar_t verb[80]{};
    require(SUCCEEDED(shell->GetCommandString(6, GCS_VERBW, nullptr, reinterpret_cast<char*>(verb), 80)) && wcscmp(verb,L"tortoisescm.diff") == 0, "diff canonical verb");
    DestroyMenu(menu);
    Selection multi({(first / L"child/file.txt").native(), (first / L"child/second.txt").native()});
    shell->Initialize(nullptr, &multi, nullptr); menu = CreatePopupMenu();
    require(HRESULT_CODE(shell->QueryContextMenu(menu,0,100,200,CMF_NORMAL)) == 8, "multi selection excludes diff and history"); DestroyMenu(menu);
    Selection cross({first.native(),second.native()}); shell->Initialize(nullptr,&cross,nullptr); menu=CreatePopupMenu();
    require(HRESULT_CODE(shell->QueryContextMenu(menu,0,100,200,CMF_NORMAL))==0, "cross workspace excluded"); DestroyMenu(menu);
    PIDLIST_ABSOLUTE pidl{}; SHParseDisplayName(first.c_str(),nullptr,&pidl,0,nullptr);
    shell->Initialize(pidl,nullptr,nullptr); CoTaskMemFree(pidl); menu=CreatePopupMenu();
    require(HRESULT_CODE(shell->QueryContextMenu(menu,0,100,200,CMF_NORMAL))==9, "directory background menu"); DestroyMenu(menu);
    menu=CreatePopupMenu(); require(HRESULT_CODE(shell->QueryContextMenu(menu,0,100,200,CMF_DEFAULTONLY))==0, "default-only query ignored"); DestroyMenu(menu);
    menu=CreatePopupMenu(); require(HRESULT_CODE(shell->QueryContextMenu(menu,0,100,102,CMF_NORMAL))==3, "command id limit respected"); DestroyMenu(menu);
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
