// SPDX-License-Identifier: GPL-2.0-or-later
class BackgroundSite final : public IServiceProvider, public IFolderView, public IOleWindow
{
    ULONG references = 1;
    std::wstring path;
    HWND window;
public:
    explicit BackgroundSite(std::wstring value, HWND handle = nullptr) : path(std::move(value)), window(handle) {}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** output) override
    {
        if (!output) return E_POINTER;
        *output = nullptr;
        if (iid == IID_IUnknown || iid == IID_IServiceProvider) *output = static_cast<IServiceProvider*>(this);
        else if (iid == IID_IFolderView) *output = static_cast<IFolderView*>(this);
        else if (iid == IID_IOleWindow && window) *output = static_cast<IOleWindow*>(this);
        else return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++references; }
    ULONG STDMETHODCALLTYPE Release() override { const ULONG count = --references; if (!count) delete this; return count; }
    HRESULT STDMETHODCALLTYPE QueryService(REFGUID service, REFIID iid, void** output) override
    { if (service != SID_SFolderView) return E_NOINTERFACE; return QueryInterface(iid, output); }
    HRESULT STDMETHODCALLTYPE GetFolder(REFIID iid, void** output) override
    { return SHCreateItemFromParsingName(std::filesystem::path(path).make_preferred().c_str(), nullptr, iid, output); }
    HRESULT STDMETHODCALLTYPE GetWindow(HWND* output) override { if (!output) return E_POINTER; *output = window; return S_OK; }
    HRESULT STDMETHODCALLTYPE ContextSensitiveHelp(BOOL) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetCurrentViewMode(UINT*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE SetCurrentViewMode(UINT) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE Item(int, PITEMID_CHILD*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE ItemCount(UINT, int*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE Items(UINT, REFIID, void**) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetSelectionMarkedItem(int*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetFocusedItem(int*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetItemPosition(PCUITEMID_CHILD, POINT*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetSpacing(POINT*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetDefaultSpacing(POINT*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetAutoArrange() override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE SelectItem(int, DWORD) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE SelectAndPositionItems(UINT, PCUITEMID_CHILD_ARRAY, POINT*, DWORD) override { return E_NOTIMPL; }
};

ComPtr<IShellItemArray> ModernSelection(const std::vector<std::wstring>& paths)
{
    std::vector<PCIDLIST_ABSOLUTE> ids;
    for (const auto& path : paths)
    {
        PIDLIST_ABSOLUTE id = nullptr;
        require(SUCCEEDED(SHParseDisplayName(std::filesystem::path(path).make_preferred().c_str(), nullptr, &id, 0, nullptr)), "modern fixture shell PIDL");
        ids.push_back(id);
    }
    ComPtr<IShellItemArray> items;
    require(SUCCEEDED(SHCreateShellItemArrayFromIDLists(static_cast<UINT>(ids.size()), ids.data(), &items)), "modern fixture shell item array");
    for (auto id : ids) CoTaskMemFree(const_cast<PIDLIST_ABSOLUTE>(id));
    return items;
}

void ModernShellTests(const std::filesystem::path& first, const std::filesystem::path& second, const wchar_t* registeredDirectory = nullptr, bool loadDll = false)
{
    struct ModuleGuard { HMODULE module = nullptr; ~ModuleGuard() { if (module) FreeLibrary(module); } } loaded;
    ComPtr<IExplorerCommand> root;
    if (registeredDirectory)
    {
        HRESULT activated;
        if (loadDll)
        {
            const auto library = std::filesystem::path(registeredDirectory) / L"TortoiseSCMShell.dll";
            loaded.module = LoadLibraryExW(library.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
            require(loaded.module != nullptr, "modern production DLL loaded");
            auto getClass = reinterpret_cast<HRESULT(WINAPI*)(REFCLSID, REFIID, void**)>(GetProcAddress(loaded.module, "DllGetClassObject"));
            require(getClass != nullptr, "modern production DLL class export");
            ComPtr<IClassFactory> factory;
            activated = getClass(ExplorerCommandClsid, IID_PPV_ARGS(&factory));
            if (SUCCEEDED(activated)) activated = factory->CreateInstance(nullptr, IID_PPV_ARGS(&root));
        }
        else activated = CoCreateInstance(ExplorerCommandClsid, nullptr, CLSCTX_LOCAL_SERVER, IID_PPV_ARGS(&root));
        if (FAILED(activated)) std::cerr << "Modern activation HRESULT: 0x" << std::hex << static_cast<unsigned long>(activated) << '\n';
        require(SUCCEEDED(activated), loadDll ? "modern production DLL COM activation" : "modern registered surrogate COM activation");
        LPWSTR icon = nullptr;
        require(SUCCEEDED(root->GetIcon(nullptr, &icon)) && icon, "modern registered icon path");
        const auto expected = (std::filesystem::path(registeredDirectory) / L"TortoiseSCMShell.dll").native() + L",-1";
        require(_wcsicmp(icon, expected.c_str()) == 0, "modern registered exact DLL version path");
        std::wcout << L"Modern registered icon: " << icon << L'\n'; CoTaskMemFree(icon);
    }
    else
    {
        ComPtr<IClassFactory> factory;
        require(SUCCEEDED(DllGetClassObject(ExplorerCommandClsid, IID_PPV_ARGS(&factory))), "modern class factory exported");
        require(factory->CreateInstance(nullptr, IID_IExplorerCommand, nullptr) == E_POINTER, "modern factory rejects null output");
        require(SUCCEEDED(factory->CreateInstance(nullptr, IID_PPV_ARGS(&root))), "modern factory creates command");
    }
    EXPCMDFLAGS flags = ECF_DEFAULT;
    require(SUCCEEDED(root->GetFlags(&flags)) && flags == ECF_HASSUBCOMMANDS, "modern root has subcommands");
    GUID identity{};
    require(SUCCEEDED(root->GetCanonicalName(&identity)) && identity == ExplorerCommandClsid, "modern root canonical GUID");
    LPWSTR title = nullptr;
    require(SUCCEEDED(root->GetTitle(nullptr, &title)) && wcscmp(title, L"TortoiseSCM") == 0, "modern root title"); CoTaskMemFree(title);
    if (!registeredDirectory)
        require(root->GetTitle(nullptr, nullptr) == E_POINTER && root->GetState(nullptr, TRUE, nullptr) == E_POINTER &&
            root->GetFlags(nullptr) == E_POINTER && root->GetCanonicalName(nullptr) == E_POINTER && root->EnumSubCommands(nullptr) == E_POINTER,
            "modern root COM output pointer validation");
    auto file = ModernSelection({(first / L"child/file.txt").native()});
    auto directory = ModernSelection({first.native()});
    auto multiple = ModernSelection({(first / L"child/file.txt").native(), (first / L"child/second.txt").native()});
    auto cross = ModernSelection({first.native(), second.native()});
    auto outside = ModernSelection({first.parent_path().native()});
    auto metadata = ModernSelection({(first / L".plastic/plastic.workspace").native()});
    EXPCMDSTATE state = ECS_ENABLED;
    require(root->GetState(file.Get(), FALSE, &state) == E_PENDING, "modern fast state defers filesystem probing");
    require(SUCCEEDED(root->GetState(file.Get(), TRUE, &state)) && state == ECS_ENABLED, "modern file root enabled");
    require(SUCCEEDED(root->GetState(nullptr, TRUE, &state)) && state == ECS_HIDDEN, "modern null selection never reuses prior paths");
    require(SUCCEEDED(root->GetState(outside.Get(), TRUE, &state)) && state == ECS_ENABLED, "modern checkout root enabled outside workspace");
    for (auto items : {cross.Get(), metadata.Get()})
        require(SUCCEEDED(root->GetState(items, TRUE, &state)) && state == ECS_HIDDEN, "modern invalid workspace root hidden");
    ComPtr<IEnumExplorerCommand> enumerator;
    require(SUCCEEDED(root->EnumSubCommands(&enumerator)), "modern enumeration available before selection");
    if (!registeredDirectory)
    {
        require(enumerator->Next(1, nullptr, nullptr) == E_POINTER && enumerator->Clone(nullptr) == E_POINTER, "modern enumerator output validation");
        IExplorerCommand* dummy = nullptr;
        require(enumerator->Next(2, &dummy, nullptr) == E_POINTER, "modern bulk Next requires fetched count");
        ULONG fetched = 99;
        require(enumerator->Next(0, &dummy, &fetched) == S_OK && fetched == 0, "modern zero Next does not consume");
    }
    unsigned fileCount = 0, directoryCount = 0, multiCount = 0;
    std::vector<GUID> names;
    ComPtr<IExplorerCommand> diff;
    for (size_t index = 0; index < ARRAYSIZE(expectedMenuVerbs); ++index)
    {
        if (index == 4)
        {
            ComPtr<IExplorerCommand> separator; ULONG copied = 0;
            require(enumerator->Next(1, &separator, &copied) == S_OK && copied == 1, "modern separator follows frequent commands");
            require(SUCCEEDED(separator->GetFlags(&flags)) && flags == ECF_ISSEPARATOR, "modern separator uses native separator flag");
            require(SUCCEEDED(separator->GetTitle(nullptr, &title)) && title && !*title, "modern separator has no label"); CoTaskMemFree(title);
            require(separator->GetIcon(nullptr, &title) == S_FALSE && !title, "modern separator has no icon");
            for (auto items : {file.Get(), directory.Get(), multiple.Get()})
                require(SUCCEEDED(separator->GetState(items, TRUE, &state)) && state == ECS_ENABLED, "modern separator visible for workspace selections");
            require(SUCCEEDED(separator->GetState(outside.Get(), TRUE, &state)) && state == ECS_HIDDEN, "modern checkout-only menu hides separator");
            require(separator->Invoke(file.Get(), nullptr) == E_NOTIMPL, "modern separator cannot launch a command");
        }
        ComPtr<IExplorerCommand> child;
        ULONG fetched = 0;
        require(enumerator->Next(1, &child, &fetched) == S_OK && fetched == 1, "modern next command");
        child->GetCanonicalName(&identity);
        require(identity != ExplorerCommandClsid && std::find(names.begin(), names.end(), identity) == names.end(), "modern unique canonical command GUID");
        require(identity.Data1 == 0xb1da4600 + expectedMenuIdentities[index], "modern reordered command preserves stable canonical GUID");
        names.push_back(identity);
        require(SUCCEEDED(child->GetTitle(nullptr, &title)) && wcscmp(title, Label(commands[expectedMenuIdentities[index]])) == 0, "modern frequent-first command labels"); CoTaskMemFree(title);
        LPWSTR icon = nullptr;
        require(SUCCEEDED(child->GetIcon(nullptr, &icon)) && icon, "modern child command supplies an icon location");
        std::wstring iconModulePath;
        if (registeredDirectory)
            iconModulePath = (std::filesystem::path(registeredDirectory) / L"TortoiseSCMShell.dll").native();
        else
        {
            wchar_t modulePath[32768]{};
            require(GetModuleFileNameW(moduleInstance, modulePath, ARRAYSIZE(modulePath)) != 0, "modern child icon module path resolves");
            iconModulePath = modulePath;
        }
        const std::wstring expectedIcon = iconModulePath + L",-" + std::to_wstring(commandIconResources[expectedMenuIdentities[index]]);
        require(_wcsicmp(icon, expectedIcon.c_str()) == 0, "modern child icon matches its command resource");
        CoTaskMemFree(icon);
        require(SUCCEEDED(child->GetFlags(&flags)) && flags == ECF_DEFAULT, "modern child flags");
        child->GetState(file.Get(), TRUE, &state); fileCount += state == ECS_ENABLED;
        child->GetState(directory.Get(), TRUE, &state); directoryCount += state == ECS_ENABLED;
        child->GetState(multiple.Get(), TRUE, &state); multiCount += state == ECS_ENABLED;
        require(SUCCEEDED(child->GetState(outside.Get(), TRUE, &state)) && state == (expectedMenuIdentities[index] == 26 ? ECS_ENABLED : ECS_HIDDEN), "modern only checkout enabled outside workspace");
        require(FAILED(child->Invoke(cross.Get(), nullptr)), "modern cross-workspace invocation rejected");
        if (wcscmp(expectedMenuVerbs[index], L"diff") == 0) diff = child;
    }
    require(fileCount == 25 && directoryCount == 23 && multiCount == 7, "modern selection counts match classic filtering");
    require(FAILED(diff->Invoke(directory.Get(), nullptr)) && FAILED(diff->Invoke(nullptr, nullptr)), "modern invoke validates fresh selection");
    ComPtr<IExplorerCommand> exhausted;
    ULONG fetched = 99;
    require(enumerator->Next(1, &exhausted, &fetched) == S_FALSE && fetched == 0 && !exhausted, "modern enumeration exhaustion");
    require(enumerator->Reset() == S_OK && enumerator->Skip(7) == S_OK, "modern enumeration reset and skip includes separator");
    ComPtr<IEnumExplorerCommand> clone;
    require(SUCCEEDED(enumerator->Clone(&clone)), "modern enumeration clone");
    require(clone->Next(1, &exhausted, &fetched) == S_OK, "modern clone retains cursor");
    exhausted->GetCanonicalName(&identity); require(identity == names[6], "modern cloned cursor identity"); exhausted.Reset();
    require(enumerator->Skip(static_cast<ULONG>(ARRAYSIZE(expectedMenuVerbs) - 6)) == S_OK && enumerator->Skip(1) == S_FALSE, "modern skip exactly to end");
    require(clone->Skip(MAXDWORD) == S_FALSE && clone->Next(1, &exhausted, &fetched) == S_FALSE, "modern oversized skip reaches end without overflow");
    ComPtr<IObjectWithSite> withSite;
    require(SUCCEEDED(root.As(&withSite)), "modern IObjectWithSite supported");
    ComPtr<BackgroundSite> background; background.Attach(new BackgroundSite(first.native()));
    require(SUCCEEDED(withSite->SetSite(static_cast<IServiceProvider*>(background.Get()))), "modern background site set");
    require(SUCCEEDED(root->GetState(nullptr, TRUE, &state)) && state == ECS_ENABLED, "modern background folder resolved from site");
    require(SUCCEEDED(root->GetState(cross.Get(), TRUE, &state)) && state == ECS_HIDDEN, "modern explicit invalid selection cannot fall back to site");
    ComPtr<IUnknown> gotSite;
    require(SUCCEEDED(withSite->GetSite(IID_PPV_ARGS(&gotSite))), "modern retained site retrievable");
    enumerator.Reset(); root->EnumSubCommands(&enumerator);
    unsigned backgroundCount = 0;
    while (enumerator->Next(1, &exhausted, &fetched) == S_OK)
    {
        exhausted->GetState(nullptr, TRUE, &state); backgroundCount += state == ECS_ENABLED; exhausted.Reset();
    }
    require(backgroundCount == 24, "modern children and separator inherit background site");
    ComPtr<BackgroundSite> checkoutBackground; checkoutBackground.Attach(new BackgroundSite(first.parent_path().native()));
    withSite->SetSite(static_cast<IServiceProvider*>(checkoutBackground.Get()));
    require(SUCCEEDED(root->GetState(nullptr, TRUE, &state)) && state == ECS_ENABLED, "modern ordinary background checkout root visible");
    enumerator.Reset(); root->EnumSubCommands(&enumerator);
    backgroundCount = 0;
    while (enumerator->Next(1, &exhausted, &fetched) == S_OK)
    {
        exhausted->GetState(nullptr, TRUE, &state); backgroundCount += state == ECS_ENABLED; exhausted.Reset();
    }
    require(backgroundCount == 1, "modern ordinary background exposes only checkout");

    require(SUCCEEDED(withSite->SetSite(nullptr)) && SUCCEEDED(root->GetState(nullptr, TRUE, &state)) && state == ECS_HIDDEN, "modern clearing site clears background context");
    HWND classicWindow = CreateWindowExW(0, L"STATIC", L"", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, nullptr, nullptr);
    require(classicWindow != nullptr, "modern classic site fixture window");
    ComPtr<BackgroundSite> classic; classic.Attach(new BackgroundSite(first.native(), classicWindow));
    withSite->SetSite(static_cast<IServiceProvider*>(classic.Get()));
    require(SUCCEEDED(root->GetState(file.Get(), TRUE, &state)) && state == ECS_HIDDEN, "modern duplicate entry hidden in classic menu");
    withSite->SetSite(nullptr); DestroyWindow(classicWindow);
    WNDCLASSW treeClass{};
    treeClass.lpfnWndProc = DefWindowProcW;
    treeClass.hInstance = GetModuleHandleW(nullptr);
    treeClass.lpszClassName = L"NamespaceTreeControl";
    const ATOM registeredClass = RegisterClassW(&treeClass);
    require(registeredClass != 0, "modern tree site fixture class");
    HWND treeWindow = CreateWindowExW(0, treeClass.lpszClassName, L"", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, treeClass.hInstance, nullptr);
    require(treeWindow != nullptr, "modern tree site fixture window");
    ComPtr<BackgroundSite> tree; tree.Attach(new BackgroundSite(first.native(), treeWindow));
    withSite->SetSite(static_cast<IServiceProvider*>(tree.Get()));
    require(SUCCEEDED(root->GetState(file.Get(), TRUE, &state)) && state == ECS_ENABLED, "modern tree explicit selection supported");
    require(SUCCEEDED(root->GetState(nullptr, TRUE, &state)) && state == ECS_HIDDEN, "modern tree missing selection does not use active folder");
    enumerator.Reset(); root->EnumSubCommands(&enumerator);
    exhausted.Reset(); enumerator->Next(1, &exhausted, &fetched);
    require(FAILED(exhausted->Invoke(nullptr, nullptr)), "modern tree missing selection invocation rejected");
    withSite->SetSite(nullptr); DestroyWindow(treeWindow); UnregisterClassW(treeClass.lpszClassName, treeClass.hInstance);
}

// Child process protocol used only by this test executable when copied under
// the GUI executable name. It records the actual production Launch pathfile.
int ModernHandoffRecorder(int argc, wchar_t** argv)
{
    if (argc != 5 || wcscmp(argv[1], L"--command") ||
        (wcscmp(argv[3], L"--pathfile") && wcscmp(argv[3], L"--parent-path"))) return -1;
    wchar_t output[32768]{};
    if (!GetEnvironmentVariableW(L"TORTOISESCM_SHELL_TEST_CAPTURE", output, ARRAYSIZE(output))) return 3;
    if (wcscmp(argv[3], L"--parent-path") == 0)
    {
        const std::wstring value = std::wstring(argv[2]) + L"\n" + argv[4] + L"\n";
        const int length = WideCharToMultiByte(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
        std::string utf8(length, '\0');
        WideCharToMultiByte(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), utf8.data(), length, nullptr, nullptr);
        std::ofstream capture(output, std::ios::binary); capture << utf8; return 0;
    }
    std::ifstream input(argv[4], std::ios::binary);
    std::ofstream capture(output, std::ios::binary);
    capture << Ascii(argv[2]) << '\n' << input.rdbuf();
    capture.close(); input.close();
    DeleteFileW(argv[4]);
    return 0;
}

void ModernHandoffTest(const std::filesystem::path& first, const wchar_t* binaryDirectory)
{
    const auto stage = first.parent_path() / L"modern handoff";
    std::filesystem::create_directory(stage);
    const auto dll = stage / L"TortoiseSCMShell.dll";
    std::filesystem::copy_file(std::filesystem::path(binaryDirectory) / L"TortoiseSCMShell.dll", dll);
    wchar_t self[32768]{}; GetModuleFileNameW(nullptr, self, ARRAYSIZE(self));
    std::filesystem::copy_file(self, stage / L"TortoiseSCM.exe");
    HMODULE loaded = LoadLibraryExW(dll.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    require(loaded != nullptr, "modern handoff production DLL staged");
    auto getClass = reinterpret_cast<HRESULT(WINAPI*)(REFCLSID, REFIID, void**)>(GetProcAddress(loaded, "DllGetClassObject"));
    {
        ComPtr<IClassFactory> factory;
        require(getClass && SUCCEEDED(getClass(ExplorerCommandClsid, IID_PPV_ARGS(&factory))), "modern handoff production factory");
        ComPtr<IExplorerCommand> root;
        require(SUCCEEDED(factory->CreateInstance(nullptr, IID_PPV_ARGS(&root))), "modern handoff production root");
        ComPtr<IEnumExplorerCommand> enumerator; root->EnumSubCommands(&enumerator);
        ComPtr<IExplorerCommand> checkin;
        ULONG fetched = 0;
        require(enumerator->Skip(1) == S_OK && enumerator->Next(1, &checkin, &fetched) == S_OK, "modern handoff checkin command follows update");
        const auto selectedPath = (first / L"child/selected 中文 & item.txt").make_preferred();
        std::ofstream(selectedPath) << "fixture";
        auto previous = ModernSelection({(first / L"child/file.txt").native()});
        auto selected = ModernSelection({selectedPath.native()});
        EXPCMDSTATE state;
        require(SUCCEEDED(checkin->GetState(previous.Get(), TRUE, &state)) && state == ECS_ENABLED, "modern handoff prior selection state");
        const auto capturePath = stage / L"capture.txt";
        wchar_t oldCapture[32768]{};
        const DWORD oldLength = GetEnvironmentVariableW(L"TORTOISESCM_SHELL_TEST_CAPTURE", oldCapture, ARRAYSIZE(oldCapture));
        require(SetEnvironmentVariableW(L"TORTOISESCM_SHELL_TEST_CAPTURE", capturePath.c_str()) != FALSE, "modern handoff capture environment");
        require(SUCCEEDED(checkin->Invoke(selected.Get(), nullptr)), "modern handoff invokes actual process launcher");
        SetEnvironmentVariableW(L"TORTOISESCM_SHELL_TEST_CAPTURE", oldLength && oldLength < ARRAYSIZE(oldCapture) ? oldCapture : nullptr);
        const std::wstring wideExpected = L"checkin\n" + selectedPath.native() + L"\n";
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
        require(contents == expected, "modern handoff preserves fresh selection and UTF-8 path bytes");
        // Exercise the shipping DLL's real launcher for the basic file operations.
        // The staged executable only records argv/pathfile bytes, never edits SCM files.
        for (const wchar_t* command : {L"add", L"remove"})
        {
            ULONG index = 0;
            while (index < ARRAYSIZE(expectedMenuVerbs) && wcscmp(expectedMenuVerbs[index], command) != 0) ++index;
            require(index < ARRAYSIZE(expectedMenuVerbs), "modern basic operation command exists");
            enumerator->Reset(); enumerator->Skip(index + (index >= 4 ? 1 : 0));
            ComPtr<IExplorerCommand> operation;
            require(enumerator->Next(1, &operation, &fetched) == S_OK, "modern basic operation enumerated");
            unsigned selectionCase = 0;
            for (const auto& operationPath : {selectedPath, first})
            {
                auto operationSelection = ModernSelection({operationPath.native()});
                require(SUCCEEDED(operation->GetState(operationSelection.Get(), TRUE, &state)) && state == ECS_ENABLED, "modern basic operation enabled for file and directory");
                const auto operationCapture = stage / (std::wstring(command) + L"-" + std::to_wstring(selectionCase++) + L".txt");
                require(SetEnvironmentVariableW(L"TORTOISESCM_SHELL_TEST_CAPTURE", operationCapture.c_str()) != FALSE, "modern basic operation capture environment");
                const HRESULT invoked = operation->Invoke(operationSelection.Get(), nullptr);
                SetEnvironmentVariableW(L"TORTOISESCM_SHELL_TEST_CAPTURE", oldLength && oldLength < ARRAYSIZE(oldCapture) ? oldCapture : nullptr);
                require(SUCCEEDED(invoked), "modern basic operation invokes production launcher");
                const std::wstring wideOperation = std::wstring(command) + L"\n" + operationPath.native() + L"\n";
                const int size = WideCharToMultiByte(CP_UTF8, 0, wideOperation.c_str(), static_cast<int>(wideOperation.size()), nullptr, 0, nullptr, nullptr);
                std::string operationExpected(size, '\0');
                WideCharToMultiByte(CP_UTF8, 0, wideOperation.c_str(), static_cast<int>(wideOperation.size()), operationExpected.data(), size, nullptr, nullptr);
                std::string actual;
                const auto operationDeadline = GetTickCount64() + 10000;
                do
                {
                    std::ifstream capture(operationCapture, std::ios::binary);
                    actual.assign(std::istreambuf_iterator<char>(capture), std::istreambuf_iterator<char>());
                    if (actual == operationExpected) break;
                    Sleep(20);
                } while (GetTickCount64() < operationDeadline);
                const std::string label = "modern production dispatch " + Ascii(command) + " selection=" + std::to_string(selectionCase - 1);
                require(actual == operationExpected, label.c_str());
                require(std::filesystem::exists(operationPath), "modern recorder never removes selected fixture");
            }
        }
        enumerator->Reset(); enumerator->Skip(27);
        ComPtr<IExplorerCommand> checkout; require(enumerator->Next(1, &checkout, &fetched) == S_OK, "modern handoff checkout command");
        const auto unicodeParent = first.parent_path() / L"checkout \u4e2d\u6587 & parent";
        std::filesystem::create_directory(unicodeParent);
        unsigned checkoutCase = 0;
        for (const auto& parentPath : {unicodeParent, first.root_path()})
        {
            auto checkoutSelection = ModernSelection({parentPath.native()});
            require(SUCCEEDED(checkout->GetState(checkoutSelection.Get(), TRUE, &state)) && state == ECS_ENABLED, "modern checkout ordinary parent and drive root enabled");
            const auto checkoutCapture = stage / (L"checkout-" + std::to_wstring(checkoutCase++) + L".txt");
            require(SetEnvironmentVariableW(L"TORTOISESCM_SHELL_TEST_CAPTURE", checkoutCapture.c_str()) != FALSE, "modern checkout capture environment");
            const HRESULT invoked = checkout->Invoke(checkoutSelection.Get(), nullptr);
            SetEnvironmentVariableW(L"TORTOISESCM_SHELL_TEST_CAPTURE", oldLength && oldLength < ARRAYSIZE(oldCapture) ? oldCapture : nullptr);
            require(SUCCEEDED(invoked), "modern checkout production launcher");
            const std::wstring wideCheckout = L"create-workspace\n" + parentPath.native() + L"\n";
            const int size = WideCharToMultiByte(CP_UTF8, 0, wideCheckout.c_str(), static_cast<int>(wideCheckout.size()), nullptr, 0, nullptr, nullptr);
            std::string checkoutExpected(size, '\0');
            WideCharToMultiByte(CP_UTF8, 0, wideCheckout.c_str(), static_cast<int>(wideCheckout.size()), checkoutExpected.data(), size, nullptr, nullptr);
            std::string checkoutContents;
            const auto checkoutDeadline = GetTickCount64() + 10000;
            do
            {
                std::ifstream capture(checkoutCapture, std::ios::binary);
                checkoutContents.assign(std::istreambuf_iterator<char>(capture), std::istreambuf_iterator<char>());
                if (checkoutContents == checkoutExpected) break;
                Sleep(20);
            } while (GetTickCount64() < checkoutDeadline);
            require(checkoutContents == checkoutExpected, "modern checkout preserves Unicode spaces ampersand and root trailing slash");
        }

    }
    FreeLibrary(loaded);
    // Wait for the short-lived recorder to release its image before fixture cleanup.
    const auto executable = stage / L"TortoiseSCM.exe";
    const auto deadline = GetTickCount64() + 10000;
    while (!DeleteFileW(executable.c_str()) && GetTickCount64() < deadline) Sleep(20);
    require(GetFileAttributesW(executable.c_str()) == INVALID_FILE_ATTRIBUTES, "modern handoff recorder exits");
}
