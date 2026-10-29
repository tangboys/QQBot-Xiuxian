using System.Text.Json;
using System.Threading.Channels;
using QQBot.Abstractions;

namespace QQBot.Plugins.Xiuxian;

/// <summary>将完整修仙引擎接入 QQBot；普通消息只做路由，游戏在独立有界工作队列中执行。</summary>
public sealed class XiuxianPlugin : IBotPlugin
{
    private readonly string _root = Path.GetDirectoryName(typeof(XiuxianPlugin).Assembly.Location)!;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Channel<(BotMessage Message, string Body, bool Deferred)> _pending = Channel.CreateBounded<(BotMessage, string, bool)>(new BoundedChannelOptions(64)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private EngineCommands? _commands;
    private IPluginActionChannel? _actions;
    private EngineSession? _session;
    private Task _worker = Task.CompletedTask;
    private Action<string> _log = _ => { };
    private int _queued;
    private DateTimeOffset _retryAfter;

    public PluginMetadata Metadata { get; } = new("xiuxian", "修仙 · 群聊世界", "1.0.0");

    /// <summary>仅加载固定命令索引，不在初始化时启动游戏、恢复任务或调用 QQ 接口。</summary>
    public ValueTask OnLoadAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _commands = new EngineCommands(Path.Combine(_root, "engine", "qqbot", "audit", "feature-manifest.json"));
        _log = context.Log;
        _log("修仙指令索引就绪，首次群指令时启动完整引擎。");
        return ValueTask.CompletedTask;
    }

    /// <summary>过滤非修仙消息、提供状态与群聊提示，快速投递后立即释放宿主消息线程。</summary>
    public async ValueTask OnMessageAsync(BotMessage message, IPluginContext context, CancellationToken cancellationToken)
    {
        var indexed = _commands!.Match(message);
        var deferred = indexed is null && _session?.IsDispatching(message) == true;
        var command = indexed;
        if (indexed != "__status__" && (_session?.CanContinue(message) == true || deferred)) command = message.Text;
        if (command is null) return;
        if (command == "__status__")
        {
            var state = _session?.ReadyDescription ?? "尚未启动；在群里发送 /修仙 即可准备引擎。";
            await context.Messages.ReplyAsync(message, $"修仙引擎：{state}\n等待处理：{Volatile.Read(ref _queued)} / 64\n版本：1.0.0 · 上游 128e316", cancellationToken);
            return;
        }
        if (message.GroupId is null)
        {
            await context.Messages.ReplyAsync(message, "修仙是群聊游戏，请在已开启插件的群发送 /修仙 查看菜单。", cancellationToken);
            return;
        }
        if (_actions is null)
        {
            _actions = context.OpenOneBotActionChannel();
            _worker = Task.Run(ProcessQueueAsync, CancellationToken.None);
        }
        // 不等待满队列、不丢弃已经接收的指令；明确反馈让用户稍后主动操作。
        Interlocked.Increment(ref _queued);
        if (!_pending.Writer.TryWrite((message, command, deferred)))
        {
            Interlocked.Decrement(ref _queued);
            await context.Messages.ReplyAsync(message, "修仙请求较多，当前指令尚未执行，请稍后再试。", cancellationToken);
        }
    }

