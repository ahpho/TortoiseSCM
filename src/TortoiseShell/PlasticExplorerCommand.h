// SPDX-License-Identifier: GPL-2.0-or-later
// Included inside PlasticShell.cpp's private namespace: both shell interfaces
// deliberately share command ordering, visibility, workspace rules and Launch.
using Microsoft::WRL::ComPtr;

HRESULT CopyCommandText(const wchar_t* text, LPWSTR* output)
{
    if (!output) return E_POINTER;
    *output = nullptr;
    const size_t bytes = (wcslen(text) + 1) * sizeof(wchar_t);
    auto copy = static_cast<LPWSTR>(CoTaskMemAlloc(bytes));
    if (!copy) return E_OUTOFMEMORY;
    memcpy(copy, text, bytes); *output = copy; return S_OK;
}

HRESULT BackgroundItem(IUnknown* site, IShellItem** output)
{
    *output = nullptr;
    if (!site) return E_FAIL;
    ComPtr<IServiceProvider> services;
    HRESULT result = site->QueryInterface(IID_PPV_ARGS(&services));
    if (FAILED(result)) return result;
    ComPtr<IFolderView> view;
    result = services->QueryService(SID_SFolderView, IID_PPV_ARGS(&view));
    if (FAILED(result))
    {
        // Explorer also exposes the active view through its browser service.
        ComPtr<IShellBrowser> browser;
        result = services->QueryService(SID_SShellBrowser, IID_PPV_ARGS(&browser));
        if (FAILED(result)) return result;
        ComPtr<IShellView> shellView;
        result = browser->QueryActiveShellView(&shellView);
        if (FAILED(result)) return result;
        result = shellView.As(&view);
        if (FAILED(result)) return result;
    }
    result = view->GetFolder(IID_IShellItem, reinterpret_cast<void**>(output));
    if (SUCCEEDED(result)) return result;
    // Shell folder implementations need not expose IShellItem directly.
    ComPtr<IPersistFolder2> folder;
    result = view->GetFolder(IID_PPV_ARGS(&folder));
    if (FAILED(result)) return result;
    PIDLIST_ABSOLUTE pidl = nullptr;
    result = folder->GetCurFolder(&pidl);
    if (SUCCEEDED(result)) result = SHCreateItemFromIDList(pidl, IID_IShellItem, reinterpret_cast<void**>(output));
    CoTaskMemFree(pidl);
    return result;
}

HRESULT ExplorerPaths(IShellItemArray* items, IUnknown* site, std::vector<std::wstring>& paths)
{
    paths.clear();
    if (!items && site)
    {
        ComPtr<IOleWindow> window;
        if (SUCCEEDED(site->QueryInterface(IID_PPV_ARGS(&window))))
        {
            HWND handle = nullptr;
            wchar_t className[128]{};
            if (SUCCEEDED(window->GetWindow(&handle)) && handle &&
                GetClassNameW(handle, className, ARRAYSIZE(className)) &&
                wcscmp(className, L"NamespaceTreeControl") == 0)
                return E_INVALIDARG; // The active folder can differ from the clicked tree node.
        }
    }
    DWORD count = 0;
    if (items)
    {
        const HRESULT result = items->GetCount(&count);
        if (FAILED(result)) return result;
        // An explicit empty/invalid selection must not fall back to the site.
        if (!count || count > 10000) return E_INVALIDARG;
    }
    else count = 1;
    std::vector<std::wstring> resolved;
    for (DWORD index = 0; index < count; ++index)
    {
        ComPtr<IShellItem> item;
        HRESULT result = items ? items->GetItemAt(index, &item) : BackgroundItem(site, &item);
        if (FAILED(result) || !item) return FAILED(result) ? result : E_FAIL;
        PWSTR path = nullptr;
        result = item->GetDisplayName(SIGDN_FILESYSPATH, &path);
        if (FAILED(result)) { CoTaskMemFree(path); return result; }
        // Free Shell-owned memory even if the vector allocation throws.
        struct TextGuard { PWSTR value; ~TextGuard() { CoTaskMemFree(value); } } guard{path};
        if (!path || !*path) return E_INVALIDARG;
        resolved.emplace_back(path);
    }
    const auto root = WorkspaceRoot(resolved.front());
    if (root.empty())
    {
        if (!CheckoutParent(resolved)) return E_INVALIDARG;
        paths = std::move(resolved);
        return S_OK;
    }
    for (const auto& path : resolved)
        if (_wcsicmp(root.c_str(), WorkspaceRoot(path).c_str()) != 0) return E_INVALIDARG;
    paths = std::move(resolved);
    return S_OK;
}

