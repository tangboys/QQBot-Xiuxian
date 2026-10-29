using System.Text.Json;
using System.Text.RegularExpressions;
using QQBot.Abstractions;

namespace QQBot.Plugins.Xiuxian;

/// <summary>从固定上游静态清单建立轻量路由，普通群消息不进入 Python 游戏引擎。</summary>
internal sealed class EngineCommands
{
    private readonly HashSet<string> _exact = new(StringComparer.Ordinal);
    private readonly List<string> _prefixes = [];
    private readonly List<Regex> _patterns = [];

    /// <summary>读取所有 matcher 和别名；无法解释的入口明确报错，避免静默遗漏玩法。</summary>
    public EngineCommands(string manifest)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(manifest));
        foreach (var file in json.RootElement.GetProperty("files").EnumerateArray())
        foreach (var item in file.GetProperty("commands").EnumerateArray())
        {
            var matcher = item.GetProperty("matcher").GetString();
            var command = item.GetProperty("command");
            if (command.ValueKind == JsonValueKind.String) Add(command.GetString()!, matcher);
            else if (command.ValueKind == JsonValueKind.Array)
                foreach (var value in command.EnumerateArray()) Add(value.GetString()!, matcher);
            else throw new InvalidDataException("修仙指令清单包含未支持的动态入口。");
            if (item.TryGetProperty("aliases", out var aliases))
                foreach (var value in aliases.EnumerateArray()) Add(value.GetString()!, matcher);
        }
    }

    /// <summary>只编译固定上游正则并设置超时；普通命令用字符串匹配。</summary>
    private void Add(string command, string? matcher)
    {
        switch (matcher)
        {
            case "on_command": _prefixes.Add(command); break;
            case "on_fullmatch": _exact.Add(command); break;
            case "on_regex": _patterns.Add(new Regex(command, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(30))); break;
            default: throw new InvalidDataException("修仙 matcher 类型不支持。");
        }
    }

    /// <summary>识别原版完整指令以及 /修仙 的便捷入口，保留独立的状态查询。</summary>
    public string? Match(BotMessage message)
    {
        var text = message.Text.Trim();
        if (text.StartsWith('/')) text = text[1..].TrimStart();
        if (text == "修仙") return "修仙帮助";
        if (text.StartsWith("修仙 ", StringComparison.Ordinal))
        {
            text = text[3..].Trim();
            text = text switch
            {
                "开始" => "我要修仙", "帮助" or "菜单" => "修仙帮助", "签到" => "修仙签到",
                "属性" => "我的修仙信息", "状态" => "__status__", _ => text
            };
            if (text == "__status__") return text;
        }
        if (_exact.Contains(text) || _prefixes.Any(prefix => text.StartsWith(prefix, StringComparison.Ordinal))) return text;
        return _patterns.Any(pattern => pattern.IsMatch(text)) ? text : null;
    }
}
