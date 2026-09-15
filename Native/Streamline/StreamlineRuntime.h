#pragma once
#include "StreamlineProbe.h"
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <tlhelp32.h>
#include <d3d11.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <bcrypt.h>
#include <wrl/client.h>
#include <sl.h>
#include <sl_dlss_g.h>
#include <sl_helpers.h>
#include <sl_security.h>
#include <array>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>

using Microsoft::WRL::ComPtr;
namespace {
constexpr uint32_t Abi = 1;

std::mutex LogMutex;
std::string LastSdkError;
void Log(sl::LogType type, const char* message) {
    if (type != sl::LogType::eError) return;
    std::lock_guard<std::mutex> lock(LogMutex);
    if (LastSdkError.empty()) LastSdkError = message ? message : "";
}

struct Failure : std::runtime_error {
    uint32_t code;
    Failure(uint32_t result, const std::string& message) : std::runtime_error(message), code(result) {}
};
void Require(bool value, uint32_t result, const char* message) {
    if (!value) throw Failure(result, message);
}
void Text(char* output, size_t size, const char* input) {
    std::snprintf(output, size, "%s", input ? input : "");
}
void Check(HRESULT result, const char* operation) {
    if (FAILED(result)) {
        char message[256]{};
        std::snprintf(message, sizeof(message), "%s (HRESULT 0x%08lx)", operation, static_cast<unsigned long>(result));
        throw Failure(RbaSlDeviceError, message);
    }
}
[[maybe_unused]] void CheckSl(sl::Result result, RbaSlProbeStatus& status, const char* operation) {
    status.sdkResult = static_cast<uint32_t>(result);
    if (result != sl::Result::eOk)
        throw Failure(RbaSlSdkError, std::string(operation) + ": " + sl::getResultAsStr(result));
}
[[maybe_unused]] bool ProjectId(const char* value) {
    if (!value || std::strlen(value) != 36) return false;
    for (size_t i = 0; i < 36; ++i) {
        if (i == 8 || i == 13 || i == 18 || i == 23) { if (value[i] != '-') return false; }
        else if (!((value[i] >= '0' && value[i] <= '9') || (value[i] >= 'a' && value[i] <= 'f') ||
            (value[i] >= 'A' && value[i] <= 'F'))) return false;
    }
    return true;
}

struct File {
    HANDLE handle = INVALID_HANDLE_VALUE;
    ~File() { if (handle != INVALID_HANDLE_VALUE) CloseHandle(handle); }
};
struct Hash {
    BCRYPT_ALG_HANDLE algorithm = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    ~Hash() {
        if (hash) BCryptDestroyHash(hash);
        if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0);
    }
};
// Shared across the separately built probe/provider DLLs. A semaphore has no
// thread-affine ownership and closes the check/load race between our callers.
// Quarantined sessions keep this lease just like their loaded modules/file locks.
struct ProcessSessionLease {
    HANDLE semaphore = nullptr;
    bool acquired = false;
    ~ProcessSessionLease() {
        if (acquired) ReleaseSemaphore(semaphore, 1, nullptr);
        if (semaphore) CloseHandle(semaphore);
    }
    void Acquire() {
        wchar_t name[96]{};
        std::swprintf(name, std::size(name), L"Local\\ReduxBetterAA.Streamline.%lu", GetCurrentProcessId());
        semaphore = CreateSemaphoreW(nullptr, 1, 1, name);
        Require(semaphore != nullptr, RbaSlRuntimeUnavailable, "Cannot establish Streamline process ownership");
        acquired = WaitForSingleObject(semaphore, 0) == WAIT_OBJECT_0;
        Require(acquired, RbaSlAlreadyOwned, "Another Streamline probe/provider owns this process session");
    }
};
struct RuntimeFile { const wchar_t* name; const char* hash; };
// Official v2.14.1 Windows x64 production archive, release 2026-09-08.
// Keep all verified files locked against modification while vendor code runs.
constexpr std::array<RuntimeFile, 6> PinnedFiles{{
    {L"sl.interposer.dll", "8c87c9499461da561edd529aa9bf7831d67d7b94ebb1c1a5ed54ef4934e1ea4c"},
    {L"sl.common.dll", "82924a8954dd671e09351c5de0eb87ad0eb25b944cc9f9ab955ca1d9950de15d"},
    {L"sl.pcl.dll", "f13d51cfa05f4cd514df2026049e2db8adf359221713170ad386fd499915b582"},
    {L"sl.reflex.dll", "0ce9725e3e03ea9e7f81d008b57f33ee365973d2e349131c8b1c3e3378fe2db0"},
    {L"sl.dlss_g.dll", "f4a6b2b14dcc0b1485989e430d3b4e3a44ac1800b92ba1ad74f476e64fb2b09c"},
    {L"nvngx_dlssg.dll", "ff6e90eb78b827927dff5b4ecc6b1c870c2e9bca29ed9f48c7d348cc9e170b82"}
}};

