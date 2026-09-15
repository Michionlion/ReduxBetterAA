#include "StreamlineProbe.h"
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <wrl/client.h>
#include <cstdio>
#include <cstring>
#include <stdexcept>
using Microsoft::WRL::ComPtr;
namespace {
void Expect(bool value, const char* message) { if (!value) throw std::runtime_error(message); }
RbaSlProbeStatus Status() { RbaSlProbeStatus value{}; value.structSize = sizeof(value); value.abiVersion = 1; return value; }
RbaSlProbeDesc Description() {
    RbaSlProbeDesc value{}; value.structSize = sizeof(value); value.abiVersion = 1;
    value.unityVersion = "6000.5.8f1";
    // Actual ReduxBetterAA Unity productGUID from ProjectSettings.asset.
    // No NVIDIA sample/game application identity is used.
    value.projectId = "5be2ef0c-dad9-b134-4ae1-03b0d475456b"; return value;
}
void Contract() {
    auto status = Status();
    Expect(RbaSlProbe(nullptr, &status) == RbaSlAbiMismatch, "Null description must fail");
    Expect(!status.available && !status.queriedMultiplierMask, "Failed query must not advertise FG");
    auto description = Description();
    description.abiVersion = 999;
    status = Status();
    Expect(RbaSlProbe(&description, &status) == RbaSlAbiMismatch, "Unknown ABI must fail");
    description = Description(); description.unityVersion = nullptr;
    status = Status();
    Expect(RbaSlProbe(&description, &status) == RbaSlInvalidArgument && std::strstr(status.reason, "Unity version"), "Missing identity must fail before loading DLLs");
    description = Description(); description.projectId = nullptr;
    status = Status();
    Expect(RbaSlProbe(&description, &status) == RbaSlInvalidArgument && std::strstr(status.reason, "project GUID"), "Engine version alone must not select NVIDIA temporary ID");
    description = Description(); description.runtimeDirectory = L"relative/runtime";
    status = Status();
    Expect(RbaSlProbe(&description, &status) == RbaSlInvalidArgument && std::strstr(status.reason, "absolute"), "Relative runtime path must fail");
    description.runtimeDirectory = L"C:/missing/Streamline";
    status = Status();
    Expect(RbaSlProbe(&description, &status) == RbaSlInvalidArgument && std::strstr(status.reason, "source D3D11"), "Cannot choose a different GPU without source device");
    description.reserved = 1;
    status = Status();
    Expect(RbaSlProbe(&description, &status) == RbaSlInvalidArgument && !status.available, "Unknown descriptor controls must fail");
    std::puts("Streamline probe contract tests passed; no vendor runtime required.");
}
}
int wmain(int argc, wchar_t** argv) {
    try {
        Contract();
        if (argc == 2) {
            ComPtr<ID3D11Device> device;
            D3D_FEATURE_LEVEL level{};
            Expect(SUCCEEDED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0,
                nullptr, 0, D3D11_SDK_VERSION, &device, &level, nullptr)), "D3D11 hardware device creation failed");
            auto description = Description(); description.runtimeDirectory = argv[1]; description.d3d11Device = device.Get();
            auto status = Status();
            const uint32_t result = RbaSlProbe(&description, &status);
            std::printf("Probe result=%u sdk=%u adapter=%04x:%04x fg=%u reflex=%u maxGenerated=%u mask=0x%x dynamic=%u available=%u\n%s\n",
                result, status.sdkResult, status.adapterVendorId, status.adapterDeviceId, status.featureSupported,
                status.reflexSupported, status.maxGeneratedFrames, status.queriedMultiplierMask,
                status.dynamicMfgSupported, status.available, status.reason);
            Expect(!status.available && !status.presentationReady, "Standalone discovery must never enable interpolation");
            // A real unsupported adapter is an observed result, not a test failure.
            Expect(result == RbaSlOk || result == RbaSlUnsupported, "Vendor bootstrap failed; inspect the actual SDK error");
            std::puts("Standalone vendor capability query completed. No frame generation or player validation was performed.");
        }
        return 0;
    } catch (const std::exception& exception) {
        std::fprintf(stderr, "%s\n", exception.what()); return 1;
    }
}
