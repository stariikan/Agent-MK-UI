using Orchestra.Core.Models;
namespace Orchestra.Core.Services
{
    public class ModelRecommender
    {
        public static readonly IReadOnlyList<ModelTier> Tiers = new List<ModelTier>
    {
        new()
        {
            Name = "tiny",
    OllamaTag = "qwen2.5-coder:1.5b",
    MinVramGb = 0,
    MinRamGbCpuOnly = 4,
    ApproxDiskGb = 1.0,
    Description = "Runs on almost anything, including CPU-only laptops. " +
    "Best for quick edits, not complex reasoning.",
},

new()

{

Name = "small",

OllamaTag = "qwen2.5-coder:3b",

MinVramGb = 4,

MinRamGbCpuOnly = 8,

ApproxDiskGb = 2.0,

Description = "Good balance for low-end GPUs (4GB) or 8GB+ RAM CPU-only machines.",

},

new()

{

Name = "medium",

OllamaTag = "qwen2.5-coder:7b",

MinVramGb = 6,

MinRamGbCpuOnly = 16,

ApproxDiskGb = 4.5,

Description = "Solid general coding assistant. Comfortable on 6-8GB GPUs " +

"or 16GB+ RAM CPU-only.",

},

new()

{

Name = "large",

OllamaTag = "qwen2.5-coder:14b",

MinVramGb = 10,

MinRamGbCpuOnly = 32,

ApproxDiskGb = 9.0,

Description = "Noticeably stronger reasoning/code quality. Wants a 10GB+ GPU, " +

"or 32GB+ RAM if running on CPU (will be slow).",

},

new()

{

Name = "xlarge",

OllamaTag = "qwen2.5-coder:32b",

MinVramGb = 20,

MinRamGbCpuOnly = 64,

ApproxDiskGb = 19.0,

Description = "Best local quality this app recommends by default. Needs a " +

"20GB+ GPU (e.g. RTX 4090/5090, A6000) or a 64GB+ RAM machine " +

"(CPU inference will be slow).",

},

};



        public ModelRecommendation Recommend(HardwareProfile hardware, double? overrideRamGb = null, double? overrideVramGb = null)

        {

            var notes = new List<string>();



            double effectiveRam = overrideRamGb ?? hardware.RamGb;

            double? effectiveVram = overrideVramGb ?? hardware.VramGb;



            if ((hardware.GpuVendor is "amd" or "intel" or "unknown") && hardware.VramGb != null)

            {

                notes.Add("Dedicated VRAM for non-NVIDIA GPUs is detected via Windows WMI, " +

                "which is often inaccurate. Treat this figure as a rough guess.");

            }



            if (hardware.VramIsApproximate && effectiveVram == null)

            {

                notes.Add("GPU VRAM could not be reliably detected (looked like the known " +

                "WMI reporting bug), so this recommendation is based on system RAM " +

                "only, assuming CPU inference.");

            }



            bool hasUsableGpu = effectiveVram is > 0;



            ModelTier chosen = Tiers[0];

            foreach (var tier in Tiers)

            {

                if (hasUsableGpu)

                {

                    if (effectiveVram!.Value >= tier.MinVramGb) chosen = tier;

                }

                else

                {

                    if (effectiveRam >= tier.MinRamGbCpuOnly) chosen = tier;

                }

            }



            string reason;

            if (hasUsableGpu)

            {

                reason = $"Selected based on {effectiveVram:F1}GB of GPU VRAM ({hardware.GpuName ?? "unknown GPU"}).";

            }

            else

            {

                reason = $"No usable dedicated GPU detected; selected based on {effectiveRam:F1}GB " +

                "of system RAM (CPU inference will be slower than GPU inference).";

                notes.Add("Running a coding LLM on CPU works but is significantly slower than " +

                "GPU inference. Consider a smaller tier for a more responsive experience.");

            }



            return new ModelRecommendation

            {

                Hardware = hardware,

                RecommendedTier = chosen.Name,

                RecommendedModel = chosen.OllamaTag,

                ApproxDiskGb = chosen.ApproxDiskGb,

                Reason = reason,

                Notes = notes,

                AllTiers = Tiers.ToList(),

            };

        }

    }
}

