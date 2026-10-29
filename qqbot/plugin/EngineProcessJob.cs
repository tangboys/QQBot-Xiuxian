using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace QQBot.Plugins.Xiuxian;

/// <summary>为当前游戏引擎创建专有 Windows Job，宿主异常退出后由系统回收子进程。</summary>
internal sealed class EngineProcessJob : IDisposable
{
    private readonly SafeFileHandle _handle;

    /// <summary>在发送初始化帧前将等待输入的引擎纳入 Job；此时引擎尚未加载游戏或启动任务。</summary>
    public EngineProcessJob(Process process)
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } };
            if (!SetInformationJobObject(_handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()) ||
                !AssignProcessToJobObject(_handle, process.SafeHandle)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch { _handle.Dispose(); throw; }
    }

    /// <summary>释放仅属于本插件引擎的句柄，关闭此 Job 内的进程。</summary>
    public void Dispose() => _handle.Dispose();

    /// <summary>对应原生 Job 基础限制布局，字段顺序与 Windows ABI 一致。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    /// <summary>对应 Job 扩展限制所需的 IO 统计布局。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }

    /// <summary>携带 kill-on-close 标志的原生扩展限制。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
    }

    /// <summary>创建不命名、不继承的专有 Job。</summary>
    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    /// <summary>设置关闭 Job 时回收进程的策略。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int type, ref ExtendedLimits limits, uint length);
    /// <summary>只绑定当前插件直接启动的进程，不根据名称寻找其他进程。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}
