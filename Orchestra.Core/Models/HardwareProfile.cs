namespace Orchestra.Core.Models
{
    /// <summary>
    /// Raw hardware facts gathered from the local machine. Populated by
    /// <see cref="Orchestra.Core.Services.HardwareDetector"/>.
    /// </summary>
    public class HardwareProfile
    {
        public double RamGb { get; set; }

        public string GpuVendor { get; set; } = "none"; // "nvidia" | "amd" | "intel" | "unknown" | "none"

        public string? GpuName { get; set; }

        public double? VramGb { get; set; }

        /// <summary>
        /// True when VRAM came from a source known to be unreliable
        /// (Windows WMI AdapterRAM has a long-standing 32-bit overflow bug
        /// that misreports many AMD/Intel cards). NVIDIA figures via
        /// nvidia-smi are always exact, so this is false for those.
        /// </summary>
        public bool VramIsApproximate { get; set; }
    }
}
