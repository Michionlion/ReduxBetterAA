using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using ReduxBetterAA.Diagnostics;
using ReduxTestHarness;
using UnityEngine;

namespace ReduxBetterAAVisualTests
{
    // Test-only, consecutive-frame capture. Encoder backpressure slows wall time,
    // while the camera and render delta advance exactly once per recorded frame.
    [DefaultExecutionOrder(31000)]
    public sealed class ComparisonVideo : MonoBehaviour
    {
        private const int Fps = 60, FrameCount = 840;
        private AaComparison _comparison;
        private Action<double, double, double, double, bool> _orbit;
        private Action _applyCamera;
        private Process _encoder;
        private Task<string> _log;
        private Task _finish;
        private RenderTexture _target;
        private Texture2D _pixels;
        private byte[] _rgb;
        private StreamWriter _manifest;
        private string _path, _error;
        private int _startFrame, _poseFrame = -1, _frames;
        private uint _previousRender, _resets;
        private float _priorCaptureDelta;
        private bool _recording;

        internal void Begin(AaComparison comparison, string label)
        {
            if (string.IsNullOrEmpty(label) || label.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("Invalid clip label.");
            _comparison = comparison;
            if (!comparison.Running) throw new InvalidOperationException("Start and warm the live comparison first.");
            _path = Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("RBAA_QUALITY_OUTPUT")), "videos", label + ".lossless.mkv");
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            var harness = UnityEngine.Object.FindAnyObjectByType<ReduxTestHarnessMod>();
            object adapter = typeof(ReduxTestHarnessMod).GetField("_game", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(harness);
            _orbit = (Action<double, double, double, double, bool>)Delegate.CreateDelegate(typeof(Action<double, double, double, double, bool>), adapter, adapter.GetType().GetMethod("SetOrbitCamera"));
            _applyCamera = (Action)Delegate.CreateDelegate(typeof(Action), adapter, adapter.GetType().GetMethod("ApplyCameraOverride"));
            _target = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            _target.Create();
            _pixels = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
            _rgb = new byte[Screen.width * Screen.height * 3];
            _encoder = new Process { StartInfo = new ProcessStartInfo {
                FileName = Environment.GetEnvironmentVariable("RBAA_VISUAL_FFMPEG"),
                Arguments = "-hide_banner -loglevel error -y -f rawvideo -pixel_format rgb24 -video_size " + Screen.width + "x" + Screen.height +
                    " -framerate 60 -i pipe:0 -an -vf vflip -c:v libx264rgb -preset veryfast -crf 0 -threads 8 -pix_fmt rgb24 \"" + _path + ".partial.mkv\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardError = true
            }};
            _encoder.Start();
            _log = _encoder.StandardError.ReadToEndAsync();
            _manifest = new StreamWriter(_path + ".frames.csv");
            _manifest.WriteLine("video_frame,unity_frame,comparison_frame,yaw_degrees,history_resets");
            _priorCaptureDelta = Time.captureDeltaTime;
            Time.captureDeltaTime = 1f / Fps;
            _startFrame = Time.frameCount + 1;
            _resets = comparison.HistoryResets;
            _recording = true;
            StartCoroutine(CaptureFrames());
        }

        // Two seconds still, a five-second pan each way, two seconds settled.
        private static double Yaw(int frame)
        {
            double t = frame / (double)Fps;
            if (t < 2) return -10;
            if (t < 7) return -10 + (t - 2) * 4;
            if (t < 12) return 10 - (t - 7) * 4;
            return -10;
        }

        private void LateUpdate()
        {
            if (!_recording || Time.frameCount < _startFrame) return;
            _orbit(35, Yaw(_frames), 30, 55, true);
            _applyCamera();
            _poseFrame = Time.frameCount;
        }

        private IEnumerator CaptureFrames()
        {
            var end = new WaitForEndOfFrame();
            while (_recording)
            {
                yield return end;
                if (Time.frameCount < _startFrame) continue;
                RenderTexture prior = RenderTexture.active;
                try
                {
                    if (!_comparison.Running || _poseFrame != Time.frameCount ||
                        (_frames > 0 && _comparison.RenderedFrames != _previousRender + 1))
                        throw new InvalidOperationException("Comparison frames were skipped or camera pose was not applied.");
                    if (_comparison.HistoryResets != _resets)
                        throw new InvalidOperationException("Temporal history reset during the recorded camera path.");
                    _comparison.Present(_target);
                    RenderTexture.active = _target;
                    _pixels.ReadPixels(new Rect(0, 0, _target.width, _target.height), 0, 0, false);
                    _pixels.GetRawTextureData<byte>().CopyTo(_rgb);
                    _encoder.StandardInput.BaseStream.Write(_rgb, 0, _rgb.Length);
                    if (_frames == 0 || _frames == 300 || _frames == 839)
                        File.WriteAllBytes(_path + "." + _frames.ToString("D4") + ".png", ImageConversion.EncodeToPNG(_pixels));
                    _manifest.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0},{1},{2},{3:F6},{4}",
                        _frames, Time.frameCount, _comparison.RenderedFrames, Yaw(_frames), _comparison.HistoryResets));
                    _previousRender = _comparison.RenderedFrames;
                    if (++_frames == FrameCount)
                    {
                        _recording = false;
                        Time.captureDeltaTime = _priorCaptureDelta;
                        _manifest.Dispose(); _manifest = null;
                        _encoder.StandardInput.Close();
                        _finish = Task.Run(() => {
                            if (!_encoder.WaitForExit(30000)) throw new TimeoutException("Video encoder did not finish.");
                            if (_encoder.ExitCode != 0) throw new InvalidOperationException(_log.Result);
                            File.Move(_path + ".partial.mkv", _path);
                        });
                    }
                }
                catch (Exception error) { _error = error.Message; _recording = false; Time.captureDeltaTime = _priorCaptureDelta; }
                finally { RenderTexture.active = prior; }
            }
        }

        internal bool Ready()
        {
            if (_error != null) throw new InvalidOperationException(_error);
            if (_finish != null && _finish.IsFaulted) throw _finish.Exception;
            return _frames == FrameCount && _finish != null && _finish.IsCompleted;
        }

        internal string Finish()
        {
            if (!Ready()) throw new InvalidOperationException("Video not finished.");
            return _path;
        }

        private void OnDestroy()
        {
            if (_recording) Time.captureDeltaTime = _priorCaptureDelta;
            StopAllCoroutines();
            _recording = false;
            _manifest?.Dispose();
            if (_encoder != null)
            {
                try { if (!_encoder.HasExited) { _encoder.Kill(); _encoder.WaitForExit(5000); } }
                catch (InvalidOperationException) { }
                _encoder.Dispose();
            }
            if (_target != null) { _target.Release(); Destroy(_target); }
            if (_pixels != null) Destroy(_pixels);
        }
    }
}
