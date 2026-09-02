using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services
{
    public class RouterInfoService
    {
        private readonly GLInetSshService _ssh;
        private readonly object _cpuSnapshotLock = new();
        private CpuTickSnapshot? _previousCpuSnapshot;
        private double? _lastCpuUsagePercent;


        public RouterInfoService(GLInetSshService ssh)
        {
            _ssh = ssh;
        }


        public async Task<RouterInfo> GetRouterInfoAsync()
        {
            var info = new RouterInfo();


            //
            // Board information
            //

            string boardJson =
                await _ssh.RunCommandAsync(
                    "ubus call system board");


            try
            {
                using JsonDocument doc =
                    JsonDocument.Parse(boardJson);


                JsonElement root =
                    doc.RootElement;


                if (root.TryGetProperty(
                    "model",
                    out JsonElement model))
                {
                    info.Model =
                        model.GetString() ?? "-";
                }


                if (root.TryGetProperty(
                    "hostname",
                    out JsonElement hostname))
                {
                    info.Hostname =
                        hostname.GetString() ?? "-";
                }


                if (root.TryGetProperty(
                    "release",
                    out JsonElement release))
                {
                    if (release.TryGetProperty(
                        "version",
                        out JsonElement version))
                    {
                        info.Firmware =
                            version.GetString() ?? "-";
                    }
                }
            }
            catch
            {
                info.Model = "Unknown";
            }



            //
            // Uptime
            //

            try
            {
                string uptimeSeconds =
                    await _ssh.RunCommandAsync(
                        "cat /proc/uptime | awk '{print $1}'");


                if (double.TryParse(
                    uptimeSeconds.Trim(),
                    out double seconds))
                {
                    TimeSpan uptime =
                        TimeSpan.FromSeconds(seconds);


                    if (uptime.TotalDays >= 1)
                    {
                        info.Uptime =
                            $"{(int)uptime.TotalDays} days " +
                            $"{uptime.Hours} hours " +
                            $"{uptime.Minutes} minutes";
                    }
                    else
                    {
                        info.Uptime =
                            $"{uptime.Hours} hours " +
                            $"{uptime.Minutes} minutes";
                    }
                }
                else
                {
                    info.Uptime = "-";
                }
            }
            catch
            {
                info.Uptime = "-";
            }


            //
            // CPU
            //

            try
            {
                string cpuSample =
                    await _ssh.RunCommandAsync(
                        "awk '/^cpu / { print; exit }' /proc/stat; " +
                        "(getconf _NPROCESSORS_ONLN 2>/dev/null || " +
                        "grep -c '^processor' /proc/cpuinfo) | head -n1");

                Debug.WriteLine(
                    $"Router CPU sample raw: {ToSingleLine(cpuSample)}");

                if (!TryParseCpuSample(
                        cpuSample,
                        out CpuTickSnapshot currentSnapshot,
                        out int? logicalProcessorCount))
                {
                    Debug.WriteLine("Router CPU sample parsing failed.");
                    ApplyLastCpuUsage(info);
                }
                else
                {
                    info.LogicalProcessorCount = logicalProcessorCount;
                    double? calculatedUsage = null;
                    bool baselinePending = false;

                    lock (_cpuSnapshotLock)
                    {
                        if (_previousCpuSnapshot is CpuTickSnapshot previous &&
                            currentSnapshot.TotalTicks >= previous.TotalTicks &&
                            currentSnapshot.IdleTicks >= previous.IdleTicks)
                        {
                            ulong totalDelta =
                                currentSnapshot.TotalTicks - previous.TotalTicks;
                            ulong idleDelta =
                                currentSnapshot.IdleTicks - previous.IdleTicks;

                            if (totalDelta > 0 && idleDelta <= totalDelta)
                            {
                                calculatedUsage = Math.Clamp(
                                    (1d - idleDelta / (double)totalDelta) * 100d,
                                    0d,
                                    100d);
                                _lastCpuUsagePercent = calculatedUsage;
                            }
                        }
                        else if (_lastCpuUsagePercent is null)
                        {
                            baselinePending = true;
                        }

                        _previousCpuSnapshot = currentSnapshot;

                        if (calculatedUsage is null)
                            calculatedUsage = _lastCpuUsagePercent;
                    }

                    info.CpuUtilisationPending =
                        baselinePending && calculatedUsage is null;
                    ApplyCpuUsage(info, calculatedUsage);
                }

                Debug.WriteLine(
                    $"Router CPU parsed: cores={info.LogicalProcessorCount?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}, " +
                    $"usage={info.CpuUsage}");
            }
            catch
            {
                Debug.WriteLine("Router CPU sampling command failed.");
                ApplyLastCpuUsage(info);
            }


            //
            // Temperature and system load
            //

            try
            {
                string temperature =
                    await _ssh.RunCommandAsync(
                        "for f in /sys/class/thermal/thermal_zone*/temp; do [ -r \"$f\" ] && cat \"$f\" && break; done");

                if (double.TryParse(
                    temperature.Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double rawTemperature))
                {
                    double celsius =
                        rawTemperature > 1000
                            ? rawTemperature / 1000d
                            : rawTemperature;

                    info.Temperature =
                        $"{celsius:0.#} °C";
                }
            }
            catch
            {
                info.Temperature = "-";
            }

            try
            {
                string loadAverage =
                    await _ssh.RunCommandAsync(
                        "cat /proc/loadavg");

                Debug.WriteLine(
                    $"Router load average raw: {ToSingleLine(loadAverage)}");

                string[] loadParts = loadAverage.Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries);

                if (loadParts.Length >= 3 &&
                    double.TryParse(
                        loadParts[0],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double load1) &&
                    double.TryParse(
                        loadParts[1],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double load5) &&
                    double.TryParse(
                        loadParts[2],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double load15))
                {
                    info.LoadAverage1Minute = load1;
                    info.LoadAverage = string.Create(
                        CultureInfo.InvariantCulture,
                        $"{load1:0.##} / {load5:0.##} / {load15:0.##}");

                    Debug.WriteLine(
                        $"Router load average parsed: oneMinute={load1.ToString("0.##", CultureInfo.InvariantCulture)}, " +
                        $"cores={info.LogicalProcessorCount?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}");
                }
                else
                {
                    info.LoadAverage = "-";
                    Debug.WriteLine("Router load average parsing failed.");
                }
            }
            catch
            {
                info.LoadAverage = "-";
            }



            //
            // Memory
            //

            try
            {
                string memory =
                    await _ssh.RunCommandAsync(
                        "awk '/^(MemTotal|MemFree|Buffers|Cached|SReclaimable):/ {print $1 $2}' /proc/meminfo");

                double total = 0;
                double free = 0;
                double buffers = 0;
                double cached = 0;
                double reclaimable = 0;

                foreach (string line in
                    memory.Split(
                        new[] { '\r', '\n' },
                        StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] pair =
                        line.Split(
                            ':',
                            StringSplitOptions.RemoveEmptyEntries);

                    if (pair.Length != 2 ||
                        !double.TryParse(
                            pair[1].Trim(),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out double value))
                    {
                        continue;
                    }

                    switch (pair[0].Trim())
                    {
                        case "MemTotal":
                            total = value;
                            break;
                        case "MemFree":
                            free = value;
                            break;
                        case "Buffers":
                            buffers = value;
                            break;
                        case "Cached":
                            cached = value;
                            break;
                        case "SReclaimable":
                            reclaimable = value;
                            break;
                    }
                }

                double cache =
                    cached + reclaimable;

                double used =
                    Math.Max(
                        0,
                        total - free - buffers - cache);

                if (total > 0)
                {
                    info.MemoryUsage =
                        Math.Round(
                            used / total * 100,
                            1) + "%";

                    info.MemoryUsed =
                        FormatKilobytes(used);

                    info.MemoryCache =
                        FormatKilobytes(cache);
                }
                else
                {
                    info.MemoryUsage = "-";
                    info.MemoryUsed = "-";
                    info.MemoryCache = "-";
                }
            }
            catch
            {
                info.MemoryUsage = "-";
                info.MemoryUsed = "-";
                info.MemoryCache = "-";
            }



            //
            // Storage
            //

            info.StorageUsage =
                (await _ssh.RunCommandAsync(
                    "df -h / | tail -1")).Trim();

            // BusyBox/OpenWrt-compatible aggregate inventory.  Only mounted
            // block-backed filesystems are surfaced as external storage; root,
            // overlay, ROM and virtual filesystems are deliberately excluded.
            try
            {
                string storageOutput = await _ssh.RunCommandAsync("df -P; printf '\\n__MOUNTS__\\n'; cat /proc/mounts 2>/dev/null; printf '\\n__PARTITIONS__\\n'; cat /proc/partitions 2>/dev/null; printf '\\n__BLOCKS__\\n'; for f in /sys/class/block/*/removable; do n=${f%/removable}; n=${n##*/}; r=$(cat \"$f\" 2>/dev/null); s=$(cat \"/sys/class/block/$n/size\" 2>/dev/null); printf '%s|%s|%s\\n' \"$n\" \"$r\" \"$s\"; done");
                info.ExternalStorage = ParseExternalStorage(storageOutput);
                info.AttachedStorage = ParseAttachedStorage(storageOutput);
                info.ExternalStorageInventoryLoaded = !storageOutput.StartsWith("SSH_", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                info.ExternalStorageInventoryLoaded = false;
                info.ExternalStorage = new();
                info.AttachedStorage = new();
            }


            return info;
        }

        private static List<MountedStorageInfo> ParseExternalStorage(string output)
        {
            var mounts = new Dictionary<string, string>(StringComparer.Ordinal);
            bool inMounts = false;
            foreach (string line in output.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line == "__MOUNTS__") { inMounts = true; continue; }
                if (line is "__PARTITIONS__" or "__BLOCKS__") { inMounts = false; continue; }
                if (!inMounts) continue;
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4) mounts[NormalizeMountPoint(parts[1])] = $"{parts[2]}|{parts[3]}";
            }

            var result = new List<MountedStorageInfo>();
            foreach (string line in output.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.StartsWith("Filesystem", StringComparison.OrdinalIgnoreCase) || line == "__MOUNTS__") continue;
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 6 || !parts[0].StartsWith("/dev/", StringComparison.Ordinal)) continue;
                int percentIndex = Array.FindIndex(parts, part => part.EndsWith('%'));
                if (percentIndex < 4 || percentIndex == parts.Length - 1) continue;
                string mountPoint = NormalizeMountPoint(string.Join(' ', parts[(percentIndex + 1)..]));
                if (mountPoint is "/" or "/rom" or "/overlay") continue;
                if (mounts.TryGetValue(mountPoint, out string? mountMetadata))
                {
                    string fileSystem = mountMetadata.Split('|')[0];
                    if (IsVirtualFileSystem(fileSystem)) continue;
                }
                result.Add(new MountedStorageInfo
                {
                    Device = parts[0], Capacity = FormatStorageSize(parts[1]), Used = FormatStorageSize(parts[2]),
                    Available = FormatStorageSize(parts[3]), Usage = parts[percentIndex], MountPoint = mountPoint,
                    FileSystem = mountMetadata?.Split('|')[0] ?? "Unknown",
                    ReadOnly = mountMetadata?.Split('|').ElementAtOrDefault(1)?.Split(',').Contains("ro", StringComparer.Ordinal) == true
                });
            }
            return result.GroupBy(item => item.MountPoint, StringComparer.Ordinal).Select(group => group.First()).ToList();
        }

        private static string NormalizeMountPoint(string mountPoint)
        {
            if (string.IsNullOrWhiteSpace(mountPoint)) return string.Empty;
            string normalized = mountPoint.Trim();
            while (normalized.Length > 1 && normalized.EndsWith("/", StringComparison.Ordinal))
                normalized = normalized[..^1];
            return normalized;
        }

        private static bool IsVirtualFileSystem(string fileSystem) => fileSystem.ToLowerInvariant() switch
        {
            "overlay" or "overlayfs" or "rootfs" or "tmpfs" or "devtmpfs" or "proc" or "sysfs" or
            "cgroup" or "cgroup2" or "debugfs" or "tracefs" or "securityfs" or "pstore" or "configfs" or "ramfs" => true,
            _ => false
        };

        private static List<StorageDeviceInfo> ParseAttachedStorage(string output)
        {
            bool inBlocks = false;
            var devices = new List<StorageDeviceInfo>();
            foreach (string line in output.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line == "__BLOCKS__") { inBlocks = true; continue; }
                if (!inBlocks || line.Contains("SSH_", StringComparison.OrdinalIgnoreCase)) continue;
                string[] parts = line.Split('|');
                if (parts.Length != 3 || string.IsNullOrWhiteSpace(parts[0]) || !long.TryParse(parts[2], out long sectors) || sectors <= 0) continue;
                string name = parts[0];
                if (name.Any(char.IsWhiteSpace) || name.EndsWith("p", StringComparison.OrdinalIgnoreCase) && name.Length < 2) continue;
                devices.Add(new StorageDeviceInfo { Device = "/dev/" + name, Removable = parts[1] == "1", Size = FormatStorageBytes(sectors * 512d) });
            }
            return devices.Where(item => item.Removable).GroupBy(item => item.Device.TrimEnd('0','1','2','3','4','5','6','7','8','9'), StringComparer.Ordinal).Select(group => group.First()).ToList();
        }
        private static string FormatKilobytes(
            double kilobytes)
        {
            double megabytes =
                kilobytes / 1024d;

            return megabytes >= 1024d
                ? $"{megabytes / 1024d:0.0} GB"
                : $"{megabytes:0} MB";
        }

        private static string FormatStorageSize(string value)
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double blocks)) return "—";
            return FormatStorageBytes(blocks * 1024d);
        }

        private static string FormatStorageBytes(double bytes)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            int unit = 0;
            while (bytes >= 1024d && unit < units.Length - 1) { bytes /= 1024d; unit++; }
            return $"{bytes:0.#} {units[unit]}";
        }

        private void ApplyLastCpuUsage(RouterInfo info)
        {
            double? lastUsage;
            lock (_cpuSnapshotLock)
                lastUsage = _lastCpuUsagePercent;

            ApplyCpuUsage(info, lastUsage);
        }

        private static void ApplyCpuUsage(
            RouterInfo info,
            double? usagePercent)
        {
            if (usagePercent is not double usage ||
                !double.IsFinite(usage) ||
                usage < 0 || usage > 100)
            {
                info.CpuUsagePercent = null;
                info.CpuUsage = "-";
                return;
            }

            double rounded = Math.Round(usage, 1);
            info.CpuUsagePercent = rounded;
            info.CpuUsage =
                rounded.ToString("0.#", CultureInfo.InvariantCulture) + "%";
        }

        private static bool TryParseCpuSample(
            string output,
            out CpuTickSnapshot snapshot,
            out int? logicalProcessorCount)
        {
            snapshot = default;
            logicalProcessorCount = null;

            string[] lines = output.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            string? cpuLine = lines.FirstOrDefault(
                line => line.StartsWith("cpu ", StringComparison.Ordinal));
            if (cpuLine is null)
                return false;

            string[] fields = cpuLine.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 5)
                return false;

            // Linux accounts guest time inside user/nice, so sum through
            // steal only and count idle plus iowait as idle time.
            int tickFieldCount = Math.Min(fields.Length - 1, 8);
            var ticks = new ulong[tickFieldCount];
            for (int index = 0; index < tickFieldCount; index++)
            {
                if (!ulong.TryParse(
                        fields[index + 1],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out ticks[index]))
                {
                    return false;
                }
            }

            ulong totalTicks = 0;
            foreach (ulong tick in ticks)
                totalTicks += tick;

            ulong idleTicks = ticks[3];
            if (ticks.Length > 4)
                idleTicks += ticks[4];

            if (totalTicks == 0 || idleTicks > totalTicks)
                return false;

            foreach (string line in lines)
            {
                if (int.TryParse(
                        line,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int cores) && cores > 0)
                {
                    logicalProcessorCount = cores;
                    break;
                }
            }

            snapshot = new CpuTickSnapshot(totalTicks, idleTicks);
            return true;
        }

        private static string ToSingleLine(string value)
        {
            return value
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
        }

        private readonly record struct CpuTickSnapshot(
            ulong TotalTicks,
            ulong IdleTicks);

    }
}
