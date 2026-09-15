using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace BSVRManager.UiKit;

/// <summary>One widget instance in a screen layout. Positions are absolute within the parent widget.</summary>
public class UiWidget
{
    public string Id = "";
    public string Type = "panel";
    public float X, Y, W = 200, H = 60;
    /// <summary>Press/submit/activate action name consumed by the app runtime.</summary>
    public string Action = "";
    /// <summary>Static parameter passed to the action; may contain {tokens} inside list templates.</summary>
    public string Param = "";
    /// <summary>For list widgets: the template widget rendered once per data row.</summary>
    public UiWidget ItemTemplate;
    public List<UiWidget> Children = new();

    /// <summary>Type-specific properties. Values are string/long/double/bool or List&lt;object&gt;.</summary>
    public Dictionary<string, object> Props = new();

    public UiWidget Clone()
    {
        var w = new UiWidget
        {
            Id = Id, Type = Type, X = X, Y = Y, W = W, H = H,
            Action = Action, Param = Param,
            Props = new Dictionary<string, object>(Props),
        };
        if (ItemTemplate != null) w.ItemTemplate = ItemTemplate.Clone();
        foreach (var c in Children) w.Children.Add(c.Clone());
        return w;
    }
}

/// <summary>A full screen layout: fixed-size canvas (default 1600x900) with absolute-positioned widgets.</summary>
public class UiScreen
{
    public string Name = "screen";
    public int Width = 1600;
    public int Height = 900;
    public string Background = "#14171f";
    public List<UiWidget> Children = new();

    public UiScreen Clone() { var s = new UiScreen { Name = Name, Width = Width, Height = Height, Background = Background }; foreach (var c in Children) s.Children.Add(c.Clone()); return s; }

    public UiWidget Find(string id)
    {
        foreach (var c in Children) { var f = FindIn(c, id); if (f != null) return f; }
        return null;
    }
    static UiWidget FindIn(UiWidget w, string id)
    {
        if (w.Id == id) return w;
        foreach (var c in w.Children) { var f = FindIn(c, id); if (f != null) return f; }
        if (w.ItemTemplate != null) { var f = FindIn(w.ItemTemplate, id); if (f != null) return f; }
        return null;
    }
}

