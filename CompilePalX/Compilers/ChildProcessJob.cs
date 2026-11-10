using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CompilePalX.Compiling;

namespace CompilePalX.Compilers
{
    /// <summary>
    /// Ties the programs a compile starts to Compile Pal's own lifetime.
    ///
    /// Every compile step's process is put in one Windows job object that kills everything in it
    /// when the job's last handle closes, and Compile Pal holds that handle until it exits. So
    /// however Compile Pal goes away - closed mid-compile, killed, crashed - VVIS, VRAD, bspzip and
    /// whatever a plugin started under them (Viswright's vbsp/vvis, Shipwright's gmpublish) go
    /// with it. Before this, all of those kept running orphaned: at full CPU, holding files the
    /// next compile needed, and in Shipwright's case finishing an upload the user had stopped.
    ///
    /// Programs started after a process joins the job join it too, so a plugin's children are
    /// covered without the plugin doing anything. One that starts a child in the instant before
    /// it is adopted leaves that child out; the step's Cancel still kills the whole tree.
    ///
    /// Only for programs that belong to the compile. The game a step launches to play the map is
    /// left out (see <see cref="ShouldAdopt"/>), and so is anything the user opens from the
    /// window: closing Compile Pal must not close those.
    ///
    /// Best effort: where the system refuses (an old Wine, a parent job that forbids nesting),
    /// the compile runs exactly as it did before, and the reason is in the debug log.
    /// </summary>
    internal static class ChildProcessJob
    {
        private static readonly object gate = new();
        private static IntPtr job;
        private static bool unavailable;

        /// <summary>Puts <paramref name="process"/>, which must have started, in the job.</summary>
        public static void Adopt(Process process)
        {
            try
            {
                var handle = Job();
                if (handle == IntPtr.Zero)
                    return;

                if (!AssignProcessToJobObject(handle, process.Handle))
                    CompilePalLogger.LogLineDebug($"Could not tie {SafeName(process)} to Compile Pal's lifetime (error {Marshal.GetLastWin32Error()}).");
            }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception or EntryPointNotFoundException or DllNotFoundException)
            {
                // exited already, or no job objects on this system
                CompilePalLogger.LogLineDebug($"Could not tie {SafeName(process)} to Compile Pal's lifetime: {e.Message}");
            }
        }

        /// <summary>
        /// Whether a step's program belongs to the compile. The game does not: a GAME step launches
        /// it to play the map, and closing Compile Pal afterwards must not close the game.
        /// </summary>
        public static bool ShouldAdopt(string? metadataPath, string? fileName)
        {
            if (metadataPath != null && metadataPath.Contains("$gameEXE$", StringComparison.OrdinalIgnoreCase))
                return false;

            string? game = GameConfigurationManager.GameConfiguration?.GameEXE;
            if (!string.IsNullOrWhiteSpace(game) && !string.IsNullOrWhiteSpace(fileName))
            {
                try
                {
                    if (string.Equals(System.IO.Path.GetFullPath(fileName.Trim('"')), System.IO.Path.GetFullPath(game.Trim('"')), StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                catch (Exception e) when (e is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
                {
                    // not a path we can compare; treat it as a compile program
                }
            }

            return true;
        }

        private static string SafeName(Process process)
        {
            try { return process.StartInfo.FileName; }
            catch (InvalidOperationException) { return "a step"; }
        }

        private static IntPtr Job()
        {
            lock (gate)
            {
                if (job != IntPtr.Zero || unavailable)
                    return job;

                var created = CreateJobObject(IntPtr.Zero, null);
                if (created == IntPtr.Zero)
                {
                    unavailable = true;
                    CompilePalLogger.LogLineDebug($"No job object for compile programs (error {Marshal.GetLastWin32Error()}); they will not be stopped if Compile Pal exits.");
                    return IntPtr.Zero;
                }

                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
                };

                int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                IntPtr buffer = Marshal.AllocHGlobal(length);
                try
                {
                    Marshal.StructureToPtr(info, buffer, false);
                    if (!SetInformationJobObject(created, JobObjectExtendedLimitInformation, buffer, (uint)length))
                    {
                        unavailable = true;
                        CompilePalLogger.LogLineDebug($"Could not set up the job object for compile programs (error {Marshal.GetLastWin32Error()}).");
                        CloseHandle(created);
                        return IntPtr.Zero;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }

                // Never closed: the handle closing is what kills the job, and that is meant to
                // happen when the process exits.
                job = created;
                return job;
            }
        }

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
