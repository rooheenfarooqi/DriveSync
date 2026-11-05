using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DriveSync.Core.Parsing;

/// <summary>
/// Finds which programs have a file open, using the Windows Restart Manager, so the preview can say
/// "Microsoft Word has it locked" instead of a generic error.
/// </summary>
public static class FileLockInfo
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames,
        uint nApplications, RmUniqueProcess[]? rgApplications, uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RmProcessInfo[]? rgAffectedApps, ref uint lpdwRebootReasons);

    /// <summary>Names of the programs that have <paramref name="path"/> open (may be empty).</summary>
    public static List<string> WhoIsLocking(string path)
    {
        var names = new List<string>();
        if (!OperatingSystem.IsWindows()) return names;
        try
        {
            if (RmStartSession(out uint session, 0, Guid.NewGuid().ToString()) != 0) return names;
            try
            {
                if (RmRegisterResources(session, 1, new[] { path }, 0, null, 0, null) != 0) return names;
                uint needed, count = 0, reasons = 0;
                int result = RmGetList(session, out needed, ref count, null, ref reasons);
                if (result == 234 /* ERROR_MORE_DATA */ && needed > 0)
                {
                    var info = new RmProcessInfo[needed];
                    count = needed;
                    if (RmGetList(session, out needed, ref count, info, ref reasons) == 0)
                        for (int i = 0; i < count; i++)
                            names.Add(FriendlyName(info[i]));
                }
            }
            finally { RmEndSession(session); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return names.Distinct().ToList();
    }

    private static string FriendlyName(RmProcessInfo info)
    {
        try
        {
            using Process p = Process.GetProcessById(info.Process.dwProcessId);
            string? description = p.MainModule?.FileVersionInfo.FileDescription;
            if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
        }
        catch (Exception) { /* access denied or process gone */ }
        return string.IsNullOrWhiteSpace(info.strAppName) ? "Another app" : info.strAppName;
    }

    /// <summary>True for "being used by another process" and "locked a portion of the file".</summary>
    public static bool IsLockViolation(IOException ex)
    {
        int code = ex.HResult & 0xFFFF;
        return code is 32 or 33;
    }
}
