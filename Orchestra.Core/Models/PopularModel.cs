using System.Collections.Generic;

namespace Orchestra.Core.Models
{
    /// <summary>
    /// One entry in the curated list of well-known Ollama models shown in
    /// the setup wizard as an alternative to the single auto-recommendation
    /// -- lets the user browse by name/parameter count and pick something
    /// themselves instead of trusting the heuristic blindly.
    /// </summary>
    public class PopularModel
    {
        public string DisplayName { get; init; } = string.Empty;
        public string OllamaTag { get; init; } = string.Empty;
        public string ParamSize { get; init; } = string.Empty;
        public double ApproxVramGb { get; init; }
        public string Description { get; init; } = string.Empty;

        /// <summary>Ollama's library page for this model family (lists all available sizes/quantizations).</summary>
        public string LibraryUrl { get; init; } = string.Empty;
    }

    /// <summary>
    /// A small, hand-picked list, not exhaustive. The full catalog lives at
    /// https://ollama.com/library -- treat this as "good defaults to start
    /// from," not the only options. Sizes/VRAM figures are approximate
    /// (4-bit quantization assumed) and can shift as Ollama updates tags.
    /// </summary>
    public static class PopularModelCatalog
    {
        public static readonly IReadOnlyList<PopularModel> Models = new List<PopularModel>
        {
            // --- DEEPSEEK FAMILY ---
            new()
            {
                DisplayName = "DeepSeek R1 (1.5B Distill)",
                OllamaTag = "deepseek-r1:1.5b",
                ParamSize = "1.5B",
                ApproxVramGb = 1.5,
                Description = "Tiny distilled reasoning model trained on Qwen 2.5.",
            },
            new()
            {
                DisplayName = "DeepSeek R1 (7B Distill)",
                OllamaTag = "deepseek-r1:7b",
                ParamSize = "7B",
                ApproxVramGb = 5.5,
                Description =
                    "Reasoning-specialized distilled model with explicit chain-of-thought scratchpad.",
            },
            new()
            {
                DisplayName = "DeepSeek R1 (8B Distill)",
                OllamaTag = "deepseek-r1:8b",
                ParamSize = "8B",
                ApproxVramGb = 6.0,
                Description = "Llama-3.1 based distillation for math and logic tasks.",
            },
            new()
            {
                DisplayName = "DeepSeek R1 (14B Distill)",
                OllamaTag = "deepseek-r1:14b",
                ParamSize = "14B",
                ApproxVramGb = 10.5,
                Description = "Advanced reasoning logic based on Qwen-14B distillation.",
            },
            new()
            {
                DisplayName = "DeepSeek R1 (32B Distill)",
                OllamaTag = "deepseek-r1:32b",
                ParamSize = "32B",
                ApproxVramGb = 20.0,
                Description = "Massive reasoning capabilities for 24GB hardware setups.",
            },
            new()
            {
                DisplayName = "DeepSeek Coder V2 (16B)",
                OllamaTag = "deepseek-coder-v2:16b",
                ParamSize = "16B (MoE)",
                ApproxVramGb = 10.0,
                Description = "Mixture-of-Experts architecture. Highly efficient code synthesis.",
            },
            // --- QWEN 3 GENERAL FAMILY (Research, Vision & Logic) ---
            new()
            {
                DisplayName = "Qwen 3 (4B)",
                OllamaTag = "qwen3:4b",
                ParamSize = "4B",
                ApproxVramGb = 3.5,
                Description =
                    "Incredibly capable tiny model that rivals older 70B models in pure logic. Excellent for 4GB VRAM.",
            },
            new()
            {
                DisplayName = "Qwen 3 (8B)",
                OllamaTag = "qwen3:8b",
                ParamSize = "8B",
                ApproxVramGb = 6.0,
                Description =
                    "The ultimate daily-driver for research and chat. Supports thinking/unthinking modes.",
            },
            new()
            {
                DisplayName = "Qwen 3 (14B)",
                OllamaTag = "qwen3:14b",
                ParamSize = "14B",
                ApproxVramGb = 10.5,
                Description =
                    "Advanced reasoning capabilities, surpassing older distilled models on math and logic.",
            },
            new()
            {
                DisplayName = "Qwen 3 (30B MoE)",
                OllamaTag = "qwen3:30b",
                ParamSize = "30B (MoE)",
                ApproxVramGb = 15.0,
                Description =
                    "Highly efficient Mixture-of-Experts architecture. Delivers 30B performance while using less VRAM.",
            },
            new()
            {
                DisplayName = "Qwen 3 (32B)",
                OllamaTag = "qwen3:32b",
                ParamSize = "32B",
                ApproxVramGb = 20.0,
                Description =
                    "Unmatched human preference alignment and role-playing. Fits beautifully on 24GB cards.",
            },
            // --- QWEN 3 CODER FAMILY (2026 Flagship Coding Stack) ---
            new()
            {
                DisplayName = "Qwen3-Coder 30B",
                OllamaTag = "qwen3-coder:30b",
                ParamSize = "30B",
                ApproxVramGb = 22.0,
                Description =
                    "Features 30.5B total and 3.3B active parameters using a fast MoE architecture. Built with a 256k context window, it efficiently handles large codebases and complex multi-file agentic coding tasks locally on 24GB VRAM or 32GB unified memory systems.",
            },
            // --- META LLAMA 3.1 & 3.2 FAMILY ---
            new()
            {
                DisplayName = "Llama 3.2 (1B)",
                OllamaTag = "llama3.2:1b",
                ParamSize = "1B",
                ApproxVramGb = 1.5,
                Description = "Meta's smallest edge model. Fast text processing.",
            },
            new()
            {
                DisplayName = "Llama 3.2 (3B)",
                OllamaTag = "llama3.2:3b",
                ParamSize = "3B",
                ApproxVramGb = 2.8,
                Description =
                    "Compact multi-lingual edge model optimized for low-latency reasoning.",
            },
            new()
            {
                DisplayName = "Llama 3.1 (8B)",
                OllamaTag = "llama3.1:8b",
                ParamSize = "8B",
                ApproxVramGb = 6.0,
                Description =
                    "Meta's flagship mid-sized general model with 128K native context support.",
            },
            new()
            {
                DisplayName = "Llama 3.1 (70B)",
                OllamaTag = "llama3.1:70b",
                ParamSize = "70B",
                ApproxVramGb = 40.0,
                Description =
                    "Massive general intelligence. Requires dual 24GB GPUs or heavy CPU offloading.",
            },
            // --- GOOGLE GEMMA 2 FAMILY ---
            new()
            {
                DisplayName = "Gemma 2 (2B)",
                OllamaTag = "gemma2:2b",
                ParamSize = "2B",
                ApproxVramGb = 2.0,
                Description =
                    "Google's lightweight model. Exceptional benchmark throughput for small memory budgets.",
            },
            new()
            {
                DisplayName = "Gemma 2 (9B)",
                OllamaTag = "gemma2:9b",
                ParamSize = "9B",
                ApproxVramGb = 7.0,
                Description =
                    "High-performing instruction following; punches well above its weight class.",
            },
            new()
            {
                DisplayName = "Gemma 2 (27B)",
                OllamaTag = "gemma2:27b",
                ParamSize = "27B",
                ApproxVramGb = 18.0,
                Description =
                    "Google's heavy lifter. Extremely dense reasoning for 24GB VRAM configurations.",
            },
            // --- MICROSOFT PHI FAMILY ---
            new()
            {
                DisplayName = "Phi-3.5 Mini (3.8B)",
                OllamaTag = "phi3.5:3.8b",
                ParamSize = "3.8B",
                ApproxVramGb = 3.2,
                Description =
                    "Trained heavily on synthetic textbook data. High reasoning-to-parameter ratio.",
            },
            new()
            {
                DisplayName = "Phi-3 Medium (14B)",
                OllamaTag = "phi3:14b",
                ParamSize = "14B",
                ApproxVramGb = 10.0,
                Description = "Scaled up synthetic data training for complex logic.",
            },
            // --- MISTRAL & CODESTRAL FAMILY ---
            new()
            {
                DisplayName = "Mistral (7B)",
                OllamaTag = "mistral:7b",
                ParamSize = "7B",
                ApproxVramGb = 5.5,
                Description =
                    "Proven baseline general-purpose model with sliding-window attention.",
            },
            new()
            {
                DisplayName = "Mistral Nemo (12B)",
                OllamaTag = "mistral-nemo:12b",
                ParamSize = "12B",
                ApproxVramGb = 8.5,
                Description = "128k context length built in collaboration with NVIDIA.",
            },
            new()
            {
                DisplayName = "Codestral (22B)",
                OllamaTag = "codestral:22b",
                ParamSize = "22B",
                ApproxVramGb = 15.0,
                Description =
                    "Mistral's dedicated code generation model covering 80+ programming languages.",
            },
            new()
            {
                DisplayName = "Mixtral (8x7B)",
                OllamaTag = "mixtral:8x7b",
                ParamSize = "47B (MoE)",
                ApproxVramGb = 26.0,
                Description =
                    "Sparse Mixture-of-Experts. Fast inference but requires significant memory footprint.",
            },
            // --- SPECIALTY & VISION MODELS ---
            new()
            {
                DisplayName = "Llava (7B Vision)",
                OllamaTag = "llava:7b",
                ParamSize = "7B",
                ApproxVramGb = 6.0,
                Description =
                    "Llama-based multimodal model capable of analyzing images and visual context.",
            },
            new()
            {
                DisplayName = "Llava (13B Vision)",
                OllamaTag = "llava:13b",
                ParamSize = "13B",
                ApproxVramGb = 9.5,
                Description = "Higher resolution multimodal analysis for detailed image querying.",
            },
            new()
            {
                DisplayName = "Qwen 2.5 Math (7B)",
                OllamaTag = "qwen2.5-math:7b",
                ParamSize = "7B",
                ApproxVramGb = 5.5,
                Description =
                    "Specialized variant fine-tuned exclusively for complex mathematical problem solving.",
            },
            new()
            {
                DisplayName = "CodeGemma (7B)",
                OllamaTag = "codegemma:7b",
                ParamSize = "7B",
                ApproxVramGb = 5.5,
                Description =
                    "Google's coding variant of Gemma. Strong infill and completion capabilities.",
            },
            new()
            {
                DisplayName = "StarCoder 2 (7B)",
                OllamaTag = "starcoder2:7b",
                ParamSize = "7B",
                ApproxVramGb = 5.5,
                Description = "Trained on The Stack v2. Excellent for standard code repositories.",
            },
        };
    }
}
