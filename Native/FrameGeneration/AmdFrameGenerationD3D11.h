#pragma once
#include "AmdFrameGeneration.h"

enum RbaAmdFgColorTransfer : uint32_t { RbaAmdFgSrgbEncoded = 1 };
struct RbaAmdFgD3D11CreateDesc {
    uint32_t structSize, abiVersion;
    const wchar_t* runtimeDirectory;
    void* d3d11Device; // ID3D11Device from the actual scene renderer.
    uint32_t renderWidth, renderHeight, displayWidth, displayHeight;
    uint32_t flags, preferCompatibility;
    uint32_t colorTransfer; // RbaAmdFgSrgbEncoded only; explicit color encoding.
    uint32_t reserved;
};

// Same-adapter D3D11 → shared D3D12 → actual AMD FG → D3D11 texture transport.
// Independent of Present/UI. No implicit conversion, resize, or color transforms.
RBA_AMD_FG_API uint32_t RbaAmdFgD3D11Create(const RbaAmdFgD3D11CreateDesc* desc,
    void** transport, RbaAmdFgStatus* status);
// Reuses frame metadata, but color/depth/motion/output are ID3D11Texture2D pointers.
// Color/output: RGBA8, depth: R32 float, motion: RG16 float. Compatible typeless
// storage is accepted; color bytes must use the explicitly declared sRGB encoding.
// Dispatch and Poll must run on the same D3D11 render thread. Serialize all calls.
// On GpuBusy, retain/never modify inputs or output until Poll or Destroy returns Ok.
// A native/device failure poisons the context: no output is valid and the caller
// must retain/never modify all frame textures until Destroy returns Ok. Poll errors
// never release this obligation. A failed fence signal on a live device can leave
// retirement unprovable: Destroy stays Busy and the transport/textures remain held
// until device removal. COM reference retention alone does not prevent overwrites.
// InvalidArgument submits no new frame; an earlier pending frame keeps its lease.
RBA_AMD_FG_API uint32_t RbaAmdFgD3D11Dispatch(void* transport,
    const RbaAmdFgFrame* frame, RbaAmdFgStatus* status);
// Additive equivalent of the D3D12 HUD-less path; all five resources are D3D11
// textures on the source device. HUD-less color has the same display extent and
// RGBA8/sRGB metadata as final color, must differ from final color/output, and
// must remain unmodified for the entire frame lease (including busy/error paths).
RBA_AMD_FG_API uint32_t RbaAmdFgD3D11DispatchWithHudLess(void* transport,
    const RbaAmdFgFrame* frame, void* hudLessColor, RbaAmdFgStatus* status);
// Completes a late generated-output copy on the original render thread.
RBA_AMD_FG_API uint32_t RbaAmdFgD3D11Poll(void* transport, RbaAmdFgStatus* status);
// Can run off the render thread when other calls have stopped. Drains GPU work
// and discards an output not yet copied. Busy leaves the entire transport alive.
RBA_AMD_FG_API uint32_t RbaAmdFgD3D11Destroy(void* transport);
