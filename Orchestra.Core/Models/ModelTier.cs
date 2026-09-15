using System.Collections.Generic;

namespace Orchestra.Core.Models
{
    /// <summary>
    /// One entry in the ladder of Ollama models this app knows how to
    /// recommend, ordered from lightest to heaviest.
    /// </summary>
    public class ModelTier
    {
        public string Name { get; init; } = string.Empty;
        public string OllamaTag { get; init; } = string.Empty;
        public double MinVramGb { get; init; }
        public double MinRamGbCpuOnly { get; init; }
        public double ApproxDiskGb { get; init; }
        public string Description { get; init; } = string.Empty;
    }

    /// <summary>
    /// The result of matching a <see cref="HardwareProfile"/> against the
    /// model tier ladder.
    /// </summary>
    public class ModelRecommendation
    {
        public HardwareProfile Hardware { get; init; } = new();
        public string RecommendedTier { get; init; } = string.Empty;
        public string RecommendedModel { get; init; } = string.Empty;
        public double ApproxDiskGb { get; init; }
        public string Reason { get; init; } = string.Empty;
        public List<string> Notes { get; init; } = new();
        public List<ModelTier> AllTiers { get; init; } = new();
    }
}
