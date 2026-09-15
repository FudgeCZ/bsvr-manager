using System.Collections.Generic;
using System.Linq;
using Godot;

namespace BSVRManager.UiKit;

/// <summary>Scrollable list that renders one UiWidget template per data row. Rows may carry {token} bindings.</summary>
public partial class UiList : ScrollContainer
{
    VBoxContainer _box;
    UiWidget _template;
    UiWidget _headerTemplate;
    UiContext _ctx;
    float _itemHeight = 84f, _gap = 8f;
    int _sampleRows = 3;
    readonly List<Dictionary<string, string>> _rows = new();

    public UiList()
    {
        HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        VerticalScrollMode = ScrollContainer.ScrollMode.ShowNever;
        ClipContents = true;
        _box = new VBoxContainer();
        _box.AddThemeConstantOverride("separation", (int)_gap);
        _box.SizeFlagsHorizontal = (Control.SizeFlags)3; // expand+fill: rows span the list width
        AddChild(_box);
    }

    public void Configure(UiWidget widget, UiContext ctx)
    {
        _ctx = ctx;
        _template = widget.ItemTemplate;
        // optional second template for group-header rows (row dict sets ["_header"] = "1")
        _headerTemplate = widget.Children.FirstOrDefault(c => c.Id == "header");
        _itemHeight = widget.Props.Float("itemHeight", 84f);
        _gap = widget.Props.Float("gap", 8f);
        _sampleRows = (int)widget.Props.Float("sample", 3);
        _box.AddThemeConstantOverride("separation", (int)_gap);
    }

    public int RowCount => _rows.Count;

    /// <summary>Rebuild rows from data. In edit mode renders sample rows for styling.</summary>
    public void Fill(IEnumerable<Dictionary<string, string>> rows)
    {
        _rows.Clear();
        if (rows != null) _rows.AddRange(rows);
        Rebuild();
    }

    public void Rebuild()
    {
        foreach (var child in _box.GetChildren()) child.QueueFree();
        _box.SizeFlagsVertical = 0; // shrink-begin: rows stack from the top

        if (_template == null) return;

        if (_ctx != null && _ctx.EditMode)
        {
            for (int i = 0; i < _sampleRows; i++)
            {
                var sample = new Dictionary<string, string>
                {
                    ["index"] = i.ToString(),
                    ["title"] = "Sample item " + (i + 1),
                    ["subtitle"] = "Subtitle",
                    ["value"] = "—",
                    ["id"] = "sample_" + i,
                };
                _box.AddChild(BuildRow(sample));
            }
            return;
        }

        foreach (var row in _rows)
            _box.AddChild(BuildRow(row));
    }

    Control BuildRow(Dictionary<string, string> row)
    {
        if (row.TryGetValue("_header", out var isHeader) && isHeader == "1" && _headerTemplate != null)
        {
            var header = UiLoader.BuildWidget(_headerTemplate, _ctx, row);
            header.CustomMinimumSize = new Vector2(0, 40);
            header.Size = new Vector2(Size.X, 40);
            header.SizeFlagsHorizontal = (Control.SizeFlags)3;
            return header;
        }
        var rowRoot = UiLoader.BuildWidget(_template, _ctx, row);
        rowRoot.CustomMinimumSize = new Vector2(0, _itemHeight);
        rowRoot.Size = new Vector2(Size.X, _itemHeight);
        rowRoot.SizeFlagsHorizontal = (Control.SizeFlags)3; // expand + fill
        MakeWheelTransparent(rowRoot);
        return rowRoot;
    }

    /// <summary>Non-interactive row content must not swallow the mouse wheel —
    /// Stop filters would keep ScrollContainer from ever seeing it.</summary>
    static void MakeWheelTransparent(Control c)
    {
        if (c is Button) return;
        c.MouseFilter = MouseFilterEnum.Pass;
        foreach (var child in c.GetChildren())
            if (child is Control cc) MakeWheelTransparent(cc);
    }
}

/// <summary>Simple colored progress bar with optional percent label.</summary>
public partial class UiProgress : Control
{
    public float Value = 50f;
    Color _fg = UiTheme.Accent, _bg = UiTheme.BgCard;
    float _radius = 6f;
    bool _showPercent = true;
    readonly Panel _back = new();
    readonly ColorRect _fill = new();
    readonly Label _label = new();

    public UiProgress()
    {
        CustomMinimumSize = new Vector2(120, 28);
        _back.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_back);
        _fill.Color = _fg;
        _fill.SetAnchorsPreset(Control.LayoutPreset.LeftWide);
        _fill.OffsetBottom = 0; _fill.OffsetTop = 0;
        AddChild(_fill);
        _label.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _label.HorizontalAlignment = HorizontalAlignment.Center;
        _label.VerticalAlignment = VerticalAlignment.Center;
        _label.AddThemeFontSizeOverride("font_size", 18);
        _label.AddThemeColorOverride("font_color", UiTheme.TextDefault);
        AddChild(_label);
        Resized += Redraw;
    }

    public void Configure(UiWidget widget)
    {
        Value = widget.Props.Float("value", 50f);
        _fg = UiTheme.ParseColor(widget.Props.Str("color"), UiTheme.Accent);
        _bg = UiTheme.ParseColor(widget.Props.Str("bgColor"), UiTheme.BgCard);
        _radius = widget.Props.Float("radius", 6f);
        _showPercent = widget.Props.Bool("showPercent", true);
        _fill.Color = _fg;
        _label.Visible = _showPercent;
        Redraw();
    }

    public void Redraw()
    {
        _back.AddThemeStyleboxOverride("panel", UiTheme.Box(_bg, _radius));
        float pct = Mathf.Clamp(Value, 0, 100) / 100f;
        float w = Size.X * pct;
        _fill.Position = new Vector2(2, 2);
        _fill.Size = new Vector2(Mathf.Max(0, (Size.X - 4) * pct), Size.Y - 4);
        // rounding on the fill panel is approximated by clipping inside the back panel
        _label.Text = $"{(int)Mathf.Round(Value)}%";
    }
}