// A driver DRS override can bypass the SDK's optional OTA preferences. Audit
// modules that actually loaded, not merely the files offered to slInit.
void AuditLoadedRuntime(const std::filesystem::path& directory) {
    File snapshot;
    snapshot.handle = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, GetCurrentProcessId());
    Require(snapshot.handle != INVALID_HANDLE_VALUE, RbaSlRuntimeUnavailable, "Cannot enumerate loaded Streamline modules");
    MODULEENTRY32W entry{}; entry.dwSize = sizeof(entry);
    Require(Module32FirstW(snapshot.handle, &entry) != FALSE, RbaSlRuntimeUnavailable, "Cannot inspect loaded Streamline modules");
    do {
        const RuntimeFile* expected = nullptr;
        for (const auto& pin : PinnedFiles) if (_wcsicmp(entry.szModule, pin.name) == 0) expected = &pin;
        const bool streamlinePlugin = _wcsnicmp(entry.szModule, L"sl.", 3) == 0 ||
            _wcsnicmp(entry.szModule, L"nvngx_dlssg", 11) == 0;
        if (!expected && !streamlinePlugin) continue;
        Require(expected != nullptr, RbaSlRuntimeUnavailable, "An unpinned Streamline plugin is loaded");
        Require(std::filesystem::equivalent(directory / expected->name, entry.szExePath), RbaSlRuntimeUnavailable,
            "Streamline/NGX loaded outside the verified runtime directory (possibly a driver override); capabilities were rejected");
        // The expected file is still held without write/delete sharing after its
        // SHA256 check. File identity equality therefore proves this module uses
        // that locked pin, without trusting its version string or basename.
    } while (Module32NextW(snapshot.handle, &entry));
    Require(GetLastError() == ERROR_NO_MORE_FILES, RbaSlRuntimeUnavailable, "Loaded Streamline module enumeration was incomplete");
}

void AuditFeatureModule(const std::filesystem::path& directory, const wchar_t* expected, void* function) {
    HMODULE module = nullptr;
    Require(function && GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(function), &module), RbaSlRuntimeUnavailable, "Cannot identify the loaded Streamline feature implementation");
    std::array<wchar_t, 32768> path{};
    const DWORD size = GetModuleFileNameW(module, path.data(), static_cast<DWORD>(path.size()));
    Require(size && size < path.size() && std::filesystem::equivalent(directory / expected, path.data()),
        RbaSlRuntimeUnavailable, "Streamline feature entry point belongs to an unverified runtime module");
}

void PinCallbackModule() {
    HMODULE callbackModule = nullptr;
    Require(GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
        reinterpret_cast<LPCWSTR>(&Log), &callbackModule) != FALSE, RbaSlRuntimeUnavailable,
        "Cannot retain the native module containing Streamline's log callback");
}

void VerifyFile(const std::filesystem::path& path, const char* expected, File& file) {
    file.handle = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr,
        OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    Require(file.handle != INVALID_HANDLE_VALUE, RbaSlRuntimeUnavailable, "A pinned Streamline runtime file is missing or locked");
    Hash hash;
    Require(BCryptOpenAlgorithmProvider(&hash.algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) >= 0 &&
        BCryptCreateHash(hash.algorithm, &hash.hash, nullptr, 0, nullptr, 0, 0) >= 0,
        RbaSlRuntimeUnavailable, "Cannot initialize Streamline runtime verification");
    std::array<unsigned char, 65536> bytes{};
    DWORD count = 0;
    do {
        Require(ReadFile(file.handle, bytes.data(), static_cast<DWORD>(bytes.size()), &count, nullptr) != FALSE,
            RbaSlRuntimeUnavailable, "Cannot read Streamline runtime for verification");
        if (count) Require(BCryptHashData(hash.hash, bytes.data(), count, 0) >= 0,
            RbaSlRuntimeUnavailable, "Cannot hash Streamline runtime");
    } while (count);
    std::array<unsigned char, 32> digest{};
    Require(BCryptFinishHash(hash.hash, digest.data(), static_cast<ULONG>(digest.size()), 0) >= 0,
        RbaSlRuntimeUnavailable, "Cannot finish Streamline runtime verification");
    char hex[65]{};
    for (size_t i = 0; i < digest.size(); ++i) std::snprintf(hex + i * 2, 3, "%02x", digest[i]);
    Require(std::strcmp(hex, expected) == 0, RbaSlRuntimeUnavailable, "Streamline runtime differs from pinned production SDK 2.14.1");
}

