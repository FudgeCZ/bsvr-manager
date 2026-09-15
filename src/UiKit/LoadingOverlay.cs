using System;
using Godot;

namespace BSVRManager.UiKit;

/// <summary>Full-cover dim overlay with a spinning ring and status text. Shown while anything loads.</summary>
public partial class LoadingOverlay : Control
{
    readonly Label _text;
    float _angle;

    public LoadingOverlay()
    {
        Name = "LoadingOverlay";
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore; // indicator only — never blocks interaction
        Visible = false;

        var dim = new ColorRect { Color = new Color(0.03f, 0.04f, 0.07f, 0.72f) };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        dim.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(dim);

        var card = new Panel();
        card.AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(0.13f, 0.16f, 0.22f, 0.96f), 18f));
        card.SetAnchorsPreset(LayoutPreset.Center);
        card.CustomMinimumSize = new Vector2(560, 220);
        card.Position = new Vector2(-280, -110);
        card.Size = new Vector2(560, 220);
        card.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(card);

        _text = new Label();
        _text.SetAnchorsPreset(LayoutPreset.FullRect);
        _text.OffsetTop = 30;
        _text.HorizontalAlignment = HorizontalAlignment.Center;
        _text.VerticalAlignment = VerticalAlignment.Center;
        _text.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _text.AddThemeFontSizeOverride("font_size", 26);
        _text.AddThemeColorOverride("font_color", UiTheme.TextDefault);
        card.AddChild(_text);
    }

    public void ShowLoading(string text)
    {
        _text.Text = string.IsNullOrEmpty(text) ? "Loading…" : text;
        Visible = true;
        MoveToFront();
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        _angle += (float)delta * 4.5f;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (!Visible) return;
        var center = new Vector2(Size.X / 2f, Size.Y / 2f - 40f);
        var radius = 34f;
        for (int i = 0; i < 10; i++)
        {
            var a = _angle + i * MathF.Tau / 10f;
            var alpha = 0.15f + 0.85f * (i / 10f);
            var p = center + new Vector2(MathF.Cos(a), MathF.Sin(a)) * radius;
            DrawCircle(p, 5.5f, new Color(0.31f, 0.55f, 1f, alpha));
        }
    }
}
