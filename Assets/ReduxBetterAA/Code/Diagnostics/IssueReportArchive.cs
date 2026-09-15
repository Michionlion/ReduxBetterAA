using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using Newtonsoft.Json;

namespace ReduxBetterAA.Diagnostics
{
    [Serializable]
    internal sealed class IssueReportManifest
    {
        public int schemaVersion = 4;
        public string id;
        public string capturedUtc;
        public string status;
        public int inputFrame = -1;
        public int outputFrame = -1;
        public int screenshotFrame = -1;
        public int capabilitiesFrame = -1;
        public string capabilitiesStage = "unavailable";
        public bool capabilitiesMatchOutput;
        public int motionMatrixFrame = -1;
        public bool motionMatrixMatchesOutput;
        public string camera;
        public string inputStage = "unavailable";
        public string outputStage = "unavailable";
        public string note = "EXR files contain floating-point samples; PNGs are previews. " +
            "Owned targets are observed after the AA pass. Vendor-internal " +
            "history is opaque and cannot be exported. " +
            "Screenshots include visible UI. " +
            "Capture stalls are expected and are not performance measurements.";
        public readonly List<BufferCaptureRecord> buffers = new List<BufferCaptureRecord>();
        public readonly List<InputBindingRecord> inputBindings = new List<InputBindingRecord>();
        public readonly List<string> errors = new List<string>();
        public readonly List<ReportFileRecord> files = new List<ReportFileRecord>();
    }

    [Serializable]
    internal sealed class InputBindingRecord
    {
        public string binding;
        public int frame;
        public bool requestedByCamera;
        public int width;
        public int height;
        public string format;
        public float[] cpuGlobalTexelSize;
        public bool cpuGlobalDimensionsMatchTexture;
        public string provenance = "Texture dimensions are from the bound input before diagnostic blits. " +
            "The CPU global texel-size vector is a separate observation and may describe another camera; " +
            "matching dimensions alone do not establish its orientation or ownership.";
    }

    [Serializable]
    internal sealed class BufferCaptureRecord
    {
        public string name;
        public int frame;
        public int width;
        public int height;
        public string format;
        public string status;
        public string rawFile;
        public string previewFile;
        public string error;
    }

    [Serializable]
    internal sealed class ReportFileRecord
    {
        public string path;
        public long bytes;
        public string sha256;
    }

    // Pure file work; invoked on a worker only after every GPU readback/file write finishes.
    internal static class IssueReportArchive
    {
        public static string Create(string directory, IssueReportManifest manifest)
        {
            string zipPath = directory + ".zip";
            string partialPath = zipPath + ".partial";
            bool ownsPartial = false;
            manifest.files.Clear();
            foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            {
                string relative = file.Substring(directory.Length + 1).Replace('\\', '/');
                if (relative == "manifest.json")
                    continue;
                manifest.files.Add(new ReportFileRecord
                {
                    path = relative,
                    bytes = new FileInfo(file).Length,
                    sha256 = HashFile(file)
                });
            }
            manifest.files.Sort((left, right) => string.CompareOrdinal(left.path, right.path));
            File.WriteAllText(Path.Combine(directory, "manifest.json"),
                JsonConvert.SerializeObject(manifest, Formatting.Indented));
            try
            {
                using (var stream = new FileStream(partialPath, FileMode.CreateNew))
                {
                    ownsPartial = true;
                    using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                    {
                        foreach (ReportFileRecord file in manifest.files)
                            AddFile(zip, directory, file.path);
                        AddFile(zip, directory, "manifest.json");
                    }
                }
                File.Move(partialPath, zipPath);
                return zipPath;
            }
            catch
            {
                if (ownsPartial && File.Exists(partialPath))
                    File.Delete(partialPath);
                throw;
            }
        }

        internal static string HashFile(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private static void AddFile(ZipArchive zip, string directory, string relative)
        {
            var entry = zip.CreateEntry(relative, CompressionLevel.Fastest);
            using (var input = File.OpenRead(Path.Combine(directory, relative)))
            using (var output = entry.Open())
                input.CopyTo(output);
        }
    }
}
