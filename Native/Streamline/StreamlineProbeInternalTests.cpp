// Compile the actual private loader/owner into this isolated test executable.
// Fault injection never changes the production ABI or executes a fake provider.
#define RBA_STREAMLINE_EXPORTS
#include "StreamlineProbe.cpp"

namespace {
unsigned ShutdownCalls = 0;
sl::Result ShutdownResult = sl::Result::eOk;
sl::Result FailedInitialization(const sl::Preferences&, uint64_t) { return sl::Result::eErrorNoPlugins; }
sl::Result TestShutdown() { ++ShutdownCalls; return ShutdownResult; }
void Expect(bool value, const char* message) { if (!value) throw std::runtime_error(message); }

void PartialInitializationCleanup() {
    ShutdownCalls = 0; ShutdownResult = sl::Result::eOk;
    HANDLE retainedFile = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    Expect(retainedFile != nullptr, "Create cleanup sentinel failed");
    {
        RuntimeOwner owner;
        owner.value->files[0].handle = retainedFile;
        owner.value->init = FailedInitialization;
        owner.value->shutdown = TestShutdown;
        Expect(owner.value->Start() == sl::Result::eErrorNoPlugins, "Init failure injection failed");
        // Simulates the same exception path used by CheckSl in production.
    }
    DWORD flags = 0;
    Expect(ShutdownCalls == 1, "Failed slInit must still call slShutdown exactly once");
    Expect(!GetHandleInformation(retainedFile, &flags), "Successful shutdown must release retained files");
}

void FailedShutdownRetainsSession(sl::Result shutdownResult) {
    ShutdownCalls = 0; ShutdownResult = shutdownResult;
    Runtime* retained = nullptr;
    HANDLE retainedFile = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    Expect(retainedFile != nullptr, "Create quarantine sentinel failed");
    {
        RuntimeOwner owner;
        retained = owner.value.get();
        retained->files[0].handle = retainedFile;
        retained->init = FailedInitialization;
        retained->shutdown = TestShutdown;
        Expect(retained->Start() == sl::Result::eErrorNoPlugins, "Partial init failure injection failed");
    }
    DWORD flags = 0;
    Expect(ShutdownCalls == 1 && GetHandleInformation(retainedFile, &flags),
        "Uncertain SDK cleanup must retain files and session instead of unloading");
    // This session contains only test callbacks: explicitly reclaim the sentinel.
    delete retained;
}

void ReinitializedAttemptMustShutdownAgain() {
    ShutdownCalls = 0; ShutdownResult = sl::Result::eOk;
    {
        RuntimeOwner owner; owner.value->init = FailedInitialization; owner.value->shutdown = TestShutdown;
        Expect(owner.value->Start() == sl::Result::eErrorNoPlugins, "First injected init did not fail");
        Expect(owner.value->Stop() == sl::Result::eOk && owner.value->shutdownAttempted, "First shutdown did not complete");
        Expect(owner.value->Start() == sl::Result::eErrorNoPlugins && !owner.value->shutdownAttempted,
            "Re-init inherited the prior session's shutdown-attempt flag");
    }
    Expect(ShutdownCalls == 2, "A failed reinitialization must receive its own cleanup attempt");
}

void ExternalPluginRejected(const wchar_t* fixturePath) {
    const auto modulePath = std::filesystem::canonical(fixturePath);
    const auto expectedDirectory = modulePath.parent_path() / L"expected";
    std::filesystem::create_directories(expectedDirectory);
    const auto expectedFile = expectedDirectory / L"sl.reflex.dll";
    std::filesystem::copy_file(modulePath, expectedFile, std::filesystem::copy_options::overwrite_existing);
    HMODULE fixture = LoadLibraryExW(modulePath.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    Expect(fixture != nullptr, "Could not load the harmless module-audit fixture");
    bool rejected = false;
    try { AuditLoadedRuntime(expectedDirectory); }
    catch (const Failure& failure) { rejected = failure.code == RbaSlRuntimeUnavailable; }
    FreeLibrary(fixture);
    Expect(rejected, "A loaded plugin outside the verified directory must be rejected even when its bytes match");
}
}

int wmain(int argc, wchar_t** argv) {
    try {
        Expect(argc == 2, "Pass the external module-audit fixture path");
        PartialInitializationCleanup();
        FailedShutdownRetainsSession(sl::Result::eErrorExceptionHandler);
        FailedShutdownRetainsSession(sl::Result::eErrorNotInitialized);
        ReinitializedAttemptMustShutdownAgain();
        ExternalPluginRejected(argv[1]);
        std::puts("PASS: partial-init cleanup, uncertain-shutdown quarantine and loaded override rejection.");
        return 0;
    } catch (const std::exception& error) { std::fprintf(stderr, "%s\n", error.what()); return 1; }
}
