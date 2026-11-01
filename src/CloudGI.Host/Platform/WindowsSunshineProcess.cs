using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CloudGI.Host.Platform;

/// <summary>Creates the child suspended, assigns its job, then resumes it. Closing the job kills owned children only.</summary>
internal sealed class WindowsSunshineProcess : IDisposable
{
    private readonly SafeFileHandle _job;
    public Process Process { get; }

    public WindowsSunshineProcess(string executable, string arguments)
    {
        _job = CreateJobObjectW(0, null);
        if (_job.IsInvalid) throw new Win32Exception();
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE
        if (!SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        { _job.Dispose(); throw new Win32Exception(); }
        var startup = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfo>() };
        var command = new StringBuilder($"\"{executable}\" {arguments}");
        if (!CreateProcessW(executable, command, 0, 0, false, 0x08000004, 0, Path.GetDirectoryName(executable), ref startup, out var info))
        { _job.Dispose(); throw new Win32Exception(); }
        try
        {
            if (!AssignProcessToJobObject(_job, info.Process)) throw new Win32Exception();
            Process = Process.GetProcessById((int)info.ProcessId);
            if (ResumeThread(info.Thread) == uint.MaxValue) throw new Win32Exception();
        }
        catch { TerminateProcess(info.Process, 1); _job.Dispose(); throw; }
        finally { CloseHandle(info.Thread); CloseHandle(info.Process); }
    }

    public void Dispose()
    {
        _job.Dispose();
        try { Process.WaitForExit(5000); } catch (InvalidOperationException) { }
        Process.Dispose();
    }

    public static byte[] Protect(byte[] bytes, bool decrypt = false)
    {
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Marshal.Copy(bytes, 0, input.Data, bytes.Length);
        try
        {
            Blob output;
            var success = decrypt
                ? CryptUnprotectData(ref input, 0, 0, 0, 0, 1, out output)
                : CryptProtectData(ref input, null, 0, 0, 0, 1, out output);
            if (!success) throw new Win32Exception();
            try { var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result; }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public nint Data; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long ProcessTime, JobTime; public uint Flags; public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses; public nuint Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong A, B, C, D, E, F; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public uint Size; public string? Reserved, Desktop, Title;
        public uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public ushort Show, ReservedLength; public nint ReservedData, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int type, ref ExtendedLimits limits, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, nint process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string application, StringBuilder command, nint processAttributes, nint threadAttributes, bool inherit, uint flags, nint environment, string? directory, ref StartupInfo startup, out ProcessInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(nint process, uint code);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CryptProtectData(ref Blob input, string? description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}
