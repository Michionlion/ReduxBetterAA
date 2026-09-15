using System;
using ReduxBetterAA.Configuration;
using UnityEngine;

namespace ReduxBetterAA.Rendering
{
    // Populate only from the owned presenter's completed hidden-context probes;
    // neither DLL presence nor the AA/upscaler capability proves FG support.
    internal static class FrameGenerationAvailability
    {
        private static FrameGenerationSupport[] Providers = Array.Empty<FrameGenerationSupport>();
        internal static string UnavailableReason { get; private set; } = "The optional frame-generation runtime is not installed.";
        internal static uint Revision { get; private set; }
        internal static event Action ChoicesChanged;
        private static uint _nvidiaMask;
        private static uint _amdBestMask, _amdBestFamily, _amdCompatibilityMask, _amdCompatibilityFamily;
        private static ulong _amdBestVersion, _amdCompatibilityVersion;

        internal static string[] BuildChoices() =>
            FrameGenerationPolicy.BuildChoices(Providers, SystemInfo.graphicsDeviceType);

        internal static string[] BuildMenuChoices()
        {
            var choices=BuildChoices();
            string requested=FrameGenerationPolicy.Label(Requested);
            if (Array.IndexOf(choices,requested) >= 0) return choices;
            // Display a saved request while unavailable; diagnostics distinguish
            // its effective fallback from actual supported choices.
            Array.Resize(ref choices,choices.Length+1);
            choices[choices.Length-1]=requested;
            return choices;
        }

        internal static FrameGenerationSelection Resolve(FrameGenerationMode requested) =>
            FrameGenerationPolicy.Resolve(requested, Providers, SystemInfo.graphicsDeviceType);

        internal static FrameGenerationMode Requested { get; private set; }
        internal static FrameGenerationMode Selected => Resolve(Requested).Selected;
        internal static bool Active { get; private set; }
        internal static void SetRequested(string value)
        {
            var requested=FrameGenerationPolicy.Parse(value);
            if (Requested == requested) return;
            Requested=requested; ++Revision; ChoicesChanged?.Invoke();
        }

        // The runtime publishes this only after its owned hidden-context probe
        // has retired successfully. A DLL presence or GPU model is insufficient.
        internal static void SetNvidiaSupport(uint mask)
        {
            mask &= 0x1cu;
            if (_nvidiaMask == mask) return;
            _nvidiaMask = mask;
            Rebuild();
        }
        internal static void SetAmdSupport(uint bestMask,uint bestFamily,ulong bestVersion,
            uint compatibilityMask,uint compatibilityFamily,ulong compatibilityVersion)
        {
            bestMask &= 4u; compatibilityMask &= 4u;
            if (bestFamily != 3 && bestFamily != 4) bestMask=0;
            if (compatibilityFamily != 3) compatibilityMask=0;
            if (_amdBestMask == bestMask && _amdBestFamily == bestFamily && _amdBestVersion == bestVersion &&
                _amdCompatibilityMask == compatibilityMask && _amdCompatibilityFamily == compatibilityFamily &&
                _amdCompatibilityVersion == compatibilityVersion) return;
            _amdBestMask=bestMask; _amdBestFamily=bestFamily; _amdBestVersion=bestVersion;
            _amdCompatibilityMask=compatibilityMask; _amdCompatibilityFamily=compatibilityFamily;
            _amdCompatibilityVersion=compatibilityVersion;
            Rebuild();
        }
        private static void Rebuild()
        {
            var providers=new System.Collections.Generic.List<FrameGenerationSupport>(3);
            if (_nvidiaMask != 0)
            {
                var caps = new FrameGenerationCapabilities("NVIDIA DLSS", "Streamline 2.14.1", true,
                    UnityEngine.Rendering.GraphicsDeviceType.Direct3D11, FrameGenerationColorDomain.DisplaySrgb,
                    FrameGenerationMotionUnits.NormalizedUv, _nvidiaMask, true, false);
                providers.Add(new FrameGenerationSupport(FrameGenerationProviderKind.NvidiaDlss, in caps, true));
            }
            AddAmd(providers,_amdBestMask,_amdBestFamily,_amdBestVersion);
            if (_amdBestFamily != 3 || _amdBestMask == 0)
                AddAmd(providers,_amdCompatibilityMask,_amdCompatibilityFamily,_amdCompatibilityVersion);
            Providers=providers.ToArray();
            ++Revision;
            ChoicesChanged?.Invoke();
        }
        private static void AddAmd(System.Collections.Generic.List<FrameGenerationSupport> providers,uint mask,uint family,ulong version)
        {
            if (mask == 0 || version == 0) return;
            var kind=family == 4 ? FrameGenerationProviderKind.AmdFsr4 : FrameGenerationProviderKind.AmdFsr31;
            var caps=new FrameGenerationCapabilities(family == 4 ? "AMD FSR 4" : "AMD FSR 3.1",
                "SDK provider 0x"+version.ToString("x"),true,UnityEngine.Rendering.GraphicsDeviceType.Direct3D11,
                FrameGenerationColorDomain.DisplaySrgb,FrameGenerationMotionUnits.NormalizedUv,mask,true,false);
            providers.Add(new FrameGenerationSupport(kind,in caps,true));
        }
        internal static void SetRuntimeState(bool active, string reason)
        { Active = active; UnavailableReason = reason ?? string.Empty; }
    }
}
