using System.Collections.Generic;

namespace Orchestra.Core.Models
{
    /// <summary>
    /// Snapshot of what's already present on the machine, gathered *before*
    /// any installation is attempted. Shown to the user as a checklist so
    /// setup is transparent about what it will and won't need to do,
    /// instead of silently installing things that might already be there.
    /// </summary>
    public class SystemScanResult
    {
        public bool PythonFound { get; set; }
        public string? PythonExePath { get; set; }
        public string? PythonVersion { get; set; }

        public bool VenvExists { get; set; }
        public bool DependenciesInstalled { get; set; }

        public bool OllamaInstalled { get; set; }
        public bool OllamaRunning { get; set; }

        public List<string> InstalledModels { get; set; } = new();

        public HardwareProfile Hardware { get; set; } = new();

        public bool IsFullyReady =>
            PythonFound && VenvExists && DependenciesInstalled && OllamaInstalled && OllamaRunning;
    }
}
