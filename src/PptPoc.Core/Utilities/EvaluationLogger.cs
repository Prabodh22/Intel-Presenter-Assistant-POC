using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PptPoc.Core.Configuration;

namespace PptPoc.Core.Utilities
{
    public static class EvaluationLogger
    {
        private static AppConfig? _config;
        private static readonly SemaphoreSlim _sem = new(1,1);
        private static readonly JsonSerializerOptions _opts = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        public static void Initialize(AppConfig config)
        {
            _config = config;
            try
            {
                if (_config.EvaluationLoggingEnabled && !string.IsNullOrWhiteSpace(_config.EvaluationLogPath))
                {
                    _config.EvaluationLogPath = ResolveLogPath(_config.EvaluationLogPath);
                    var dir = Path.GetDirectoryName(_config.EvaluationLogPath) ?? "logs";
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                }
            }
            catch { /* swallow */ }
        }

        private static string ResolveLogPath(string path)
        {
            return Environment.ExpandEnvironmentVariables(path)
                .Replace("%LocalAppData%", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        }

        public static async Task LogAsync(object record)
        {
            if (_config == null || !_config.EvaluationLoggingEnabled) return;
            try
            {
                string line = JsonSerializer.Serialize(record, _opts);
                await _sem.WaitAsync().ConfigureAwait(false);
                try
                {
                    await File.AppendAllTextAsync(_config.EvaluationLogPath, line + Environment.NewLine).ConfigureAwait(false);
                }
                finally { _sem.Release(); }
            }
            catch
            {
                // Never throw from logger — logging must be non-fatal.
            }
        }
    }
}
