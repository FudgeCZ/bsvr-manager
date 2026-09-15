using System.Collections.Generic;
using Godot;

namespace BSVRManager.UiKit;

/// <summary>Builds a live Godot Control tree from a UiScreen/UiWidget definition.</summary>
public static class UiLoader
{
    public static Control BuildScreen(UiScreen screen, UiContext ctx)
    {
        var root = new Control();
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.CustomMinimumSize = new Vector2(screen.Width, screen.Height);
        root.ClipContents = true;

        var bg = new ColorRect { Color = UiTheme.ParseColor(screen.Background, new Color(0.08f, 0.09f, 0.12f)) };
        bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        bg.MouseFilter = Control.MouseFilterEnum.Ignore;
        root.AddChild(bg);

        foreach (var child in screen.Children)
            root.AddChild(BuildWidget(child, ctx));
        return root;
    }

    public static Control BuildWidget(UiWidget widget, UiContext ctx, Dictionary<string, string> row = null)
    {
        var tokens = row;
        var control = widget.Type switch
        {
            "label" => BuildLabel(widget, ctx, tokens),
            "button" => BuildButton(widget, ctx, tokens),
            "panel" => BuildPanel(widget),
            "toggle" => BuildToggle(widget, ctx, tokens),
            "slider" => BuildSlider(widget, ctx, tokens),
            "progress" => BuildProgress(widget),
            "input" => BuildInput(widget, ctx, tokens),
            "dropdown" => BuildDropdown(widget, ctx, tokens),
            "list" => BuildList(widget, ctx),
            "image" => BuildImage(widget, ctx, tokens),
            "tabs" => BuildTabs(widget, ctx),
            "modal" => BuildModal(widget, ctx, tokens),
            _ => BuildPanel(widget),
        };

        control.Position = new Vector2(widget.X, widget.Y);
        control.Size = new Vector2(widget.W, widget.H);
        control.MouseFilter = ctx?.EditMode == true
            ? Control.MouseFilterEnum.Ignore
            : (control is Panel or ColorRect or UiProgress && !IsInteractive(widget.Type)
                ? Control.MouseFilterEnum.Ignore
                : Control.MouseFilterEnum.Stop);

        // panels are plain containers: recurse into children with the same row context
        if (widget.Type == "panel" || IsContainerLike(widget.Type))
        {
            foreach (var child in widget.Children)
                control.AddChild(BuildWidget(child, ctx, row));
        }

        if (row != null && !string.IsNullOrEmpty(widget.Id) && ctx != null)
        {
            // ids inside list templates are per-row; only register the first occurrence
            if (!ctx.ById.ContainsKey(widget.Id)) ctx.ById[widget.Id] = control;
        }
        else if (!string.IsNullOrEmpty(widget.Id) && ctx != null)
        {
            ctx.ById[widget.Id] = control;
        }
        return control;
    }

    static bool IsInteractive(string type) => type is "button" or "toggle" or "slider" or "input" or "dropdown" or "list" or "modal" or "tabs";
    static bool IsContainerLike(string type) => type is "panel" or "image";

    // ---------------- per-type builders ----------------

    static Control BuildLabel(UiWidget w, UiContext ctx, Dictionary<string, string> row)
    {
        var l = new Label();
        l.Text = UiTokens.Interpolate(w.Props.Str("text", "Label"), row);
        l.AddThemeFontSizeOverride("font_size", (int)w.Props.Float("fontSize", 24));
        l.AddThemeColorOverride("font_color", UiTheme.ParseColor(w.Props.Str("color"), UiTheme.TextDefault));
        l.HorizontalAlignment = w.Props.Str("align", "left") switch
        {
            "center" => HorizontalAlignment.Center,
            "right" => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Left,
        };
        l.VerticalAlignment = w.Props.Str("valign", "center") switch
        {
            "top" => VerticalAlignment.Top,
            "bottom" => VerticalAlignment.Bottom,
            _ => VerticalAlignment.Center,
        };
        if (w.Props.Bool("wrap")) l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.MouseFilter = Control.MouseFilterEnum.Ignore;
        return l;
    }

