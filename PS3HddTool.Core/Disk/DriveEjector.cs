using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace PS3HddTool.Core.Disk;

/// <summary>
/// Ask the operating system to safely remove a physical drive, the same as
/// "Safely Remove Hardware" (Windows), <c>udisksctl power-off</c> (Linux) or
/// <c>diskutil eject</c> (macOS). Call only after every handle to the drive is closed.
/// </summary>
public static class DriveEjector
{
    public sealed record Result(bool Success, string Message);

    public static Result Eject(string devicePath)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return EjectWindows(devicePath);
            if (OperatingSystem.IsLinux()) return EjectLinux(devicePath);
            if (OperatingSystem.IsMacOS()) return EjectMac(devicePath);
            return new Result(false, "Safe removal is not supported on this operating system.");
        }
        catch (Exception ex)
        {
            return new Result(false, $"Safe removal failed: {ex.Message}");
        }
    }

    // ── Linux ────────────────────────────────────────────────────────────────

    private static Result EjectLinux(string devicePath)
    {
        Run("sync", Array.Empty<string>(), out _);

        // A desktop may have auto-mounted a partition of the drive (the PS3's FAT cache
        // partition, for example); power-off refuses while anything is mounted.
        foreach (string part in MountedPartitionsOf(devicePath))
        {
            int urc = Run("udisksctl", new[] { "unmount", "-b", part }, out string uout, timeoutMs: 120_000);
            if (urc != 0)
                return new Result(false, $"{part} is mounted and could not be unmounted: {Trim(uout)}");
        }

        // udisks2 powers the drive down so it can be unplugged (USB docks, SATA hot-plug).
        // Same command as running "udisksctl power-off -b /dev/sdX" by hand: the desktop's
        // polkit agent can ask for a password, so allow time for that.
        int rc = Run("udisksctl", new[] { "power-off", "-b", devicePath }, out string output, timeoutMs: 120_000);
        if (rc == 0)
            return new Result(true, $"{devicePath} was powered off. You can unplug it now.");

        // Fallback for systems without udisks2 (requires root).
        string first = output;
        rc = Run("eject", new[] { devicePath }, out output);
        if (rc == 0)
            return new Result(true, $"{devicePath} was ejected. Wait for the drive to spin down before unplugging it.");

        return new Result(false,
            $"Could not remove {devicePath}. udisksctl: {Trim(first)} | eject: {Trim(output)}. " +
            "Close any program using the drive, or run: udisksctl power-off -b " + devicePath);
    }

    // ── macOS ────────────────────────────────────────────────────────────────

    private static Result EjectMac(string devicePath)
    {
        int rc = Run("diskutil", new[] { "eject", devicePath }, out string output);
        return rc == 0
            ? new Result(true, $"{devicePath} was ejected. You can unplug it now.")
            : new Result(false, $"diskutil could not eject {devicePath}: {Trim(output)}");
    }

    /// <summary>Partitions of <paramref name="devicePath"/> listed in /proc/mounts.</summary>
    private static IEnumerable<string> MountedPartitionsOf(string devicePath)
    {
        var result = new List<string>();
        try
        {
            foreach (string line in File.ReadLines("/proc/mounts"))
            {
                string dev = line.Split(' ', 2)[0];
                if (!dev.StartsWith(devicePath, StringComparison.Ordinal) || dev == devicePath) continue;
                // /dev/sda -> /dev/sda1, /dev/nvme0n1 -> /dev/nvme0n1p1 (but not /dev/sdab1).
                string rest = dev[devicePath.Length..].TrimStart('p');
                if (rest.Length > 0 && rest.All(char.IsDigit) && !result.Contains(dev))
                    result.Add(dev);
            }
        }
        catch { /* not Linux or not readable: nothing to unmount */ }
        return result;
    }

    private static int Run(string file, string[] args, out string output, int timeoutMs = 60_000)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } output = "timed out"; return -1; }
            output = (stdout + " " + stderr).Trim();
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            output = ex.Message;
            return -1;
        }
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;

    // ── Windows ──────────────────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    private static Result EjectWindows(string devicePath)
    {
        // Expect \\.\PhysicalDriveN
        int idx = devicePath.IndexOf("PhysicalDrive", StringComparison.OrdinalIgnoreCase);
        if (idx < 0 || !int.TryParse(devicePath[(idx + "PhysicalDrive".Length)..], out int driveNumber))
            return new Result(false, $"'{devicePath}' is not a \\\\.\\PhysicalDriveN path.");

        int devInst = FindDiskDevInst(driveNumber);
        if (devInst == 0)
            return new Result(false, $"Could not find PhysicalDrive{driveNumber} in the device list.");

        // Try the disk itself first, then its parent (USB bridge / port), like the shell does.
        string? veto = TryEject(devInst);
        if (veto == null) return new Result(true, $"PhysicalDrive{driveNumber} can now be safely removed.");
        if (CM_Get_Parent(out int parent, devInst, 0) == 0 && parent != 0)
        {
            string? parentVeto = TryEject(parent);
            if (parentVeto == null) return new Result(true, $"PhysicalDrive{driveNumber} can now be safely removed.");
            veto += " / parent: " + parentVeto;
        }
        return new Result(false,
            $"Windows refused to remove PhysicalDrive{driveNumber} ({veto}). Close other programs using it; " +
            "internal SATA drives only support this when hot-plug is enabled in the BIOS.");
    }

    [SupportedOSPlatform("windows")]
    private static string? TryEject(int devInst)
    {
        var vetoName = new StringBuilder(260);
        int cr = CM_Request_Device_EjectW(devInst, out int vetoType, vetoName, vetoName.Capacity, 0);
        if (cr == 0 && vetoType == 0) return null;
        return vetoType != 0 ? $"veto {VetoText(vetoType)} {vetoName}".Trim() : $"CONFIGRET 0x{cr:X}";
    }

    private static string VetoText(int t) => t switch
    {
        1 => "LegacyDevice", 2 => "PendingClose", 3 => "WindowsApp", 4 => "WindowsService",
        5 => "OutstandingOpen", 6 => "Device", 7 => "Driver", 8 => "IllegalDeviceRequest",
        9 => "InsufficientPower", 10 => "NonDisableable", 11 => "LegacyDriver", 12 => "InsufficientRights",
        _ => $"type {t}"
    };

    [SupportedOSPlatform("windows")]
    private static int FindDiskDevInst(int driveNumber)
    {
        var diskGuid = new Guid("53f56307-b6bf-11d0-94f2-00a0c91efb8b"); // GUID_DEVINTERFACE_DISK
        IntPtr set = SetupDiGetClassDevsW(ref diskGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return 0;
        try
        {
            for (int i = 0; ; i++)
            {
                var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref diskGuid, i, ref ifData)) break;

                var devInfo = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                SetupDiGetDeviceInterfaceDetailW(set, ref ifData, IntPtr.Zero, 0, out int required, ref devInfo);
                if (required <= 0) continue;
                IntPtr detail = Marshal.AllocHGlobal(required);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6); // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
                    if (!SetupDiGetDeviceInterfaceDetailW(set, ref ifData, detail, required, out _, ref devInfo)) continue;
                    string? path = Marshal.PtrToStringUni(detail + 4);
                    if (path != null && GetDeviceNumber(path) == driveNumber)
                        return devInfo.DevInst;
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return 0;
    }

    [SupportedOSPlatform("windows")]
    private static int GetDeviceNumber(string interfacePath)
    {
        IntPtr h = CreateFileW(interfacePath, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h == IntPtr.Zero || h == new IntPtr(-1)) return -1;
        try
        {
            byte[] outBuf = new byte[12]; // STORAGE_DEVICE_NUMBER
            const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x2D1080;
            if (!DeviceIoControl(h, IOCTL_STORAGE_GET_DEVICE_NUMBER, IntPtr.Zero, 0, outBuf, outBuf.Length, out int got, IntPtr.Zero) || got < 8)
                return -1;
            return BitConverter.ToInt32(outBuf, 4);
        }
        finally { CloseHandle(h); }
    }

    private const int DIGCF_PRESENT = 0x2;
    private const int DIGCF_DEVICEINTERFACE = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public int DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
        ref Guid interfaceClassGuid, int memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr deviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData,
        int deviceInterfaceDetailDataSize, out int requiredSize, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Request_Device_EjectW(int dnDevInst, out int pVetoType,
        StringBuilder pszVetoName, int ulNameLength, int ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out int pdnDevInst, int dnDevInst, int ulFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode, IntPtr lpInBuffer,
        int nInBufferSize, byte[] lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);
}
