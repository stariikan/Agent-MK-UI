using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Orchestra.Core.Contracts;
using Orchestra.Core.Models;

namespace Orchestra.Core.Services
{
    /// <summary>
    /// Detects system RAM and GPU/VRAM directly in-process, without needing
    /// Python or any other tool to already be installed. This has to run
    /// *before* the Python environment exists (it's what decides which
    /// model to download in the first place), so it is deliberately
    /// dependency-free: P/Invoke for RAM, and either `nvidia-smi` (ships
    /// with the NVIDIA driver) or WMI for GPU/VRAM.
    ///
    /// Honesty about limits: AMD/Intel dedicated VRAM queried via WMI's
    /// AdapterRAM is notoriously unreliable -- a long-standing 32-bit field
    /// overflow bug misreports many modern cards as exactly 4GB (or 0).
    /// When that's detected we report VRAM as unknown and fall back to a
    /// RAM-based recommendation instead of guessing wrong.
    /// </summary>
    public class HardwareDetector
    {
        private readonly IAgentLogger _logger;

        public HardwareDetector(IAgentLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public HardwareProfile Detect()
        {
            var profile = new HardwareProfile { RamGb = GetTotalRamGb() };

            var nvidia = TryDetectNvidia();
            if (nvidia != null)
            {
                profile.GpuVendor = "nvidia";
                profile.GpuName = nvidia.Value.Name;
                profile.VramGb = nvidia.Value.VramGb;
                profile.VramIsApproximate = false;
                return profile;
            }

            var wmi = TryDetectViaWmi();
            if (wmi != null)
            {
                profile.GpuVendor = wmi.Value.Vendor;
                profile.GpuName = wmi.Value.Name;
                profile.VramGb = wmi.Value.VramGb;
                profile.VramIsApproximate = true;
                return profile;
            }

            profile.GpuVendor = "none";
            return profile;
        }

        // ------------------------------------------------------------
        // RAM
        // ------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        private double GetTotalRamGb()
        {
            try
            {
                var status = new MEMORYSTATUSEX();
                status.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));

                if (GlobalMemoryStatusEx(ref status))
                {
                    return Math.Round(status.ullTotalPhys / (1024.0 * 1024.0 * 1024.0), 1);
                }

                _logger.LogWarning("GlobalMemoryStatusEx failed; RAM detection returning 0.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"RAM detection failed: {ex.Message}");
            }

            return 0.0;
        }

        // ------------------------------------------------------------
        // GPU: NVIDIA via nvidia-smi
        // ------------------------------------------------------------

        private (string Name, double VramGb)? TryDetectNvidia()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "--query-gpu=name,memory.total --format=csv,noheader,nounits",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var proc = Process.Start(psi);
                if (proc == null)
                    return null;

                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5000);

                if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                {
                    return null;
                }

                (string Name, double VramGb)? best = null;

                foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = rawLine.Split(',');
                    if (parts.Length != 2)
                        continue;

                    string name = parts[0].Trim();
                    if (
                        !double.TryParse(
                            parts[1].Trim(),
                            NumberStyles.Any,
                            CultureInfo.InvariantCulture,
                            out double mib
                        )
                    )
                    {
                        continue;
                    }

                    double vramGb = Math.Round(mib / 1024.0, 1);
                    if (best == null || vramGb > best.Value.VramGb)
                    {
                        best = (name, vramGb);
                    }
                }

                return best;
            }
            catch (Exception ex)
            {
                // Not found / not an NVIDIA machine -- expected on most systems.
                _logger.LogInfo(
                    $"nvidia-smi not usable ({ex.GetType().Name}); trying WMI fallback for GPU detection."
                );
                return null;
            }
        }

        // ------------------------------------------------------------
        // GPU: best-effort WMI fallback (AMD/Intel/unknown)
        // ------------------------------------------------------------

        private (string Vendor, string Name, double? VramGb)? TryDetectViaWmi()
        {
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT Name, AdapterRAM FROM Win32_VideoController"
                );

                (string Vendor, string Name, double? VramGb)? best = null;

                foreach (System.Management.ManagementBaseObject obj in searcher.Get())
                {
                    string? name = obj["Name"] as string;
                    object? ramObj = obj["AdapterRAM"];

                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    string vendor = "unknown";
                    string lower = name.ToLowerInvariant();
                    if (lower.Contains("nvidia"))
                        vendor = "nvidia";
                    else if (lower.Contains("amd") || lower.Contains("radeon"))
                        vendor = "amd";
                    else if (lower.Contains("intel"))
                        vendor = "intel";

                    double? vramGb = null;
                    if (ramObj != null)
                    {
                        try
                        {
                            ulong bytes = Convert.ToUInt64(ramObj);
                            double gb = Math.Round(bytes / (1024.0 * 1024.0 * 1024.0), 1);

                            // Known WMI AdapterRAM 32-bit overflow: cards with
                            // >4GB VRAM frequently report exactly 4GB (or 0).
                            // Treat those readings as unknown rather than wrong.
                            bool suspicious = gb <= 0 || Math.Round(gb) == 4;
                            vramGb = suspicious ? null : gb;
                        }
                        catch
                        {
                            vramGb = null;
                        }
                    }

                    if (best == null || (vramGb ?? 0) > (best.Value.VramGb ?? 0))
                    {
                        best = (vendor, name, vramGb);
                    }
                }

                return best;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"WMI GPU detection unavailable: {ex.Message}");
                return null;
            }
        }
    }
}