    static Control BuildButton(UiWidget w, UiContext ctx, Dictionary<string, string> row)
    {
        var b = new Button();
        b.Text = UiTokens.Interpolate(w.Props.Str("text", "Button"), row);
        b.AddThemeFontSizeOverride("font_size", (int)w.Props.Float("fontSize", 22));
        b.AddThemeColorOverride("font_color", UiTheme.ParseColor(w.Props.Str("color"), UiTheme.TextDefault));
        b.AddThemeColorOverride("font_hover_color", UiTheme.ParseColor(w.Props.Str("color"), UiTheme.TextDefault));
        b.AddThemeColorOverride("font_pressed_color", UiTheme.TextDefault);
        b.AddThemeColorOverride("font_disabled_color", UiTheme.TextDim);

        var bg = UiTheme.ParseColor(w.Props.Str("bgColor"), UiTheme.BgCard);
        float radius = w.Props.Float("radius", 10);
        var border = w.Props.Str("borderColor");
        var normal = UiTheme.Box(bg, radius,
            border == "" ? null : UiTheme.ParseColor(border, UiTheme.Accent),
            border == "" ? 0 : w.Props.Float("borderWidth", 2));
        b.AddThemeStyleboxOverride("normal", normal);
        b.AddThemeStyleboxOverride("hover", UiTheme.Box(w.Props.Str("hoverBg") == "" ? UiTheme.Lighten(bg) : UiTheme.ParseColor(w.Props.Str("hoverBg"), UiTheme.Lighten(bg)), radius));
        b.AddThemeStyleboxOverride("pressed", UiTheme.Box(w.Props.Str("pressedBg") == "" ? UiTheme.Darken(bg) : UiTheme.ParseColor(w.Props.Str("pressedBg"), UiTheme.Darken(bg)), radius));
        b.AddThemeStyleboxOverride("focus", UiTheme.Box(bg, radius));
        b.AddThemeStyleboxOverride("disabled", UiTheme.Box(UiTheme.Darken(bg, 0.2f), radius));

        string action = w.Action, param = UiTokens.Interpolate(w.Param, row);
        b.Pressed += () => ctx?.Emit(action, param, w);
        return b;
    }

    static Control BuildPanel(UiWidget w)
    {
        var p = new Panel();
        float radius = w.Props.Float("radius", 12);
        var bg = UiTheme.ParseColor(w.Props.Str("bgColor"), UiTheme.BgPanel);
        var border = w.Props.Str("borderColor");
        p.AddThemeStyleboxOverride("panel", UiTheme.Box(bg, radius,
            border == "" ? null : UiTheme.ParseColor(border, UiTheme.Accent),
            border == "" ? 0 : w.Props.Float("borderWidth", 1)));
        return p;
    }

    static Control BuildToggle(UiWidget w, UiContext ctx, Dictionary<string, string> row)
    {
        var b = new Button { ToggleMode = true };
        // checked state may come from a row token ("{sel}" -> "1"/"0") or a literal bool
        var checkedStr = UiTokens.Interpolate(w.Props.Str("checked"), row);
        b.ButtonPressed = checkedStr is "1" or "true" || (checkedStr == "" && w.Props.Bool("checked"));
        b.Text = UiTokens.Interpolate(w.Props.Str("text", "Toggle"), row);
        b.AddThemeFontSizeOverride("font_size", (int)w.Props.Float("fontSize", 22));
        b.AddThemeColorOverride("font_color", UiTheme.TextDefault);
        float radius = w.Props.Float("radius", 10);
        var onBg = UiTheme.ParseColor(w.Props.Str("onBg"), UiTheme.Good);
        var offBg = UiTheme.ParseColor(w.Props.Str("offBg"), UiTheme.BgCard);

        void Apply()
        {
            var bg = b.ButtonPressed ? onBg : offBg;
            b.AddThemeStyleboxOverride("normal", UiTheme.Box(bg, radius));
            b.AddThemeStyleboxOverride("hover", UiTheme.Box(UiTheme.Lighten(bg, 0.06f), radius));
            b.AddThemeStyleboxOverride("pressed", UiTheme.Box(bg, radius));
            b.AddThemeStyleboxOverride("focus", UiTheme.Box(bg, radius));
        }
        Apply();

        string action = w.Action, param = UiTokens.Interpolate(w.Param, row);
        b.Toggled += on => { Apply(); ctx?.Emit(action, (string.IsNullOrEmpty(param) ? "" : param + "|") + (on ? "on" : "off"), w); };
        return b;
    }