class PlasticExplorerCommand final : public IExplorerCommand, public IObjectWithSite
{
    std::atomic<ULONG> references{1};
    const size_t commandIndex;
    ComPtr<IUnknown> site;
public:
    explicit PlasticExplorerCommand(size_t index = ARRAYSIZE(commands), IUnknown* commandSite = nullptr)
        : commandIndex(index), site(commandSite) { ++moduleReferences; }
    ~PlasticExplorerCommand() { --moduleReferences; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** output) override
    {
        if (!output) return E_POINTER;
        *output = nullptr;
        if (iid == IID_IUnknown || iid == IID_IExplorerCommand) *output = static_cast<IExplorerCommand*>(this);
        else if (iid == IID_IObjectWithSite) *output = static_cast<IObjectWithSite*>(this);
        else return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++references; }
    ULONG STDMETHODCALLTYPE Release() override { const ULONG count = --references; if (!count) delete this; return count; }
    HRESULT STDMETHODCALLTYPE GetTitle(IShellItemArray*, LPWSTR* output) override
    { return CopyCommandText(commandIndex == ARRAYSIZE(commands) ? L"TortoiseSCM" : Label(commands[commandIndex]), output); }
    HRESULT STDMETHODCALLTYPE GetIcon(IShellItemArray*, LPWSTR* output) override
    {
        if (!output) return E_POINTER;
        *output = nullptr;
        try
        {
            wchar_t path[32768]{};
            const DWORD count = GetModuleFileNameW(moduleInstance, path, ARRAYSIZE(path));
            if (!count || count >= ARRAYSIZE(path)) return E_FAIL;
            return CopyCommandText((std::wstring(path) + L",-1").c_str(), output);
        }
        catch (...) { return E_OUTOFMEMORY; }
    }
    HRESULT STDMETHODCALLTYPE GetToolTip(IShellItemArray*, LPWSTR* output) override
    { if (!output) return E_POINTER; *output = nullptr; return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetCanonicalName(GUID* output) override
    {
        if (!output) return E_POINTER;
        *output = ExplorerCommandClsid;
        // Appended command ordinals never change, and use a separate GUID family.
        if (commandIndex != ARRAYSIZE(commands)) output->Data1 = 0xb1da4600 + static_cast<unsigned long>(commandIndex);
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetState(IShellItemArray* items, BOOL slow, EXPCMDSTATE* output) override
    {
        if (!output) return E_POINTER;
        *output = ECS_HIDDEN;
        // As in upstream ContextMenu.cpp: classic Explorer supplies IOleWindow,
        // while the Windows 11 menu does not (except its navigation tree).
        // Keep the existing IContextMenu as the sole classic-menu entry.
        ComPtr<IOleWindow> window;
        if (site && SUCCEEDED(site.As(&window)))
        {
            HWND handle = nullptr;
            wchar_t className[128]{};
            if (SUCCEEDED(window->GetWindow(&handle)) && handle &&
                GetClassNameW(handle, className, ARRAYSIZE(className)) &&
                wcscmp(className, L"NamespaceTreeControl") != 0) return S_OK;
        }
        // Let Explorer move filesystem marker probing off its fast UI callback.
        if (!slow) return E_PENDING;
        try
        {
            std::vector<std::wstring> paths;
            if (SUCCEEDED(ExplorerPaths(items, site.Get(), paths)) &&
                (commandIndex == ARRAYSIZE(commands) || CommandVisible(commandIndex, paths))) *output = ECS_ENABLED;
            return S_OK;
        }
        catch (...) { return E_FAIL; }
    }
    HRESULT STDMETHODCALLTYPE Invoke(IShellItemArray* items, IBindCtx*) override
    {
        if (commandIndex == ARRAYSIZE(commands)) return E_NOTIMPL;
        try
        {
            std::vector<std::wstring> paths;
            const HRESULT result = ExplorerPaths(items, site.Get(), paths);
            if (FAILED(result)) return result;
            if (!CommandVisible(commandIndex, paths)) return E_INVALIDARG;
            HWND parent = nullptr;
            ComPtr<IOleWindow> window;
            if (site && SUCCEEDED(site.As(&window))) window->GetWindow(&parent);
            return Launch(commands[commandIndex], paths, parent);
        }
        catch (...) { return E_FAIL; }
    }
    HRESULT STDMETHODCALLTYPE GetFlags(EXPCMDFLAGS* output) override
    { if (!output) return E_POINTER; *output = commandIndex == ARRAYSIZE(commands) ? ECF_HASSUBCOMMANDS : ECF_DEFAULT; return S_OK; }
    HRESULT STDMETHODCALLTYPE EnumSubCommands(IEnumExplorerCommand** output) override;
    HRESULT STDMETHODCALLTYPE SetSite(IUnknown* value) override { site = value; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetSite(REFIID iid, void** output) override
    {
        if (!output) return E_POINTER;
        *output = nullptr;
        return site ? site->QueryInterface(iid, output) : E_FAIL;
    }
};

class PlasticExplorerCommandEnum final : public IEnumExplorerCommand
{
    std::atomic<ULONG> references{1};
    size_t position;
    ComPtr<IUnknown> site;
public:
    explicit PlasticExplorerCommandEnum(IUnknown* commandSite, size_t offset = 0) : position(offset), site(commandSite) { ++moduleReferences; }
    ~PlasticExplorerCommandEnum() { --moduleReferences; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** output) override
    {
        if (!output) return E_POINTER;
        *output = nullptr;
        if (iid != IID_IUnknown && iid != IID_IEnumExplorerCommand) return E_NOINTERFACE;
        *output = static_cast<IEnumExplorerCommand*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++references; }
    ULONG STDMETHODCALLTYPE Release() override { const ULONG count = --references; if (!count) delete this; return count; }
    HRESULT STDMETHODCALLTYPE Next(ULONG count, IExplorerCommand** output, ULONG* fetched) override
    {
        if (fetched) *fetched = 0;
        if (!output || (!fetched && count != 1)) return E_POINTER;
        ULONG copied = 0;
        for (; copied < count; ++copied)
        {
            output[copied] = nullptr;
            if (position == ARRAYSIZE(menuOrder)) break;
            auto command = new (std::nothrow) PlasticExplorerCommand(menuOrder[position], site.Get());
            if (!command)
            {
                // Failure is transactional: no leaked or partially consumed entries.
                for (ULONG index = 0; index < copied; ++index) { output[index]->Release(); output[index] = nullptr; }
                position -= copied;
                return E_OUTOFMEMORY;
            }
            output[copied] = command; ++position;
        }
        if (fetched) *fetched = copied;
        return copied == count ? S_OK : S_FALSE;
    }
    HRESULT STDMETHODCALLTYPE Skip(ULONG count) override
    {
        const size_t remaining = ARRAYSIZE(menuOrder) - position;
        const size_t skipped = count < remaining ? count : remaining;
        position += skipped;
        return skipped == count ? S_OK : S_FALSE;
    }
    HRESULT STDMETHODCALLTYPE Reset() override { position = 0; return S_OK; }
    HRESULT STDMETHODCALLTYPE Clone(IEnumExplorerCommand** output) override
    {
        if (!output) return E_POINTER;
        *output = new (std::nothrow) PlasticExplorerCommandEnum(site.Get(), position);
        return *output ? S_OK : E_OUTOFMEMORY;
    }
};

HRESULT PlasticExplorerCommand::EnumSubCommands(IEnumExplorerCommand** output)
{
    if (!output) return E_POINTER;
    *output = nullptr;
    if (commandIndex != ARRAYSIZE(commands)) return E_NOTIMPL;
    *output = new (std::nothrow) PlasticExplorerCommandEnum(site.Get());
    return *output ? S_OK : E_OUTOFMEMORY;
}