/// <summary>Centered dialog: dim backdrop, title, body text and configurable buttons.</summary>
public partial class UiModal : Control
{
    public string[] Buttons = { "OK" };
    public string ModalId = "";
    UiContext _ctx;
    readonly Panel _card = new();
    readonly Label _title = new();
    readonly Label _body = new();
    readonly TextureRect _image = new();
    readonly HBoxContainer _btnRow = new();
    UiWidget _widget;

    public UiModal()
    {
        Visible = false;
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.62f) };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(dim);

        _card.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.BgPanel, 16f, UiTheme.Accent, 2f));
        AddChild(_card);

        _title.Position = new Vector2(28, 22);
        _title.AddThemeFontSizeOverride("font_size", 30);
        _title.AddThemeColorOverride("font_color", UiTheme.TextDefault);
        _card.AddChild(_title);

        _body.Position = new Vector2(28, 76);
        _body.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _body.AddThemeFontSizeOverride("font_size", 22);
        _body.AddThemeColorOverride("font_color", UiTheme.TextDim);
        _card.AddChild(_body);

        _image.Visible = false;
        _image.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
        _image.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
        _image.MouseFilter = MouseFilterEnum.Ignore;
        _card.AddChild(_image);

        _btnRow.AddThemeConstantOverride("separation", 12);
        _btnRow.Alignment = BoxContainer.AlignmentMode.End;
        _card.AddChild(_btnRow);
    }

    /// <summary>Fits the card inside the modal's assigned rect (panels differ in size).</summary>
    void Relayout()
    {
        float w = Mathf.Max(Size.X, 320), h = Mathf.Max(Size.Y, 240);
        float cw = Mathf.Min(640, w - 24), ch = Mathf.Min(440, h - 24);
        _card.Position = new Vector2((w - cw) / 2, (h - ch) / 2);
        _card.Size = new Vector2(cw, ch);
        _title.Size = new Vector2(cw - 56, 44);
        if (_image.Visible)
        {
            float ih = ch - 170;
            _image.Position = new Vector2(24, 76);
            _image.Size = new Vector2(ih, ih);
            _body.Position = new Vector2(ih + 44, 76);
            _body.Size = new Vector2(cw - 56 - ih - 20, ch - 170);
        }
        else
        {
            _body.Position = new Vector2(28, 76);
            _body.Size = new Vector2(cw - 56, ch - 170);
        }
        _btnRow.Position = new Vector2(28, ch - 74);
        _btnRow.Size = new Vector2(cw - 56, 52);
    }

    /// <summary>Shows an image (e.g. a login QR code) beside the body text.</summary>
    public void SetImage(Texture2D tex)
    {
        _image.Texture = tex;
        _image.Visible = tex != null;
        Relayout();
    }

    public void Configure(UiWidget widget, UiContext ctx, Dictionary<string, string> tokens = null)
    {
        _widget = widget; _ctx = ctx;
        ModalId = widget.Id;
        _image.Visible = false;
        Relayout();
        _title.Text = UiTokens.Interpolate(widget.Props.Str("title", "Title"), tokens);
        SetBody(UiTokens.Interpolate(widget.Props.Str("text", ""), tokens));
        var btnList = widget.Props.StrList("buttons");
        Buttons = btnList.Count > 0 ? btnList.ToArray() : new[] { "OK" };
        RebuildButtons();
    }

    public void SetBody(string text) => _body.Text = text;

    public void RebuildButtons()
    {
        float cardW = Mathf.Max(_card.Size.X, 320);
        foreach (var c in _btnRow.GetChildren()) c.QueueFree();
        foreach (var label in Buttons)
        {
            var btn = new Button { Text = label };
            btn.CustomMinimumSize = new Vector2(Mathf.Min(170.0f, (cardW - 72f) / Buttons.Length - 12), 52);
            btn.AddThemeFontSizeOverride("font_size", 22);
            btn.AddThemeStyleboxOverride("normal", UiTheme.Box(UiTheme.BgCard, 10f));
            btn.AddThemeStyleboxOverride("hover", UiTheme.Box(UiTheme.Lighten(UiTheme.BgCard), 10f));
            btn.AddThemeStyleboxOverride("pressed", UiTheme.Box(UiTheme.Darken(UiTheme.BgCard), 10f));
            btn.AddThemeStyleboxOverride("focus", UiTheme.Box(UiTheme.BgCard, 10f));
            var captured = label;
            btn.Pressed += () =>
            {
                Visible = false;
                _ctx?.Emit(_widget.Props.Str("onButton", "modal.button"), captured.ToLowerInvariant(), _widget);
            };
            _btnRow.AddChild(btn);
        }
    }

    public void Open() { Relayout(); Visible = true; MoveToFront(); }
    public void Close() => Visible = false;
}