    static Control BuildSlider(UiWidget w, UiContext ctx, Dictionary<string, string> row)
    {
        var s = new HSlider
        {
            MinValue = w.Props.Float("min"),
            MaxValue = w.Props.Float("max", 100),
            Step = w.Props.Float("step", 1),
            Value = w.Props.Float("value"),
        };
        s.CustomMinimumSize = new Vector2(0, 32);
        s.SizeFlagsVertical = (Control.SizeFlags)4; // shrink-center
        s.AddThemeStyleboxOverride("slider", UiTheme.Box(UiTheme.BgCard, 4f));
        s.AddThemeStyleboxOverride("grabber_area", UiTheme.Box(UiTheme.Accent, 4f));
        s.AddThemeStyleboxOverride("grabber_area_highlight", UiTheme.Box(UiTheme.Lighten(UiTheme.Accent), 4f));
        s.AddThemeIconOverride("grabber", Icons.Grabber());
        s.AddThemeIconOverride("grabber_highlight", Icons.Grabber());
        s.AddThemeIconOverride("grabber_disabled", Icons.Grabber());

        string action = w.Action, param = UiTokens.Interpolate(w.Param, row);
        s.ValueChanged += v => ctx?.Emit(action, $"{param}|{v:0.#}", w);
        return s;
    }

    static Control BuildProgress(UiWidget w)
    {
        var p = new UiProgress();
        p.Configure(w);
        return p;
    }

    static Control BuildInput(UiWidget w, UiContext ctx, Dictionary<string, string> row)
    {
        var le = new LineEdit();
        le.Text = UiTokens.Interpolate(w.Props.Str("text"), row);
        le.PlaceholderText = w.Props.Str("placeholder");
        le.Secret = w.Props.Bool("secret");
        le.AddThemeFontSizeOverride("font_size", (int)w.Props.Float("fontSize", 22));
        le.AddThemeColorOverride("font_color", UiTheme.TextDefault);
        le.AddThemeColorOverride("placeholder_color", UiTheme.TextDim);
        le.AddThemeStyleboxOverride("normal", UiTheme.Box(UiTheme.BgCard, 8f, UiTheme.Darken(UiTheme.BgCard, 0.15f), 2));
        le.AddThemeStyleboxOverride("focus", UiTheme.Box(UiTheme.BgCard, 8f, UiTheme.Accent, 2));

        string action = w.Action, param = UiTokens.Interpolate(w.Param, row);
        le.TextSubmitted += text => ctx?.Emit(action, string.IsNullOrEmpty(param) ? text : param + "|" + text, w);
        return le;
    }

