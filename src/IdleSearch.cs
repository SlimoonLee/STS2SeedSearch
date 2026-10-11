using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: InternalsVisibleTo("SeedSearch.Tests")]

namespace SeedSearch;

/// <summary>搜索运行期间的调度开关；切换不会改变筛选条件、索引或检查点。</summary>
public sealed class SearchRuntimeControl
{
    int _idleSearch;
    internal event Action<bool> Changed;

    public SearchRuntimeControl(bool idleSearch = false) => _idleSearch = idleSearch ? 1 : 0;
    public bool IdleSearch => Volatile.Read(ref _idleSearch) != 0;

    public void SetIdleSearch(bool enabled)
    {
        int value = enabled ? 1 : 0;
        if (Interlocked.Exchange(ref _idleSearch, value) == value) return;
        Changed?.Invoke(enabled);
    }
}

public sealed record IdleSearchStatus(int WorkerLimit, int AllowedWorkers, double DutyCycle,
    double? OtherCpuPercent, bool MonitoringAvailable, bool LowPriority, string Detail)
{
    public string Description => !MonitoringAvailable
        ? $"闲时搜索 · 保守限速 1 线程×20%（{Detail}）"
        : $"闲时搜索 · {(AllowedWorkers == 0 ? "让步暂停" : $"并行上限 {AllowedWorkers}/{WorkerLimit} · 工作比例 {DutyCycle:P0}")} · 其他 CPU {(OtherCpuPercent ?? 0):F0}%"
            + (LowPriority ? "" : " · 低优先级不可用");
}

internal interface ICpuLoadSource : IDisposable
{
    bool TrySample(out double otherCpuFraction, out string detail);
}

/// <summary>只读取整机与本进程 CPU 时间，不枚举其他应用或读取用户活动。</summary>
internal sealed class WindowsCpuLoadSource : ICpuLoadSource
{
    readonly Process _process = Process.GetCurrentProcess();
    ulong _total, _idle;
    long _own;
    bool _primed;

    public WindowsCpuLoadSource() => TrySample(out _, out _);

    public bool TrySample(out double otherCpuFraction, out string detail)
    {
        otherCpuFraction = 0;
        detail = "正在采样";
        if (!OperatingSystem.IsWindows()) { detail = "CPU 监测仅支持 Windows"; return false; }
        if (Environment.ProcessorCount > 64) { detail = "多处理器组暂不支持监测"; return false; }
        try
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user))
            { detail = $"CPU 计数读取失败 ({Marshal.GetLastWin32Error()})"; _primed = false; return false; }
            _process.Refresh();
            long own = _process.TotalProcessorTime.Ticks;
            ulong total = kernel.Value + user.Value;
            bool usable = _primed && total > _total && idle.Value >= _idle && own >= _own;
            if (usable)
                otherCpuFraction = ExternalFraction(total - _total, idle.Value - _idle, (ulong)(own - _own));
            _total = total; _idle = idle.Value; _own = own; _primed = true;
            return usable;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        { _primed = false; detail = "CPU 监测不可用：" + error.Message; return false; }
    }

    internal static double ExternalFraction(ulong total, ulong idle, ulong own)
        => total == 0 ? 0 : Math.Clamp((double)(total - Math.Min(total, idle)) / total - (double)own / total, 0, 1);

    public void Dispose() => _process.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    struct FileTime
    {
        public uint Low, High;
        public readonly ulong Value => ((ulong)High << 32) | Low;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
}

/// <summary>快速降低预算；余量连续恢复后，每次采样最多增加一个工作线程。</summary>
internal sealed class IdleCpuPolicy(int processors, int workerLimit)
{
    int _workers = 1, _recoverySamples;
    public const double ReserveFraction = 0.10;

    public (int Workers, double Duty) Update(double external)
    {
        double cores = Math.Min(workerLimit, Math.Max(0, (1 - ReserveFraction - Math.Clamp(external, 0, 1)) * processors));
        int wanted = (int)Math.Ceiling(cores);
        if (wanted <= _workers) { _workers = wanted; _recoverySamples = 0; }
        else if (++_recoverySamples >= 2) _workers = Math.Min(wanted, _workers + 1);
        return (_workers, _workers == 0 ? 0 : Math.Min(1, cores / _workers));
    }
    public void Reset() { _workers = 1; _recoverySamples = 0; }
}

/// <summary>闲时模式使用独立低优先级线程；停驻线程不会占用 ThreadPool。</summary>
internal sealed class IdleSearchGovernor : IDisposable
{
    readonly object _gate = new();
    readonly ICpuLoadSource _source;
    readonly IdleCpuPolicy _policy;
    readonly BackgroundPriorityScope _priority;
    readonly SearchRuntimeControl _control;
    readonly int _workerLimit;
    readonly long _origin = Stopwatch.GetTimestamp();
    static readonly long PeriodTicks = Math.Max(1, Stopwatch.Frequency / 10); // 100 ms
    static readonly long LeaseTicks = Math.Max(1, Stopwatch.Frequency / 50); // 20 ms
    IdleSearchStatus _status;
    int _active, _stopping, _disposed, _enabled;
    Thread _monitor;
    public IdleSearchStatus Status => Volatile.Read(ref _status);
    public bool Stopping => Volatile.Read(ref _stopping) != 0;
    public bool Enabled => Volatile.Read(ref _enabled) != 0;

    public IdleSearchGovernor(int maxWorkers, ICpuLoadSource source = null, SearchRuntimeControl control = null)
    {
        int limit = Math.Clamp(maxWorkers, 1, Environment.ProcessorCount);
        _source = source ?? new WindowsCpuLoadSource();
        _policy = new(Environment.ProcessorCount, limit);
        _priority = new();
        _control = control ?? new SearchRuntimeControl(true);
        _workerLimit = limit;
        _control.Changed += SetEnabled;
        SetEnabled(_control.IdleSearch);
    }

