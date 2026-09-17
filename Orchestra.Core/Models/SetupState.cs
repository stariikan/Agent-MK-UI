using System;
using System.Collections.Generic;

namespace Orchestra.Core.Models
{
    /// <summary>
    /// Persisted record of what first-run setup has already completed, so
    /// re-launching the app (or re-running the setup wizard) can skip work
    /// that's already done. Stored as JSON under
    /// %LOCALAPPDATA%\AgentMK\setup_state.json -- deliberately independent
    /// of the SQLite chat history, since setup must work before any Python
    /// environment exists.
    /// </summary>
    public class SetupState
    {
        public Dictionary<string, bool> CompletedSteps { get; set; } = new();

        public string? ChosenModel { get; set; }

        /// <summary>Agent behavior preset: auto, fast, or deep. Auto adapts to model size.</summary>
        public string AgentProfile { get; set; } = "auto";

        /// <summary>Absolute path to the Python interpreter inside the venv created during setup.</summary>
        public string? VenvPythonPath { get; set; }

        public DateTimeOffset? LastRun { get; set; }

        public bool IsStepComplete(string key) =>
            CompletedSteps.TryGetValue(key, out var done) && done;

        public void MarkStepComplete(string key) => CompletedSteps[key] = true;

        /// <summary>
        /// Setup is considered fully complete once we have a working venv
        /// Python and a chosen model recorded. Individual step flags exist
        /// so a partially-failed run can resume instead of starting over.
        /// </summary>
        public bool IsSetupComplete() =>
            !string.IsNullOrWhiteSpace(VenvPythonPath)
            && !string.IsNullOrWhiteSpace(ChosenModel)
            && IsStepComplete("deps_installed");
    }
}