    static Control BuildDropdown(UiWidget w, UiContext ctx, Dictionary<string, string> row)
    {
        var ob = new OptionButton();
        ob.AddThemeFontSizeOverride("font_size", (int)w.Props.Float("fontSize", 22));
        var options = w.Props.StrList("options");
        int selected = (int)w.Props.Float("selected");
        foreach (var opt in options) ob.AddItem(opt);
        if (options.Count > 0) ob.Selected = Mathf.Clamp(selected, 0, options.Count - 1);
        ob.AddThemeStyleboxOverride("normal", UiTheme.Box(UiTheme.BgCard, 8f));
        ob.AddThemeStyleboxOverride("hover", UiTheme.Box(UiTheme.Lighten(UiTheme.BgCard), 8f));
        ob.AddThemeStyleboxOverride("pressed", UiTheme.Box(UiTheme.Darken(UiTheme.BgCard), 8f));
        ob.AddThemeStyleboxOverride("focus", UiTheme.Box(UiTheme.BgCard, 8f));

        string action = w.Action, param = UiTokens.Interpolate(w.Param, row);
        ob.ItemSelected += idx =>
        {
            string value = idx >= 0 && idx < options.Count ? options[(int)idx] : "";
            ctx?.Emit(action, string.IsNullOrEmpty(param) ? value : param + "|" + value, w);
        };
        return ob;
    }

    static Control BuildList(UiWidget w, UiContext ctx)
    {
        var list = new UiList();
        list.Configure(w, ctx);
        var dataKey = w.Props.Str("dataKey");
        if (ctx != null && !string.IsNullOrEmpty(dataKey))
            list.Fill(ctx.RowsFor(dataKey));
        else if (ctx == null || ctx.EditMode)
            list.Fill(null); // sample rows in edit mode
        return list;
    }

    static Control BuildImage(UiWidget w, UiContext ctx, Dictionary<string, string> row)
    {
        var t = new TextureRect();
        t.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
        t.StretchMode = w.Props.Str("fit", "fit") switch
        {
            "cover" => TextureRect.StretchModeEnum.KeepAspectCovered,
            "fill" => TextureRect.StretchModeEnum.Scale,
            _ => TextureRect.StretchModeEnum.KeepAspectCentered,
        };
        var src = UiTokens.Interpolate(w.Props.Str("src"), row);
        var tex = ctx?.ImageFor(src);
        if (tex != null) t.Texture = tex;
        else
        {
            var holder = new Panel();
            holder.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.BgCard, 8f));
        }
        return t;
    }

    static Control BuildTabs(UiWidget w, UiContext ctx)
    {
        var tc = new TabContainer();
        tc.AddThemeFontSizeOverride("font_size", (int)w.Props.Float("fontSize", 22));
        tc.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.BgPanel, 10f));
        tc.AddThemeStyleboxOverride("tab_selected_background", UiTheme.Box(UiTheme.BgCard, 6f));
        tc.AddThemeStyleboxOverride("tab_background", UiTheme.Box(UiTheme.Darken(UiTheme.BgPanel, 0.05f), 6f));
        tc.AddThemeColorOverride("font_selected_color", UiTheme.TextDefault);
        tc.AddThemeColorOverride("font_unselected_color", UiTheme.TextDim);
        int i = 0;
        foreach (var child in w.Children)
        {
            var page = new Control { Name = child.Props.Str("text", $"Tab {i + 1}") };
            // child keeps its JSON rect (position is relative to the page) — matches the editor model
            page.AddChild(BuildWidget(child, ctx));
            tc.AddChild(page);
            i++;
        }
        return tc;
    }

    static Control BuildModal(UiWidget w, UiContext ctx, Dictionary<string, string> row)
    {
        var m = new UiModal();
        m.Configure(w, ctx, row);
        return m;
    }
}

/// <summary>Procedural placeholder icons (no external assets needed).</summary>
public static class Icons
{
    static ImageTexture _grabber;
    public static ImageTexture Grabber()
    {
        if (_grabber != null) return _grabber;
        var img = Image.CreateEmpty(24, 24, false, Image.Format.Rgba8);
        var c = UiTheme.Accent;
        for (int y = 0; y < 24; y++)
            for (int x = 0; x < 24; x++)
            {
                float dx = x - 11.5f, dy = y - 11.5f;
                img.SetPixel(x, y, dx * dx + dy * dy <= 100 ? c : new Color(0, 0, 0, 0));
            }
        _grabber = ImageTexture.CreateFromImage(img);
        return _grabber;
    }
}
