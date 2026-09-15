#include "StreamlineRuntime.h"
namespace {
std::mutex ProbeMutex;
void Validate(const RbaSlProbeDesc* description) {
    Require(description && description->structSize == sizeof(RbaSlProbeDesc) && description->abiVersion == Abi,
        RbaSlAbiMismatch, "Streamline probe description does not match ABI 1");
    Require(description->reserved == 0, RbaSlInvalidArgument, "Streamline probe reserved field must be zero");
    Require(description->applicationId != 0 || (description->unityVersion && *description->unityVersion && ProjectId(description->projectId)),
        RbaSlInvalidArgument, "Provide the actual Unity version and project GUID, or a legitimately assigned NVIDIA application ID");
    Require(!description->projectId || ProjectId(description->projectId), RbaSlInvalidArgument,
        "Streamline project identity must be an actual project GUID in canonical form");
    Require(description->runtimeDirectory && *description->runtimeDirectory &&
        std::filesystem::path(description->runtimeDirectory).is_absolute(), RbaSlInvalidArgument,
        "Streamline production runtime directory must be absolute");
    Require(description->d3d11Device != nullptr, RbaSlInvalidArgument, "The source D3D11 device is required to identify the actual adapter");
}

void Probe(const RbaSlProbeDesc& description, RbaSlProbeStatus& status) {
    // No command queue or swapchain is created. The owner preserves SDK/device
    // lifetime on exceptions, including a failed SDK shutdown.
    RuntimeOwner owner;
    Runtime& runtime = *owner.value;
    const auto directory = std::filesystem::weakly_canonical(description.runtimeDirectory);
    runtime.Load(directory);
    runtime.directoryString = directory.wstring();
    runtime.unityVersion = description.unityVersion ? description.unityVersion : "";
    runtime.projectId = description.projectId ? description.projectId : "";
    runtime.paths[0] = runtime.directoryString.c_str();
    auto& preferences = runtime.preferences;
    preferences.flags = sl::PreferenceFlags::eDisableCLStateTracking | sl::PreferenceFlags::eUseManualHooking;
    // Disable optional OTA requests. Driver overrides can still load alternatives;
    // audit the actual loaded modules after each initialization boundary below.
    preferences.featuresToLoad = runtime.features;
    preferences.numFeaturesToLoad = static_cast<uint32_t>(std::size(runtime.features));
    preferences.pathsToPlugins = runtime.paths;
    preferences.numPathsToPlugins = 1;
    preferences.applicationId = description.applicationId;
    preferences.engine = description.unityVersion && *description.unityVersion ? sl::EngineType::eUnity : sl::EngineType::eCustom;
    preferences.engineVersion = runtime.unityVersion.empty() ? nullptr : runtime.unityVersion.c_str();
    preferences.projectId = runtime.projectId.empty() ? nullptr : runtime.projectId.c_str();
    preferences.renderAPI = sl::RenderAPI::eD3D12;
    preferences.logMessageCallback = Log;
    const sl::Result initResult = runtime.Start();
    AuditLoadedRuntime(directory);
    CheckSl(initResult, status, "slInit");

    ComPtr<IDXGIDevice> dxgiDevice;
    Check(static_cast<ID3D11Device*>(description.d3d11Device)->QueryInterface(IID_PPV_ARGS(&dxgiDevice)), "Query source DXGI device");
    ComPtr<IDXGIAdapter> adapter;
    Check(dxgiDevice->GetAdapter(&adapter), "Get source GPU adapter");
    DXGI_ADAPTER_DESC adapterDescription{};
    Check(adapter->GetDesc(&adapterDescription), "Describe source GPU adapter");
    status.adapterVendorId = adapterDescription.VendorId;
    status.adapterDeviceId = adapterDescription.DeviceId;
    status.adapterLuidLow = adapterDescription.AdapterLuid.LowPart;
    status.adapterLuidHigh = adapterDescription.AdapterLuid.HighPart;
    sl::AdapterInfo adapterInfo{};
    adapterInfo.deviceLUID = reinterpret_cast<uint8_t*>(&adapterDescription.AdapterLuid);
    adapterInfo.deviceLUIDSizeInBytes = sizeof(LUID);
    const sl::Result fgSupport = runtime.isSupported(sl::kFeatureDLSS_G, adapterInfo);
    status.featureSupported = fgSupport == sl::Result::eOk;
    const sl::Result reflexSupport = runtime.isSupported(sl::kFeatureReflex, adapterInfo);
    status.reflexSupported = reflexSupport == sl::Result::eOk;
    if (!status.featureSupported || !status.reflexSupported) {
        const sl::Result unsupported = !status.featureSupported ? fgSupport : reflexSupport;
        status.sdkResult = static_cast<uint32_t>(unsupported);
        throw Failure(RbaSlUnsupported, std::string("DLSS FG/Streamline Reflex unavailable on the source adapter: ") + sl::getResultAsStr(unsupported));
    }
    sl::FeatureRequirements requirements{};
    CheckSl(runtime.requirements(sl::kFeatureDLSS_G, requirements), status, "slGetFeatureRequirements");
    Require((requirements.flags & sl::FeatureRequirementFlags::eD3D12Supported) != 0,
        RbaSlUnsupported, "DLSS FG runtime does not report D3D12 support");
    Check(D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&runtime.device12)), "Create same-adapter probe D3D12 device");
    const LUID luid12 = runtime.device12->GetAdapterLuid();
    Require(luid12.LowPart == adapterDescription.AdapterLuid.LowPart && luid12.HighPart == adapterDescription.AdapterLuid.HighPart,
        RbaSlDeviceError, "D3D11/D3D12 adapter identities differ");
    const sl::Result deviceResult = runtime.setDevice(runtime.device12.Get());
    AuditLoadedRuntime(directory);
    CheckSl(deviceResult, status, "slSetD3DDevice");
    // Feature initialization can narrow support; repeat both checks after setting
    // the actual D3D12 device, as required by the SDK lifecycle contract.
    const sl::Result initializedFg = runtime.isSupported(sl::kFeatureDLSS_G, adapterInfo);
    const sl::Result initializedReflex = runtime.isSupported(sl::kFeatureReflex, adapterInfo);
    status.featureSupported = initializedFg == sl::Result::eOk;
    status.reflexSupported = initializedReflex == sl::Result::eOk;
    if (!status.featureSupported || !status.reflexSupported) {
        status.sdkResult = static_cast<uint32_t>(!status.featureSupported ? initializedFg : initializedReflex);
        throw Failure(RbaSlUnsupported, std::string("DLSS FG/Reflex initialization support check: ") +
            sl::getResultAsStr(!status.featureSupported ? initializedFg : initializedReflex));
    }
    void* entry = nullptr;
    CheckSl(runtime.featureFunction(sl::kFeatureDLSS_G, "slDLSSGGetState", entry), status, "Resolve slDLSSGGetState");
    Require(entry != nullptr, RbaSlSdkError, "DLSS FG state function is missing");
    AuditFeatureModule(directory, L"sl.dlss_g.dll", entry);
    void* reflexEntry = nullptr;
    CheckSl(runtime.featureFunction(sl::kFeatureReflex, "slReflexGetState", reflexEntry), status, "Resolve slReflexGetState");
    AuditFeatureModule(directory, L"sl.reflex.dll", reflexEntry);
    sl::DLSSGState state{};
    const sl::ViewportHandle viewport(0);
    CheckSl(reinterpret_cast<PFun_slDLSSGGetState*>(entry)(viewport, state, nullptr), status, "slDLSSGGetState");
    AuditLoadedRuntime(directory);
    status.maxGeneratedFrames = state.numFramesToGenerateMax;
    status.dynamicMfgSupported = state.bIsDynamicMFGSupported == sl::Boolean::eTrue;
    status.sdkRuntimeStatus = static_cast<uint32_t>(state.status);
    for (uint32_t multiplier = 2; multiplier <= 4; ++multiplier)
        if (multiplier - 1 <= state.numFramesToGenerateMax) status.queriedMultiplierMask |= 1u << multiplier;
    // Deliberately separate successful SDK discovery from a working provider.
    Text(status.reason, sizeof(status.reason),
        "SDK capabilities queried; unavailable until a Streamline-intercepted D3D12 presenter, matching Reflex markers, retained inputs and UI composition are implemented and validated");
    CheckSl(runtime.Stop(), status, "slShutdown");
}
}