struct Runtime {
    ProcessSessionLease processLease;
    ComPtr<ID3D12Device> device12;
    HMODULE module = nullptr;
    std::array<File, PinnedFiles.size()> files;
    PFun_slInit* init = nullptr;
    PFun_slShutdown* shutdown = nullptr;
    PFun_slIsFeatureSupported* isSupported = nullptr;
    PFun_slGetFeatureRequirements* requirements = nullptr;
    PFun_slGetFeatureFunction* featureFunction = nullptr;
    PFun_slSetD3DDevice* setDevice = nullptr;
    bool initAttempted = false;
    bool shutdownAttempted = false;
    std::wstring directoryString;
    std::string unityVersion, projectId;
    const wchar_t* paths[1]{};
    const sl::Feature features[2] = { sl::kFeatureDLSS_G, sl::kFeatureReflex };
    sl::Preferences preferences{};

    // RuntimeOwner only destroys this after a successful shutdown. A failed
    // shutdown retains the whole session, including device and file locks.
    ~Runtime() { if (module) FreeLibrary(module); }
    sl::Result Start() {
        // slInit can publish callbacks and partially load plugins before failing.
        // Even a failed attempt must go through Stop or retain the full session.
        PinCallbackModule();
        shutdownAttempted = false;
        initAttempted = true;
        return init(preferences, sl::kSDKVersion);
    }
    sl::Result Stop() {
        if (!initAttempted) return sl::Result::eOk;
        shutdownAttempted = true;
        const sl::Result result = shutdown();
        // eErrorNotInitialized is not proof of cleanup: the pinned implementation
        // may already have installed its logging callback before the early exit.
        if (result == sl::Result::eOk) initAttempted = false;
        return result;
    }
    template <typename T> void Import(T*& function, const char* name) {
        function = reinterpret_cast<T*>(GetProcAddress(module, name));
        Require(function != nullptr, RbaSlRuntimeUnavailable, "Pinned Streamline core export is missing");
    }
    void Load(const std::filesystem::path& directory) {
        processLease.Acquire();
        Require(std::filesystem::is_directory(directory), RbaSlRuntimeUnavailable,
            "Pinned Streamline runtime directory is missing");
        // SL discovers plugins in this directory. Reject additional plugin DLLs
        // rather than executing unpinned dependencies during feature discovery.
        for (const auto& entry : std::filesystem::directory_iterator(directory)) {
            if (!entry.is_regular_file() || _wcsicmp(entry.path().extension().c_str(), L".dll") != 0) continue;
            bool known = false;
            for (const auto& file : PinnedFiles)
                known |= _wcsicmp(entry.path().filename().c_str(), file.name) == 0;
            Require(known, RbaSlRuntimeUnavailable,
                "Use a dedicated Streamline runtime directory containing only the six pinned DLLs");
        }
        for (size_t i = 0; i < PinnedFiles.size(); ++i) {
            Require(GetModuleHandleW(PinnedFiles[i].name) == nullptr, RbaSlAlreadyOwned,
                "Another Streamline/NGX FG owner is already loaded; this session will not replace it");
            VerifyFile(directory / PinnedFiles[i].name, PinnedFiles[i].hash, files[i]);
        }
        const auto path = directory / PinnedFiles[0].name;
        Require(sl::security::verifyEmbeddedSignature(path.c_str()), RbaSlRuntimeUnavailable,
            "NVIDIA embedded Streamline signature validation failed");
        module = LoadLibraryExW(path.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        Require(module != nullptr, RbaSlRuntimeUnavailable, "Cannot load pinned Streamline interposer");
        Import(init, "slInit"); Import(shutdown, "slShutdown"); Import(isSupported, "slIsFeatureSupported");
        Import(requirements, "slGetFeatureRequirements"); Import(featureFunction, "slGetFeatureFunction");
        Import(setDevice, "slSetD3DDevice");
    }
};
struct RuntimeOwner {
    std::unique_ptr<Runtime> value = std::make_unique<Runtime>();
    ~RuntimeOwner() {
        if (value && value->initAttempted && (value->shutdownAttempted || value->Stop() != sl::Result::eOk))
            (void)value.release(); // Quarantine until process exit; never free a live SDK/device.
    }
};


} // private runtime namespace
