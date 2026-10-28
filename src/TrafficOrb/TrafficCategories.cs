namespace TrafficOrb;

public static class TrafficCategories
{
    public const string Unknown = "未归类 / 短连接";
    public static readonly string[] All = ["AI 工具", "视频 / 直播", "社交 / 通讯", "云盘 / 文件", "开发 / 更新", "其他网站 / 服务", Unknown];
    private static readonly (string Name, string[] Domains)[] Groups =
    [
        (All[0], ["openai.com", "chatgpt.com", "oaistatic.com", "oaiusercontent.com", "anthropic.com", "claude.ai", "cursor.com", "cursor.sh", "githubcopilot.com", "copilot.microsoft.com", "deepseek.com", "perplexity.ai", "gemini.google.com", "aistudio.google.com", "generativelanguage.googleapis.com"]),
        (All[1], ["youtube.com", "youtube-nocookie.com", "googlevideo.com", "ytimg.com", "netflix.com", "nflxvideo.net", "nflximg.net", "bilibili.com", "bilivideo.com", "bilivideo.cn", "douyin.com", "douyinvod.com", "tiktok.com", "tiktokcdn.com", "twitch.tv", "ttvnw.net", "vimeo.com"]),
        (All[2], ["telegram.org", "t.me", "telegram-cdn.org", "discord.com", "discordapp.com", "discord.gg", "whatsapp.com", "whatsapp.net", "wechat.com", "weixin.qq.com", "x.com", "twitter.com", "t.co", "twimg.com", "facebook.com", "fbcdn.net", "instagram.com", "cdninstagram.com"]),
        (All[3], ["drive.google.com", "docs.google.com", "dropbox.com", "dropboxusercontent.com", "onedrive.com", "onedrive.live.com", "1drv.ms", "sharepoint.com", "aliyundrive.com", "alipan.com", "pan.baidu.com", "pan.quark.cn", "box.com", "wetransfer.com"]),
        (All[4], ["github.com", "githubusercontent.com", "githubassets.com", "npmjs.org", "npmjs.com", "pypi.org", "pythonhosted.org", "nuget.org", "docker.io", "docker.com", "windowsupdate.com", "update.microsoft.com", "dl.delivery.mp.microsoft.com", "msedge.net"])
    ];
    public static string Classify(string host, string process)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        foreach (var group in Groups)
            if (group.Domains.Any(domain => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal))) return group.Name;
        string app = process.ToLowerInvariant();
        if (app is "codex.exe" or "chatgpt.exe" or "claude.exe" or "cursor.exe") return All[0];
        if (app is "telegram.exe" or "discord.exe" or "wechat.exe" or "weixin.exe" or "whatsapp.exe") return All[2];
        if (app is "onedrive.exe" or "dropbox.exe" or "baidunetdisk.exe" or "aria2c.exe" or "qbittorrent.exe") return All[3];
        return host.Length == 0 ? Unknown : All[5];
    }
}