static_assert(sizeof(RbaSlProbeDesc) == 48, "Streamline description ABI changed");
static_assert(sizeof(RbaSlProbeStatus) == 640, "Streamline status ABI changed");

RBA_SL_API uint32_t RbaSlProbe(const RbaSlProbeDesc* description, RbaSlProbeStatus* status) {
    if (!status || status->structSize != sizeof(RbaSlProbeStatus) || status->abiVersion != Abi) return RbaSlAbiMismatch;
    *status = {};
    status->structSize = sizeof(*status); status->abiVersion = Abi;
    Text(status->runtimeVersion, sizeof(status->runtimeVersion), "Streamline 2.14.1 (pinned production runtime)");
    std::unique_lock<std::mutex> lock(ProbeMutex, std::defer_lock);
    try {
        Validate(description);
        Require(lock.try_lock(), RbaSlAlreadyOwned, "Another Streamline probe is running");
        { std::lock_guard<std::mutex> logLock(LogMutex); LastSdkError.clear(); }
        Probe(*description, *status);
    } catch (const Failure& failure) {
        status->result = failure.code;
        Text(status->reason, sizeof(status->reason), failure.what());
        if (failure.code == RbaSlUnsupported || failure.code == RbaSlSdkError) {
            std::lock_guard<std::mutex> logLock(LogMutex);
            if (!LastSdkError.empty()) {
                const std::string explanation = std::string(failure.what()) + "; " + LastSdkError;
                Text(status->reason, sizeof(status->reason), explanation.c_str());
            }
        }
    } catch (const std::exception& exception) {
        status->result = RbaSlSdkError;
        Text(status->reason, sizeof(status->reason), exception.what());
    } catch (...) {
        status->result = RbaSlSdkError;
        Text(status->reason, sizeof(status->reason), "Unexpected Streamline probe failure");
    }
    return status->result;
}
