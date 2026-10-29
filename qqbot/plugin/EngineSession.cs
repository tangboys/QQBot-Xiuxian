using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using QQBot.Abstractions;

namespace QQBot.Plugins.Xiuxian;

/// <summary>通过有界 JSONL 管道托管唯一游戏进程；协议和宿主配置环境不携带 OneBot 凭据，也不开放监听端口。</summary>
internal sealed class EngineSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly IPluginActionChannel _actions;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _lifetime;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _actionSlots = new(8, 8);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<long>> _events = new();
    private readonly ConcurrentDictionary<(long? Group, long User), long> _continuations = new();
    private readonly ConcurrentDictionary<long, Task> _actionTasks = new();
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _stopGate = new();
    private readonly EngineProcessJob? _job;
    private readonly Task _reader;
    private readonly Task _diagnostics;
    private Task? _stopTask;
    private long _actionSequence;
    private volatile string _description = "启动中";
    private volatile string? _pendingConversation;

    public bool IsAlive => !_lifetime.IsCancellationRequested && !_reader.IsCompleted && !_process.HasExited;
    public string ReadyDescription => _description;

    /// <summary>仅放行 NoneBot 正在等待的同群同用户续答，过期会话不唤醒引擎。</summary>
    public bool CanContinue(BotMessage message)
    {
        var key = (message.GroupId, message.UserId);
        return IsAlive && _continuations.TryGetValue(key, out var until) && until > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    /// <summary>覆盖提示已经送达、事件完成回包尚未返回时的续答窗口。</summary>
    public bool IsDispatching(BotMessage message) => IsAlive && _pendingConversation == $"{message.GroupId}:{message.UserId}";

    /// <summary>先绑定进程所有权再开始协议读取，防止宿主崩溃留下独立引擎。</summary>
    private EngineSession(Process process, IPluginActionChannel actions, Action<string> log, CancellationToken ct)
    {
        _process = process; _actions = actions; _log = log;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, actions.Stopping);
        if (OperatingSystem.IsWindows()) _job = new EngineProcessJob(process);
        _reader = Task.Run(ReadAsync, CancellationToken.None);
        _diagnostics = Task.Run(DrainDiagnosticsAsync, CancellationToken.None);
    }

    /// <summary>启动随包解释器或服务器指定解释器；只加载本插件固定源码与独立工作区。</summary>
    public static async Task<EngineSession> StartAsync(string root, long selfId, IPluginActionChannel actions, Action<string> log, CancellationToken ct)
    {
        var configured = Environment.GetEnvironmentVariable("QQBOT_XIUXIAN_PYTHON");
        var python = string.IsNullOrWhiteSpace(configured)
            ? OperatingSystem.IsWindows() ? Path.Combine(root, "runtime", "win-x64", "python.exe") : "python3"
            : configured;
        var engine = Path.Combine(root, "engine");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        var start = new ProcessStartInfo(python)
        {
            WorkingDirectory = data, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("-u"); start.ArgumentList.Add("-B");
        start.ArgumentList.Add(Path.Combine(engine, "qqbot", "bridge", "main.py"));
        // 父进程可从环境读取 NapCat 凭据，子进程只接收桥所需的源码位置。
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("QQBOT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(name);
        start.Environment["QQBOT_XIUXIAN_SOURCE_ROOT"] = engine;
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["PYTHONNOUSERSITE"] = "1";
        var process = Process.Start(start) ?? throw new IOException("修仙解释器未能启动。");
        EngineSession? session = null;
        try
        {
            session = new EngineSession(process, actions, log, ct);
            await session.WriteAsync(new { type = "initialize", self_id = selfId,
                administrators = actions.AdministratorIds, data_root = data, nickname = "QQBot" }, ct);
            await session._ready.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
            return session;
        }
        catch
        {
            if (session is not null) await session.DisposeAsync();
            else { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } process.Dispose(); }
            throw;
        }
    }

    /// <summary>等待一条已投递指令的实际完成；超时不重放，避免重复领取或交易。</summary>
    public async Task DispatchAsync(string id, JsonElement message, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_events.TryAdd(id, completion)) throw new InvalidOperationException("修仙指令重复投递。");
        try
        {
            long? group = message.TryGetProperty("group_id", out var value) && value.ValueKind != JsonValueKind.Null ? value.GetInt64() : null;
            var user = message.GetProperty("user_id").GetInt64();
            _pendingConversation = $"{group}:{user}";
            await WriteAsync(new { type = "event", id, @event = message }, ct);
            var until = await completion.Task.WaitAsync(TimeSpan.FromSeconds(120), ct);
            var key = (group, user);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // 仅串行消费器维护缓存；不驱逐活跃会话，超限时明确终止不同步的选择流程。
            foreach (var expired in _continuations.Where(item => item.Value <= now)) _continuations.TryRemove(expired.Key, out _);
            if (until > now)
            {
                if (!_continuations.ContainsKey(key) && _continuations.Count >= 256) throw new EngineContinuationLimitException();
                _continuations[key] = until;
            }
            else _continuations.TryRemove(key, out _);
        }
        finally { _pendingConversation = null; _events.TryRemove(id, out _); }
    }

    /// <summary>独占写入单帧，避免并发动作回执与事件数据混行。</summary>
    private async Task WriteAsync(object packet, CancellationToken ct)
    {
        var raw = JsonSerializer.SerializeToUtf8Bytes(packet);
        if (raw.Length > 8 * 1024 * 1024) throw new InvalidDataException("修仙协议帧超过上限。");
        await _writeGate.WaitAsync(ct);
        try
        {
            await _process.StandardInput.BaseStream.WriteAsync(raw, ct);
            await _process.StandardInput.BaseStream.WriteAsync(new byte[] { 10 }, ct);
            await _process.StandardInput.BaseStream.FlushAsync(ct);
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>在读取期限制字节数和 JSON 深度；动作并发有界，保持双向管道可继续接收回执。</summary>
    private async Task ReadAsync()
    {
        var ct = _lifetime.Token;
        try
        {
            var lines = new EngineLines(_process.StandardOutput.BaseStream);
            while (await lines.ReadAsync(ct) is { } line)
            {
                using var json = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 48 });
                var packet = json.RootElement;
                switch (packet.GetProperty("type").GetString())
                {
                    case "ready":
                        var modules = packet.GetProperty("modules").GetArrayLength();
                        var commands = packet.GetProperty("commands").GetInt32();
                        var jobs = packet.GetProperty("jobs").GetInt32();
                        if (modules < 16 || commands < 128 || jobs != 14) throw new InvalidDataException("完整玩法注册数量不匹配。");
                        _description = $"就绪 · {modules}模块 / {commands}入口 / {jobs}定时任务";
                        _log(_description); _ready.TrySetResult(true);
                        break;
                    case "event_done":
                        if (_events.TryGetValue(packet.GetProperty("id").GetString()!, out var waiter))
                        {
                            if (packet.GetProperty("ok").GetBoolean())
                            {
                                waiter.TrySetResult(packet.TryGetProperty("continuation_until", out var until) ? until.GetInt64() : 0);
                                _log("修仙指令处理完成。");
                            }
                            else
                            {
                                var code = SafeCode(packet);
                                _log("修仙指令处理未完成，类别=" + code);
                                if (code == "Inactive") waiter.TrySetCanceled();
                                else waiter.TrySetException(new InvalidOperationException("修仙指令处理失败。"));
                            }
                        }
                        break;
                    case "action":
                        await _actionSlots.WaitAsync(ct);
                        var owned = packet.Clone();
                        var sequence = Interlocked.Increment(ref _actionSequence);
                        var work = HandleActionAsync(owned, ct);
                        _actionTasks[sequence] = work;
                        _ = work.ContinueWith(_ => _actionTasks.TryRemove(sequence, out var ignored), CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        break;
                    case "fatal": _log("修仙引擎无法继续，类别=" + SafeCode(packet)); throw new IOException("修仙引擎报告失败。");
                    default: throw new InvalidDataException("未知修仙协议帧。");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) { _log("修仙通信已结束，ErrorType=" + error.GetType().Name); }
        finally
        {
            _description = "已停止或不可用";
            _ready.TrySetException(new IOException("修仙引擎未完成初始化。"));
            foreach (var waiter in _events.Values) waiter.TrySetException(new IOException("修仙引擎连接已结束。"));
            _ = StopProcessAsync();
        }
    }

    /// <summary>将动作转交给宿主受控通道；所有失败仅回传固定错误类型，不把凭据或异常正文传入引擎。</summary>
    private async Task HandleActionAsync(JsonElement packet, CancellationToken ct)
    {
        var id = packet.TryGetProperty("id", out var identity) && identity.ValueKind == JsonValueKind.String ? identity.GetString() : null;
        try
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 64) throw new InvalidDataException("修仙动作编号无效。");
            var action = packet.GetProperty("action").GetString()!;
            var parameters = packet.GetProperty("params");
            JsonElement data;
            if (action == "qqbot.ensure_active")
            {
                long? group = parameters.TryGetProperty("group_id", out var value) && value.ValueKind != JsonValueKind.Null ? value.GetInt64() : null;
                await _actions.EnsureActiveAsync(group, ct);
                data = JsonSerializer.SerializeToElement(new { active = true });
            }
            else data = await _actions.CallAsync(action, parameters, ct);
            await WriteAsync(new { type = "action_result", id, ok = true, data }, ct);
        }
        catch (Exception error)
        {
            try { await WriteAsync(new { type = "action_result", id, ok = false, error_type = error.GetType().Name }, ct); }
            catch (Exception) { /* 管道已关闭，不重发可能已经成功的 OneBot 动作。 */ }
        }
        finally { _actionSlots.Release(); }
    }

    /// <summary>只接受桥约定的错误类别，避免将引擎提供的任意文本写入日志。</summary>
    private static string SafeCode(JsonElement packet)
    {
        var code = packet.TryGetProperty("error_type", out var value) ? value.GetString() : null;
        return code is "ProtocolError" or "InitializationError" or "ModuleLoadError" or "QueueFull" or "InvalidEvent" or
            "ActionError" or "ActionTimeout" or "Inactive" or "Shutdown" or "EventError" or "InternalError" ? code : "InternalError";
    }

    /// <summary>持续排空标准错误避免子进程阻塞；仅保留安全类别，不记录上游原文或聊天内容。</summary>
    private async Task DrainDiagnosticsAsync()
    {
        try
        {
            var lines = new EngineLines(_process.StandardError.BaseStream, 4096);
            var count = 0;
            while (await lines.ReadAsync(_lifetime.Token) is { } line)
            {
                count++;
                if (count > 32) continue;
                if (line.StartsWith("[xiuxian-bridge] ", StringComparison.Ordinal) &&
                    line[17..] is "ProtocolError" or "InitializationError" or "ModuleLoadError" or "EventError" or "InternalError")
                    _log("修仙引擎诊断：" + line[17..]);
            }
        }
        catch (Exception) { /* 关闭中的诊断读取不影响清理。 */ }
    }

    /// <summary>重复停止共用一个任务，包含因框架隔离而触发的后台取消。</summary>
    private Task StopProcessAsync()
    {
        lock (_stopGate) return _stopTask ??= StopCoreAsync();
    }

    /// <summary>先请求引擎关闭；超过短等待只回收当前进程树，Windows Job 同时覆盖宿主异常退出。</summary>
    private async Task StopCoreAsync()
    {
        try
        {
            await _lifetime.CancelAsync();
            using var writeBudget = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            try { await WriteAsync(new { type = "shutdown" }, writeBudget.Token); } catch (Exception) { }
            try { _process.StandardInput.Close(); } catch (Exception) { }
            try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(1500)); }
            catch (TimeoutException) { if (!_process.HasExited) _process.Kill(true); }
        }
        catch (Exception error) { _log("修仙引擎清理，ErrorType=" + error.GetType().Name); }
        finally { _job?.Dispose(); }
    }

    /// <summary>先撤销所有动作等待再结束进程；有界等待后台读取，避免停止宿主时被管道无限阻塞。</summary>
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        await StopProcessAsync();
        try { await Task.WhenAll(_reader, _diagnostics).WaitAsync(TimeSpan.FromMilliseconds(300)); } catch (Exception) { }
        try { await Task.WhenAll(_actionTasks.Values).WaitAsync(TimeSpan.FromMilliseconds(300)); } catch (Exception) { }
        _process.Dispose();
        // 仍在途的取消/HTTP收尾可能读取令牌，交由任务持有源到自然回收，避免提前Dispose。
    }
}