    internal void Sample()
    {
        if (!Enabled) return;
        var previous = Status;
        if (previous == null || !Enabled) return;
        if (_source.TrySample(out double external, out string detail) && double.IsFinite(external))
        {
            if (!Enabled) return;
            var (workers, duty) = _policy.Update(external);
            Volatile.Write(ref _status, new(previous.WorkerLimit, workers, duty,
                Math.Clamp(external, 0, 1) * 100, true, _priority.Applied, ""));
        }
        else
        {
            _policy.Reset();
            Volatile.Write(ref _status, new(previous.WorkerLimit, 1, 0.20, null, false, _priority.Applied, detail));
        }
        Wake();
    }

    internal Permit CreatePermit() => new(this);
    public void RequestStop() { Volatile.Write(ref _stopping, 1); Wake(); }
    void Wake() { lock (_gate) Monitor.PulseAll(_gate); }

    void SetEnabled(bool enabled)
    {
        if (Volatile.Read(ref _disposed) != 0 && enabled) return;
        if (enabled)
        {
            if (Interlocked.Exchange(ref _enabled, 1) != 0) return;
            _policy.Reset();
            _priority.Enable();
            Volatile.Write(ref _status, new IdleSearchStatus(_workerLimit, 1, 0.20, null, false, _priority.Applied, "正在采样"));
        }
        else
        {
            if (Interlocked.Exchange(ref _enabled, 0) == 0) return;
            _priority.Disable();
            Volatile.Write(ref _status, null);
        }
        Wake();
    }

    public void Start(CancellationToken ct)
    {
        if (_monitor != null) return;
        _monitor = new Thread(() =>
        {
            try
            {
                while (!ct.IsCancellationRequested && !Stopping)
                {
                    if (ct.WaitHandle.WaitOne(Enabled ? 1000 : 100)) break;
                    if (Enabled) Sample();
                }
            }
            catch (ObjectDisposedException) { }
            catch { RequestStop(); }
            finally { Wake(); }
        }) { IsBackground = true, Name = "SeedSearch idle monitor" };
        try { _monitor.Priority = ThreadPriority.Lowest; } catch (Exception) { }
        _monitor.Start();
    }

    internal sealed class Permit(IdleSearchGovernor owner) : IDisposable
    {
        bool _held;
        long _expires;
        IdleSearchStatus _budget;

        public bool Wait(CancellationToken ct, Func<bool> shouldStop)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (owner.Stopping || shouldStop()) { Release(); return false; }
                if (!owner.Enabled) { Release(); return true; }
                var budget = owner.Status;
                if (budget == null || !owner.Enabled) { Release(); return true; }
                long now = Stopwatch.GetTimestamp();
                long phase = (now - owner._origin) % PeriodTicks;
                long workTicks = (long)(PeriodTicks * budget.DutyCycle);
                bool workWindow = budget.AllowedWorkers > 0 && phase < workTicks;
                if (_held && ReferenceEquals(budget, _budget) && now < _expires && workWindow) return true;
                Release();
                lock (owner._gate)
                {
                    if (workWindow && ReferenceEquals(budget, owner.Status) && owner._active < budget.AllowedWorkers)
                    {
                        owner._active++; _held = true; _budget = budget; _expires = now + LeaseTicks;
                        return true;
                    }
                    // A finite wait makes Stop/Ctrl+C responsive even while every worker is paused.
                    int waitMs = workWindow ? 20 : budget.AllowedWorkers == 0 ? 100
                        : (int)Math.Clamp((PeriodTicks - phase) * 1000 / Stopwatch.Frequency + 1, 1, 100);
                    Monitor.Wait(owner._gate, waitMs);
                }
            }
        }

        void Release()
        {
            if (!_held) return;
            lock (owner._gate) { owner._active--; _held = false; Monitor.PulseAll(owner._gate); }
        }
        public void Dispose() => Release();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _control.Changed -= SetEnabled;
        RequestStop();
        SetEnabled(false);
        try { _monitor?.Join(1500); } catch (ThreadStateException) { }
        _source.Dispose(); _priority.Dispose();
    }
}

/// <summary>同一进程同时运行多个搜索时，最后一个闲时搜索退出才还原优先级。</summary>
internal sealed class BackgroundPriorityScope : IDisposable
{
    static readonly object Gate = new();
    static int _owners;
    static ProcessPriorityClass _original;
    static bool _changed, _globalApplied;
    int _registered, _applied;
    public bool Applied => Volatile.Read(ref _applied) != 0;

    public void Enable()
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (Gate)
        {
            if (Volatile.Read(ref _registered) != 0) return;
            if (_owners == 0)
            {
                _globalApplied = _changed = false;
                try
                {
                    using var process = Process.GetCurrentProcess();
                    _original = process.PriorityClass;
                    if (_original is not (ProcessPriorityClass.Idle or ProcessPriorityClass.BelowNormal))
                    { process.PriorityClass = ProcessPriorityClass.BelowNormal; _changed = true; }
                    _globalApplied = true;
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
            _owners++;
            Volatile.Write(ref _registered, 1);
            Volatile.Write(ref _applied, _globalApplied ? 1 : 0);
        }
    }

    public void Disable()
    {
        lock (Gate)
        {
            if (Volatile.Read(ref _registered) == 0) return;
            Volatile.Write(ref _registered, 0);
            Volatile.Write(ref _applied, 0);
            if (--_owners == 0 && _changed)
            {
                try
                {
                    using var process = Process.GetCurrentProcess();
                    if (process.PriorityClass == ProcessPriorityClass.BelowNormal) process.PriorityClass = _original;
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
    }

    public void Dispose() => Disable();
}
