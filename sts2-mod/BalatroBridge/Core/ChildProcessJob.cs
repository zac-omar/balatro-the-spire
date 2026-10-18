using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BalatroBridge.Core;

/// <summary>
/// Ties child processes to this process's lifetime with a Windows job object, so Windows closes
/// them the moment this process ends, even if it crashes or is killed. A graceful shutdown should
/// still happen first; this is the backstop. Windows only; elsewhere it does nothing.
/// </summary>
public static class ChildProcessJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private static readonly object Lock = new();
    // Deliberately never closed: Windows closes it when this process exits, which kills the children.
    private static IntPtr _job;

    /// <summary>Returns false (and leaves the process alone) if the job couldn't be set up.</summary>
    public static bool Adopt(Process child, Action<string> log)
    {
        if (!OperatingSystem.IsWindows()) return false;
        lock (Lock)
        {
            if (_job == IntPtr.Zero) _job = CreateKillOnCloseJob(log);
            if (_job == IntPtr.Zero) return false;
            if (AssignProcessToJobObject(_job, child.Handle)) return true;
            log($"could not tie Balatro to the game's lifetime (error {Marshal.GetLastWin32Error()})");
            return false;
        }
    }

    private static IntPtr CreateKillOnCloseJob(Action<string> log)
    {
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            log($"could not create a job object (error {Marshal.GetLastWin32Error()})");
            return IntPtr.Zero;
        }
        var info = new ExtendedLimitInformation { BasicLimitInformation = { LimitFlags = JobObjectLimitKillOnJobClose } };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            log($"could not configure the job object (error {Marshal.GetLastWin32Error()})");
            CloseHandle(job);
            return IntPtr.Zero;
        }
        return job;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
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
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimitInformation info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
