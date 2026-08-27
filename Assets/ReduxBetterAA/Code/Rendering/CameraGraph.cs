using System;

namespace ReduxBetterAA.Rendering
{
    [Serializable]
    public sealed class CameraGraph
    {
        public int revision;
        public string activeScene;
        public string gameState;
        public int screenWidth;
        public int screenHeight;
        public string activeCameraGroup;
        public CameraRecord[] cameras;
        public CameraStackRecord[] stacks;
        public PresenterRecord[] presenters;
    }

    [Serializable]
    public sealed class CameraRecord
    {
        public ulong instanceId;
        public string name;
        public string hierarchy;
        public string scene;
        public string role;
        public bool enabled;
        public bool activeInHierarchy;
        public string cameraType;
        public float depth;
        public float nearClipPlane;
        public float farClipPlane;
        public float fieldOfView;
        public bool orthographic;
        public float orthographicSize;
        public string clearFlags;
        public string backgroundColor;
        public int cullingMask;
        public string cullingMaskHex;
        public int eventMask;
        public string renderingPath;
        public string actualRenderingPath;
        public string depthTextureMode;
        public bool allowHdr;
        public bool allowMsaa;
        public bool allowDynamicResolution;
        public bool useOcclusionCulling;
        public string pixelRect;
        public int pixelWidth;
        public int pixelHeight;
        public TextureRecord targetTexture;
        public bool postProcessLayerPresent;
        public bool postProcessLayerEnabled;
        public string postProcessAntialiasing;
        public string postProcessCameraFlags;
        public string[] components;
        public CommandBufferRecord[] commandBuffers;
        public ulong presentationCameraId;
    }

    [Serializable]
    public sealed class CameraStackRecord
    {
        public ulong instanceId;
        public string name;
        public string type;
        public string renderSpace;
        public bool active;
        public ulong mainCameraId;
        public ulong cubemapCameraId;
        public ulong debugCameraId;
        public ulong[] renderCameraIds;
        public ulong postProcessLayerId;
        public string postProcessAntialiasing;
    }

    [Serializable]
    public sealed class PresenterRecord
    {
        public ulong instanceId;
        public string name;
        public bool active;
        public bool renderingEnabled;
        public int renderScalePercent;
        public ulong[] sourceCameraIds;
        public ulong presentationCameraId;
        public string presentationEvent;
        public string presentationCommandBuffer;
        public TextureRecord renderTarget;
        public int savedOriginalTargetCount;
    }

    [Serializable]
    public sealed class TextureRecord
    {
        public bool present;
        public ulong instanceId;
        public string name;
        public int width;
        public int height;
        public string dimension;
        public string format;
        public string graphicsFormat;
        public int depthBits;
        public int antiAliasing;
        public bool created;
    }

    [Serializable]
    public sealed class CommandBufferRecord
    {
        public string cameraEvent;
        public string[] names;
    }
}
