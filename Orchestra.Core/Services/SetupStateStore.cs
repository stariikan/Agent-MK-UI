using System;
using System.IO;
using System.Text.Json;
using Orchestra.Core.Contracts;
using Orchestra.Core.Models;

namespace Orchestra.Core.Services
{
    /// <summary>
    /// Reads/writes the small JSON file that tracks first-run setup
    /// progress. Deliberately simple (no DB dependency) since it has to
    /// work before any Python/SQLite environment exists.
    /// </summary>
    public class SetupStateStore
    {
        private readonly IAgentLogger _logger;
        private readonly string _stateFilePath;
        private static readonly object _lock = new();

        public SetupStateStore(IAgentLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string folder = Path.Combine(appData, "AgentMK");
            Directory.CreateDirectory(folder);
            _stateFilePath = Path.Combine(folder, "setup_state.json");
        }

        public SetupState Load()
        {
            lock (_lock)
            {
                if (!File.Exists(_stateFilePath))
                {
                    return new SetupState();
                }

                try
                {
                    string json = File.ReadAllText(_stateFilePath);
                    var state = JsonSerializer.Deserialize<SetupState>(json);
                    return state ?? new SetupState();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Setup state file was unreadable, starting fresh: {ex.Message}");
                    return new SetupState();
                }
            }
        }

        public void Save(SetupState state)
        {
            lock (_lock)
            {
                state.LastRun = DateTimeOffset.Now;
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(state, options);

                string tempPath = _stateFilePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Copy(tempPath, _stateFilePath, overwrite: true);
                File.Delete(tempPath);
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                if (File.Exists(_stateFilePath))
                {
                    File.Delete(_stateFilePath);
                }
            }
        }

        /// <summary>
        /// Checks both the persisted flags AND that the recorded venv
        /// Python interpreter actually still exists on disk right now.
        /// The flags alone aren't trustworthy: the venv can disappear out
        /// from under a "complete" state (a rebuild wiping bin\, a user
        /// deleting it, antivirus quarantine, etc.), and trusting a stale
        /// flag meant the app would try to launch a nonexistent
        /// interpreter and crash instead of just re-running setup. This
        /// is the fix for exactly that reported crash.
        /// </summary>
        public bool IsSetupComplete()
        {
            var state = Load();
            if (!state.IsSetupComplete())
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(state.VenvPythonPath) || !File.Exists(state.VenvPythonPath))
            {
                _logger.LogWarning(
                    $"Setup state says complete, but the venv Python at " +
                    $"'{state.VenvPythonPath}' no longer exists (likely removed by a " +
                    "rebuild/clean or deleted manually). Treating setup as incomplete.");
                return false;
            }

            return true;
        }
    }
}
