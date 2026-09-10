using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace ReduxBetterAAVisualTests
{
    // Test-only presented-frame recording. Encode through a pipe, without a PNG sequence.
    internal sealed class SampledVideo : IDisposable
    {
        private Process _encoder;
        private Task<string> _encoderLog;
        private RenderTexture _screen;
        private RenderTexture _scaled;
        private Texture2D _pixels;
        private string _output;
        private Exception _error;
        private bool _pending;
        public int Frames { get; private set; }

        public void Start()
        {
            string ffmpeg = Environment.GetEnvironmentVariable("RBAA_VISUAL_FFMPEG");
            _output = Environment.GetEnvironmentVariable("RBAA_VISUAL_VIDEO");
            if (!File.Exists(ffmpeg) || string.IsNullOrEmpty(_output))
                throw new InvalidOperationException("Run this capture through tools/Run-VisualTests.ps1 with FFmpeg installed.");
            Directory.CreateDirectory(Path.GetDirectoryName(_output));
            int width = Screen.width / 2 * 2;
            int height = Screen.height / 2 * 2;
            _screen = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB32);
            _scaled = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
            _screen.Create();
            _scaled.Create();
            _pixels = new Texture2D(width, height, TextureFormat.RGB24, false);
            _encoder = new Process { StartInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = "-hide_banner -loglevel error -y -f rawvideo -pixel_format rgb24 -video_size " +
                    width + "x" + height + " -framerate 30 -i pipe:0 -an -c:v libx264 " +
                    "-preset medium -crf 16 -pix_fmt yuv420p -movflags +faststart \"" + _output + ".partial.mp4\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardError = true
            }};
            _encoder.Start();
            _encoderLog = _encoder.StandardError.ReadToEndAsync();
        }

        public void QueueFrame(MonoBehaviour owner)
        {
            if (_pending) throw new InvalidOperationException("Wait for the previous video frame.");
            if (Frames >= 180) throw new InvalidOperationException("The compact video is capped at 180 frames.");
            _pending = true;
            owner.StartCoroutine(CaptureFrame());
        }

        public bool FrameReady()
        {
            if (_error != null) throw new InvalidOperationException("Video capture failed: " + _error.Message);
            return !_pending;
        }

        private IEnumerator CaptureFrame()
        {
            yield return new WaitForEndOfFrame();
            RenderTexture previous = RenderTexture.active;
            try
            {
                ScreenCapture.CaptureScreenshotIntoRenderTexture(_screen);
                Graphics.Blit(_screen, _scaled);
                RenderTexture.active = _scaled;
                _pixels.ReadPixels(new Rect(0, 0, _scaled.width, _scaled.height), 0, 0, false);
                byte[] rgb = _pixels.GetRawTextureData();
                _encoder.StandardInput.BaseStream.Write(rgb, 0, rgb.Length);
                Frames++;
            }
            catch (Exception error) { _error = error; }
            finally { RenderTexture.active = previous; _pending = false; }
        }

        public string Finish()
        {
            if (!FrameReady() || Frames != 180) throw new InvalidOperationException("Expected 180 completed video frames.");
            _encoder.StandardInput.Close();
            if (!_encoder.WaitForExit(30000)) throw new TimeoutException("FFmpeg did not finish within 30 seconds.");
            if (_encoder.ExitCode != 0) throw new InvalidOperationException("FFmpeg: " + _encoderLog.Result);
            File.Move(_output + ".partial.mp4", _output);
            return _output;
        }

        public void Dispose()
        {
            if (_encoder != null)
            {
                try { if (!_encoder.HasExited) { _encoder.Kill(); _encoder.WaitForExit(5000); } }
                catch (InvalidOperationException) { /* Process.Start failed before an encoder existed. */ }
                finally { _encoder.Dispose(); _encoder = null; }
            }
            if (_screen != null) { _screen.Release(); UnityEngine.Object.Destroy(_screen); _screen = null; }
            if (_scaled != null) { _scaled.Release(); UnityEngine.Object.Destroy(_scaled); _scaled = null; }
            if (_pixels != null) { UnityEngine.Object.Destroy(_pixels); _pixels = null; }
        }
    }
}
