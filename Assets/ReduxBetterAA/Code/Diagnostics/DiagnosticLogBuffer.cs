using System;
using System.Text;
using UnityEngine;

namespace ReduxBetterAA.Diagnostics
{
    internal sealed class DiagnosticLogBuffer : IDisposable
    {
        private readonly string[] _messages = new string[128];
        private readonly string[] _stacks = new string[128];
        private int _next;
        private int _count;

        public DiagnosticLogBuffer() { Application.logMessageReceived += Receive; }

        private void Receive(string message, string stack, LogType type)
        {
            if (message == null || message.IndexOf("[ReduxBetterAA/", StringComparison.Ordinal) < 0)
                return;
            _messages[_next] = message;
            _stacks[_next] = type == LogType.Exception || type == LogType.Error ? stack : null;
            _next = (_next + 1) % _messages.Length;
            _count = Math.Min(_count + 1, _messages.Length);
        }

        public string Export()
        {
            var text = new StringBuilder("Last 128 Better AA messages only; no full player logs or save files.\n");
            for (int index = 0; index < _count; index++)
            {
                int slot = (_next - _count + index + _messages.Length) % _messages.Length;
                text.AppendLine(_messages[slot]);
                if (!string.IsNullOrEmpty(_stacks[slot])) text.AppendLine(_stacks[slot]);
            }
            string result = text.ToString();
            string[] paths = { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                System.IO.Path.GetDirectoryName(Application.dataPath),
                System.IO.Path.GetDirectoryName(typeof(DiagnosticLogBuffer).Assembly.Location) };
            foreach (string path in paths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                result = result.Replace(path, "<local-path>").Replace(path.Replace('\\', '/'), "<local-path>");
            }
            return result;
        }

        public void Dispose() { Application.logMessageReceived -= Receive; }
    }
}
