namespace QQBot.Plugins.Xiuxian;

/// <summary>将发行内只读玩法资源放入插件独立工作区，升级时保留已有存档和配置。</summary>
internal static class EngineAssets
{
    /// <summary>只补充缺失的种子资源；拒绝链接和越界，绝不将种子覆盖到玩家存档上。</summary>
    public static void Prepare(string pluginRoot, CancellationToken ct)
    {
        var source = Path.Combine(pluginRoot, "engine", "data", "xiuxian");
        var destination = Path.Combine(pluginRoot, "data", "data", "xiuxian");
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("缺少完整修仙离线资源。");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(destination, Path.GetRelativePath(source, file)));
            if (!target.StartsWith(Path.GetFullPath(destination) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("离线资源路径越界。");
            RejectLinks(file);
            RejectLinks(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target)) continue;
            // 临时文件同卷原子落地；取消或中断后不留下半份 JSON 供下次初始化读取。
            var temporary = target + ".seed.tmp";
            RejectLinks(temporary);
            File.Copy(file, temporary, true);
            File.Move(temporary, target, false);
        }
    }

    /// <summary>核对路径及父级不是重解析点，防止复制范围指向插件以外的私有目录。</summary>
    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("修仙资源和数据路径不能使用链接。");
    }
}
