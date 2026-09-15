using System.Collections.Generic;
using Godot;

namespace BSVRManager.UiKit;

/// <summary>
/// Bridge between a built UI tree and its host (the VR app or the editor preview).
/// The loader routes widget interactions here; the host can look controls up by id.
/// </summary>
public class UiContext
{
    /// <summary>True while the editor manipulates the tree: widgets ignore mouse input.</summary>
    public bool EditMode;

    /// <summary>Action sink: (action, param, source widget).</summary>
    public System.Action<string, string, UiWidget> OnAction;

    /// <summary>Data provider for list widgets, by collection key (e.g. "mods.installed").</summary>
    public System.Func<string, IEnumerable<Dictionary<string, string>>> GetData;

    /// <summary>Image resolver: returns a texture for a src string (path or URL), or null.</summary>
    public System.Func<string, Texture2D> GetImage;

    /// <summary>Global {token} values (e.g. "version") merged into every widget's row context.</summary>
    public readonly Dictionary<string, string> GlobalTokens = new();

    public readonly Dictionary<string, Control> ById = new();

    public void Emit(string action, string param, UiWidget widget)
    {
        if (string.IsNullOrEmpty(action)) return;
        OnAction?.Invoke(action, param ?? "", widget);
    }

    public IEnumerable<Dictionary<string, string>> RowsFor(string dataKey)
        => string.IsNullOrEmpty(dataKey) ? System.Array.Empty<Dictionary<string, string>>() : GetData?.Invoke(dataKey) ?? System.Array.Empty<Dictionary<string, string>>();

    public Texture2D ImageFor(string src) => string.IsNullOrEmpty(src) ? null : GetImage?.Invoke(src);

    // ---- runtime update helpers (host-side convenience) ----

    public void SetText(string id, string text) { if (ById.TryGetValue(id, out var c)) UiRuntime.SetControlText(c, text); }
    public void SetProgress(string id, float percent)
    {
        if (ById.TryGetValue(id, out var c)) UiRuntime.SetProgressValue(c, percent);
    }
    public void SetVisible(string id, bool visible) { if (ById.TryGetValue(id, out var c)) c.Visible = visible; }
    public void SetEnabled(string id, bool enabled)
    {
        if (ById.TryGetValue(id, out var c) && c is Button b) b.Disabled = !enabled;
    }
    public void SetChecked(string id, bool checkedState)
    {
        if (ById.TryGetValue(id, out var c) && c is Button { ToggleMode: true } b) b.ButtonPressed = checkedState;
    }
    public void FillList(string id, IEnumerable<Dictionary<string, string>> rows)
    {
        if (ById.TryGetValue(id, out var c) && c is UiList list) list.Fill(rows);
    }
}

/// <summary>Type-agnostic update helpers used by UiContext and the app runtime.</summary>
public static class UiRuntime
{
    public static void SetControlText(Control c, string text)
    {
        switch (c)
        {
            case Label l: l.Text = text; break;
            case Button b: b.Text = text; break;
            case LineEdit le: le.Text = text; break;
            case UiModal m: m.SetBody(text); break;
        }
    }

    public static void SetProgressValue(Control c, float percent)
    {
        switch (c)
        {
            case ProgressBar pb: pb.Value = percent; break;
            case UiProgress up: up.Value = percent; break;
        }
    }
}
