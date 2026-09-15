using Godot;

namespace BSVRManager.UiKit;

/// <summary>Color parsing + default style factories shared by the loader and the editor.</summary>
public static class UiTheme
{
    public static Color ParseColor(string hex, Color def)
    {
        if (string.IsNullOrWhiteSpace(hex)) return def;
        var s = hex.Trim().TrimStart('#');
        try
        {
            switch (s.Length)
            {
                case 3: return new Color(System.Convert.ToInt16(s.Substring(0,1),16)/15f, System.Convert.ToInt16(s.Substring(1,1),16)/15f, System.Convert.ToInt16(s.Substring(2,1),16)/15f);
                case 6: return Color.FromHtml(s);
                case 8: return Color.FromHtml(s);
            }
        }
        catch { }
        return def;
    }

    public static string ToHex(Color c) => "#" + c.ToHtml(false);

    public static Color Lighten(Color c, float amount = 0.12f) => new(Mathf.Min(c.R + amount, 1), Mathf.Min(c.G + amount, 1), Mathf.Min(c.B + amount, 1), c.A);
    public static Color Darken(Color c, float amount = 0.12f) => new(Mathf.Max(c.R - amount, 0), Mathf.Max(c.G - amount, 0), Mathf.Max(c.B - amount, 0), c.A);

    public static readonly Color TextDefault = new(0.92f, 0.94f, 0.97f);
    public static readonly Color TextDim = new(0.62f, 0.66f, 0.74f);
    public static readonly Color Accent = new(0.28f, 0.49f, 0.95f);   // #477cf2
    public static readonly Color BgPanel = new(0.14f, 0.16f, 0.21f);
    public static readonly Color BgCard = new(0.19f, 0.22f, 0.29f);
    public static readonly Color Good = new(0.30f, 0.78f, 0.47f);
    public static readonly Color Bad = new(0.91f, 0.34f, 0.29f);
    public static readonly Color Warn = new(0.95f, 0.68f, 0.26f);

    public static StyleBoxFlat Box(Color bg, float radius = 8f, Color? border = null, float borderWidth = 0f)
    {
        var sb = new StyleBoxFlat { BgColor = bg };
        int r = (int)radius;
        sb.CornerRadiusTopLeft = r; sb.CornerRadiusTopRight = r;
        sb.CornerRadiusBottomLeft = r; sb.CornerRadiusBottomRight = r;
        if (border != null && borderWidth > 0)
        {
            sb.BorderColor = border.Value;
            sb.BorderWidthLeft = (int)borderWidth; sb.BorderWidthRight = (int)borderWidth;
            sb.BorderWidthTop = (int)borderWidth; sb.BorderWidthBottom = (int)borderWidth;
        }
        sb.ContentMarginLeft = 10; sb.ContentMarginRight = 10;
        sb.ContentMarginTop = 6; sb.ContentMarginBottom = 6;
        return sb;
    }

    /// <summary>Default property values per widget type, used by the editor when creating widgets.</summary>
    public static System.Collections.Generic.Dictionary<string, object> DefaultsFor(string type) => type switch
    {
        "label" => new() { ["text"] = "Label", ["fontSize"] = 24.0, ["color"] = ToHex(TextDefault), ["align"] = "left" },
        "button" => new() { ["text"] = "Button", ["fontSize"] = 22.0, ["color"] = ToHex(TextDefault), ["bgColor"] = ToHex(BgCard), ["radius"] = 10.0 },
        "panel" => new() { ["bgColor"] = ToHex(BgPanel), ["radius"] = 12.0 },
        "toggle" => new() { ["text"] = "Toggle", ["fontSize"] = 22.0, ["onBg"] = ToHex(Good), ["offBg"] = ToHex(BgCard), ["radius"] = 10.0 },
        "slider" => new() { ["min"] = 0.0, ["max"] = 100.0, ["value"] = 50.0, ["step"] = 1.0 },
        "progress" => new() { ["value"] = 50.0, ["color"] = ToHex(Accent), ["bgColor"] = ToHex(BgCard), ["radius"] = 6.0, ["showPercent"] = true },
        "input" => new() { ["placeholder"] = "Type here", ["fontSize"] = 22.0 },
        "dropdown" => new() { ["options"] = "Option A,Option B,Option C", ["fontSize"] = 22.0 },
        "list" => new() { ["itemHeight"] = 84.0, ["gap"] = 8.0, ["dataKey"] = "" },
        "image" => new() { ["src"] = "", ["fit"] = "fit" },
        "tabs" => new() { ["fontSize"] = 22.0 },
        "modal" => new() { ["title"] = "Title", ["text"] = "Message text", ["buttons"] = "OK,Cancel" },
        _ => new(),
    };
}
