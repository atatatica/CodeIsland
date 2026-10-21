using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CodeIsland.Windows.Island;

/// Names the app hosting an agent (Windows Terminal, VS Code, the Claude app …)
/// by walking up from the hook's parent process.
public static class ProcessTree
{
    private static readonly Dictionary<int, string?> HostCache = new();

    public static string? HostName(int? processId)
    {
        if (processId is not { } pid || pid <= 0) return null;
        lock (HostCache)
        {
            if (HostCache.TryGetValue(pid, out var cached)) return cached;
        }
        var host = Resolve(pid);
        lock (HostCache) HostCache[pid] = host;
        return host;
    }

    private static string? Resolve(int pid)
    {
        var parents = SnapshotParents();
        var current = pid;
        for (var depth = 0; depth < 10 && current > 4; depth++)
        {
            var path = ImagePath(current);
            var name = path is null ? null : Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            var label = name switch
            {
                "windowsterminal" or "openconsole" => "Terminal",
                "code" => "VS Code",
                "code - insiders" => "VS Code",
                "cursor" => "Cursor",
                "windsurf" => "Windsurf",
                "idea64" => "IntelliJ",
                "pycharm64" => "PyCharm",
                "webstorm64" => "WebStorm",
                "rider64" => "Rider",
                "goland64" => "GoLand",
                "wezterm-gui" => "WezTerm",
                "alacritty" => "Alacritty",
                "tabby" => "Tabby",
                "warp" => "Warp",
                "mintty" => "Git Bash",
                // The desktop app and the CLI share the name; only the desktop app lives outside .local\bin.
                "claude" when path is not null && !path.Contains(@"\.local\", StringComparison.OrdinalIgnoreCase) => "Claude",
                "explorer" or "services" or "svchost" => "",
                _ => null
            };
            if (label is not null) return label.Length == 0 ? null : label;
            if (!parents.TryGetValue(current, out var parent) || parent == current) break;
            current = parent;
        }
        return null;
    }

    private static Dictionary<int, int> SnapshotParents()
    {
        var result = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(0x2, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return result;
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry)) return result;
            do result[(int)entry.ProcessId] = (int)entry.ParentProcessId;
            while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return result;
    }

    private static string? ImagePath(int pid)
    {
        var handle = OpenProcess(0x1000, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var builder = new StringBuilder(1024);
            var size = builder.Capacity;
            return QueryFullProcessImageName(handle, 0, builder, ref size) ? builder.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