/// <summary>待选择会话达到有界上限，要求宿主明确提示并重建一致的游戏会话。</summary>
internal sealed class EngineContinuationLimitException : Exception { }

/// <summary>按 UTF-8 字节上限读取管道行，防止 ReadLine 无界分配。</summary>
internal sealed class EngineLines(Stream stream, int maximum = 8 * 1024 * 1024)
{
    private readonly byte[] _buffer = new byte[8192];
    private int _offset, _length;

    /// <summary>复用小块读取缓冲，单帧超过限额或未以换行完整结束时明确失败。</summary>
    public async Task<string?> ReadAsync(CancellationToken ct)
    {
        using var frame = new MemoryStream();
        while (true)
        {
            if (_offset == _length)
            {
                _length = await stream.ReadAsync(_buffer, ct); _offset = 0;
                if (_length == 0) return frame.Length == 0 ? null : throw new InvalidDataException("修仙协议帧被截断。");
            }
            var next = Array.IndexOf(_buffer, (byte)10, _offset, _length - _offset);
            var count = next < 0 ? _length - _offset : next - _offset;
            if (frame.Length + count > maximum) throw new InvalidDataException("修仙协议帧超限。");
            frame.Write(_buffer, _offset, count); _offset += count;
            if (next >= 0)
            {
                _offset++;
                return new UTF8Encoding(false, true).GetString(frame.GetBuffer(), 0, (int)frame.Length).TrimEnd('\r');
            }
        }
    }
}