    /// <summary>按顺序处理已接收指令；故障不重放，以免重复结算资产或奖励。</summary>
    private async Task ProcessQueueAsync()
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token, _actions!.Stopping);
        var ct = lifetime.Token;
        try
        {
            await foreach (var (message, body, deferred) in _pending.Reader.ReadAllAsync(ct))
            {
                Interlocked.Decrement(ref _queued);
                try
                {
                    // 提示发送完成前暂存的原文，只有真实临时响应器仍在等待时才进入引擎。
                    if (deferred && _session?.CanContinue(message) != true) continue;
                    await _actions.EnsureActiveAsync(message.GroupId, ct);
                    if (_retryAfter > DateTimeOffset.UtcNow)
                    {
                        await ReplyAsync(message, "修仙引擎暂不可用，请查看运行日志，稍后发送 /修仙 状态。", ct);
                        continue;
                    }
                    if (_session is null || !_session.IsAlive)
                    {
                        if (_session is not null) await _session.DisposeAsync();
                        await ReplyAsync(message, "正在准备修仙世界，首次启动稍需等待；其他插件仍可正常使用。", ct);
                        await Task.Run(() => EngineAssets.Prepare(_root, ct), ct);
                        _session = await EngineSession.StartAsync(_root, message.SelfId, _actions, _log, ct);
                    }
                    await _session.DispatchAsync(message.DeduplicationKey, CreateEvent(message, body), ct);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { _log("修仙请求因插件或本群开关关闭而取消。"); }
                catch (Exception error) when (!ct.IsCancellationRequested)
                {
                    _retryAfter = DateTimeOffset.UtcNow.AddSeconds(30);
                    _log("修仙请求失败，ErrorType=" + error.GetType().Name + "；未自动重放。");
                    if (_session is not null) { await _session.DisposeAsync(); _session = null; }
                    try
                    {
                        await ReplyAsync(message, error is EngineContinuationLimitException
                            ? "修仙待选择会话已达 256 个上限，引擎已停止，未完成的选择已取消。请稍后重新发起操作；已执行的领取或交易不会重放。"
                            : "修仙请求未能正常结束。若涉及领取、交易或突破，请先查看角色和背包确认结果，避免重复操作；详情见运行日志。", ct);
                    }
                    catch (Exception) { /* 关闭或通道不可用时不再发送补偿消息。 */ }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) when (ct.IsCancellationRequested)
        { _log("修仙停止收尾，ErrorType=" + error.GetType().Name); }
        finally { Interlocked.Exchange(ref _queued, 0); }
    }

    /// <summary>经实时开关和生命周期校验发送准备或故障提示，不保留即时回调发送器。</summary>
    private async Task ReplyAsync(BotMessage message, string text, CancellationToken ct)
    {
        await _actions!.CallAsync("send_group_msg", JsonSerializer.SerializeToElement(new
            { group_id = message.GroupId!.Value, message = text, auto_escape = true }), ct);
    }

    /// <summary>构造 SDK 可识别的完整事件；sender 权限由引擎查询真实资料，不凭空赋予管理员身份。</summary>
    private static JsonElement CreateEvent(BotMessage source, string body)
    {
        var segments = new List<object> { new { type = "text", data = new { text = body } } };
        foreach (var segment in source.Segments)
        {
            // 游戏只使用当前指令和提及目标；去掉引用段，避免适配器额外查询被引用消息。
            if (segment.Type is "text" or "reply") continue;
            if (segment.Type == "at" && segment.Data.TryGetProperty("qq", out var target) && target.ToString() == source.SelfId.ToString()) continue;
            segments.Add(new { type = segment.Type, data = segment.Data });
        }
        return JsonSerializer.SerializeToElement(new
        {
            time = source.Timestamp.ToUnixTimeSeconds(), self_id = source.SelfId, post_type = "message",
            message_type = source.Kind == MessageKind.Group ? "group" : "private", sub_type = source.Kind == MessageKind.Group ? "normal" : "friend",
            message_id = source.MessageId, user_id = source.UserId, group_id = source.GroupId,
            message = segments, raw_message = body, font = 0, to_me = true,
            sender = new { user_id = source.UserId, nickname = source.UserId.ToString(), card = "", role = "member" }
        });
    }

    /// <summary>停止接收并撤销队列，等待自有进程有界退出；不会操作其他 QQ 或 Python 实例。</summary>
    public async ValueTask OnUnloadAsync(CancellationToken cancellationToken)
    {
        _pending.Writer.TryComplete();
        await _stopping.CancelAsync();
        if (_session is not null) await _session.DisposeAsync();
        try { await _worker.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken); }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException) { }
        _log("修仙引擎已停止接收；未完成指令不会自动重放。");
        // 若工作任务尚在 .NET 文件 IO 收尾，保留取消源供其观察，不提前 Dispose。
        if (_worker.IsCompleted) _stopping.Dispose();
    }
}