public static class UiSchemaIO
{
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static UiScreen LoadFile(string path) => Parse(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));

    public static void SaveFile(UiScreen screen, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, Serialize(screen));
    }

    public static string Serialize(UiScreen screen)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            WriteScreen(w, screen);
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    public static UiScreen Parse(string json, string name = "screen")
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var s = new UiScreen { Name = name };
        if (root.TryGetProperty("name", out var n)) s.Name = n.GetString();
        if (root.TryGetProperty("width", out var wd)) s.Width = wd.GetInt32();
        if (root.TryGetProperty("height", out var h)) s.Height = h.GetInt32();
        if (root.TryGetProperty("background", out var bg)) s.Background = bg.GetString();
        if (root.TryGetProperty("children", out var ch) && ch.ValueKind == JsonValueKind.Array)
            foreach (var e in ch.EnumerateArray()) s.Children.Add(ReadWidget(e));
        return s;
    }

    static void WriteScreen(Utf8JsonWriter w, UiScreen s)
    {
        w.WriteStartObject();
        w.WriteString("version", "1");
        w.WriteString("name", s.Name);
        w.WriteNumber("width", s.Width);
        w.WriteNumber("height", s.Height);
        w.WriteString("background", s.Background);
        w.WritePropertyName("children");
        w.WriteStartArray();
        foreach (var c in s.Children) WriteWidget(w, c);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    static void WriteWidget(Utf8JsonWriter w, UiWidget widget)
    {
        w.WriteStartObject();
        if (!string.IsNullOrEmpty(widget.Id)) w.WriteString("id", widget.Id);
        w.WriteString("type", widget.Type);
        w.WriteNumber("x", widget.X); w.WriteNumber("y", widget.Y);
        w.WriteNumber("w", widget.W); w.WriteNumber("h", widget.H);
        if (!string.IsNullOrEmpty(widget.Action)) w.WriteString("action", widget.Action);
        if (!string.IsNullOrEmpty(widget.Param)) w.WriteString("param", widget.Param);
        if (widget.Props.Count > 0)
        {
            w.WritePropertyName("props");
            w.WriteStartObject();
            foreach (var kv in widget.Props) WriteValue(w, kv.Key, kv.Value);
            w.WriteEndObject();
        }
        if (widget.ItemTemplate != null)
        {
            w.WritePropertyName("itemTemplate");
            WriteWidget(w, widget.ItemTemplate);
        }
        if (widget.Children.Count > 0)
        {
            w.WritePropertyName("children");
            w.WriteStartArray();
            foreach (var c in widget.Children) WriteWidget(w, c);
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    static void WriteValue(Utf8JsonWriter w, string key, object v)
    {
        switch (v)
        {
            case null: w.WriteNull(key); break;
            case string s: w.WriteString(key, s); break;
            case bool b: w.WriteBoolean(key, b); break;
            case int i: w.WriteNumber(key, i); break;
            case long l: w.WriteNumber(key, l); break;
            case double d: w.WriteNumber(key, d); break;
            case float f: w.WriteNumber(key, f); break;
            case List<object> arr:
                w.WritePropertyName(key);
                w.WriteStartArray();
                WriteArray(w, arr);
                w.WriteEndArray();
                break;
            default: w.WriteString(key, v.ToString()); break;
        }
    }

    // Arrays are written via WriteValue's List case; WriteArray emits the bare elements.
    static void WriteArray(Utf8JsonWriter w, List<object> arr)
    {
        foreach (var e in arr)
        {
            switch (e)
            {
                case string s: w.WriteStringValue(s); break;
                case bool b: w.WriteBooleanValue(b); break;
                case int i: w.WriteNumberValue(i); break;
                case long l: w.WriteNumberValue(l); break;
                case double d: w.WriteNumberValue(d); break;
                default: w.WriteStringValue(e.ToString()); break;
            }
        }
    }

    static UiWidget ReadWidget(JsonElement e)
    {
        var widget = new UiWidget();
        if (e.TryGetProperty("id", out var id)) widget.Id = id.GetString() ?? "";
        if (e.TryGetProperty("type", out var t)) widget.Type = t.GetString() ?? "panel";
        if (e.TryGetProperty("x", out var x)) widget.X = ToF(x);
        if (e.TryGetProperty("y", out var yv)) widget.Y = ToF(yv);
        if (e.TryGetProperty("w", out var w)) widget.W = ToF(w);
        if (e.TryGetProperty("h", out var hv)) widget.H = ToF(hv);
        if (e.TryGetProperty("action", out var a)) widget.Action = a.GetString() ?? "";
        if (e.TryGetProperty("param", out var p)) widget.Param = p.GetString() ?? "";
        if (e.TryGetProperty("props", out var pr) && pr.ValueKind == JsonValueKind.Object)
            foreach (var kv in pr.EnumerateObject()) widget.Props[kv.Name] = ToValue(kv.Value);
        if (e.TryGetProperty("itemTemplate", out var it)) widget.ItemTemplate = ReadWidget(it);
        if (e.TryGetProperty("children", out var ch) && ch.ValueKind == JsonValueKind.Array)
            foreach (var c in ch.EnumerateArray()) widget.Children.Add(ReadWidget(c));
        return widget;
    }

    static float ToF(JsonElement e) => e.ValueKind == JsonValueKind.Number ? (float)e.GetDouble() : 0f;

    static object ToValue(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String: return e.GetString();
            case JsonValueKind.Number: return e.GetDouble();
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Array:
                var list = new List<object>();
                foreach (var i in e.EnumerateArray()) list.Add(ToValue(i));
                return list;
            default: return null;
        }
    }
}

/// <summary>Typed property access over the loose Props dictionary. Numbers may be stored as string/long/double.</summary>
public static class UiProps
{
    public static string Str(this Dictionary<string, object> p, string key, string def = "")
    {
        if (p.TryGetValue(key, out var v))
        {
            if (v is string s) return s;
            if (v != null) return v.ToString();
        }
        return def;
    }

    public static float Float(this Dictionary<string, object> p, string key, float def = 0f)
    {
        if (p.TryGetValue(key, out var v))
        {
            switch (v)
            {
                case string s when float.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var fs): return fs;
                case double d: return (float)d;
                case long l: return l;
                case int i: return i;
                case bool b: return b ? 1 : 0;
            }
        }
        return def;
    }

    public static bool Bool(this Dictionary<string, object> p, string key, bool def = false)
    {
        if (p.TryGetValue(key, out var v))
        {
            switch (v)
            {
                case bool b: return b;
                case string s: return s == "true" || s == "1" || s == "yes";
                case double d: return d != 0;
                case long l: return l != 0;
            }
        }
        return def;
    }

    public static List<string> StrList(this Dictionary<string, object> p, string key)
    {
        var result = new List<string>();
        if (p.TryGetValue(key, out var v))
        {
            if (v is List<object> arr)
                foreach (var e in arr) result.Add(e?.ToString() ?? "");
            else if (v is string s)
                foreach (var part in s.Split(',')) result.Add(part.Trim());
        }
        return result;
    }
}

/// <summary>Replaces {token} placeholders with values from a row/global dictionary.</summary>
public static class UiTokens
{
    public static string Interpolate(string template, IReadOnlyDictionary<string, string> ctx)
    {
        if (string.IsNullOrEmpty(template) || ctx == null || !template.Contains('{')) return template;
        foreach (var kv in ctx)
        {
            if (string.IsNullOrEmpty(kv.Key)) continue;
            template = template.Replace("{" + kv.Key + "}", kv.Value ?? "");
        }
        return template;
    }
}
