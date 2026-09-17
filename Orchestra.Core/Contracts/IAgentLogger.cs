namespace Orchestra.Core.Contracts
{
    /// <summary>
    /// Logging abstraction owned by the Core domain.
    /// Allows Core services to log diagnostic and audit events without
    /// taking a dependency on the Infrastructure layer.
    /// </summary>
    public interface IAgentLogger
    {
        void LogInfo(string message);
        void LogWarning(string message);
        void LogError(string message);
    }
}
