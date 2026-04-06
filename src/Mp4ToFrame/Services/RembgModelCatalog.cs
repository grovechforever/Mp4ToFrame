namespace Mp4ToFrame.Services;

/// <summary>rembg CLI -m 可选模型（与 rembg p --help 对齐）。</summary>
public static class RembgModelCatalog
{
    public static readonly string[] BuiltinIds =
    {
        "isnet-general-use",
        "isnet-anime",
        "birefnet-general",
        "birefnet-general-lite",
        "birefnet-portrait",
        "u2net",
        "u2netp",
        "u2net_human_seg",
        "u2net_cloth_seg",
        "silueta",
        "sam",
        "bria-rmbg",
        "birefnet-dis",
        "birefnet-hrsod",
        "birefnet-cod",
        "birefnet-massive",
        "dis_custom",
        "u2net_custom",
        "ben_custom"
    };

    public static readonly string[] BuiltinLabels =
    {
        "ISNet 通用实景 (isnet-general-use)",
        "ISNet 二次元 / 拟人 (isnet-anime)",
        "BiRefNet 通用 (birefnet-general)",
        "BiRefNet 轻量 (birefnet-general-lite)",
        "BiRefNet 肖像 (birefnet-portrait)",
        "U²-Net 经典 (u2net)",
        "U²-Net 轻量 (u2netp)",
        "U²-Net 人像 (u2net_human_seg)",
        "U²-Net 服饰 (u2net_cloth_seg)",
        "剪影 (silueta)",
        "SAM (sam)",
        "BRIA RMBG (bria-rmbg)",
        "BiRefNet DIS (birefnet-dis)",
        "BiRefNet HRSOD (birefnet-hrsod)",
        "BiRefNet COD (birefnet-cod)",
        "BiRefNet Massive (birefnet-massive)",
        "DIS 自定义 (dis_custom)",
        "U²-Net 自定义 (u2net_custom)",
        "Ben 自定义 (ben_custom)"
    };

    public static readonly string[] AllPopupLabels = BuildPopupLabels();

    public static int CustomPopupIndex => BuiltinIds.Length;

    public static bool IsCustomPopupIndex(int index) => index == CustomPopupIndex;

    public static string ResolveModelId(int popupIndex, string customId)
    {
        if (popupIndex >= 0 && popupIndex < BuiltinIds.Length)
            return BuiltinIds[popupIndex];
        var t = customId?.Trim();
        return string.IsNullOrEmpty(t) ? "isnet-general-use" : t;
    }

    static string[] BuildPopupLabels()
    {
        var n = BuiltinLabels.Length;
        var a = new string[n + 1];
        Array.Copy(BuiltinLabels, a, n);
        a[n] = "自定义 (填写 rembg -m 模型名)…";
        return a;
    }
}
