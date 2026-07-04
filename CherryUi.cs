using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DuoVoz;

// CherrySpy custom UI kit (light theme).
// Controles pintados a mao (flicker-free) p/ a nova cara do app. Nenhuma logica
// de audio/rede aqui: sao so widgets. As cores/fontes vem do CherryTheme.

internal static class CherryTheme
{
    // Paleta exata do mockup.
    public static readonly Color Page     = Color.FromArgb(0xF5, 0xEB, 0xEF);
    public static readonly Color Panel    = Color.FromArgb(0xFF, 0xFF, 0xFF);
    public static readonly Color Soft     = Color.FromArgb(0xFF, 0xF4, 0xF7);
    public static readonly Color Line     = Color.FromArgb(0xEF, 0xDC, 0xE3);
    public static readonly Color Line2    = Color.FromArgb(0xF2, 0xE7, 0xEC);
    public static readonly Color Pink     = Color.FromArgb(0xFD, 0xC3, 0xD1);
    public static readonly Color PinkMid  = Color.FromArgb(0xF7, 0x9F, 0xB6);
    public static readonly Color PinkDeep = Color.FromArgb(0xE9, 0x7E, 0x9C);
    public static readonly Color PinkGhost= Color.FromArgb(0xFD, 0xEE, 0xF3);
    public static readonly Color Text     = Color.FromArgb(0x14, 0x10, 0x13);
    public static readonly Color Muted    = Color.FromArgb(0x7C, 0x6E, 0x75);
    public static readonly Color Dim      = Color.FromArgb(0xAA, 0x9C, 0xA3);

    // Fontes cacheadas (nunca dispostas: vivem o processo inteiro).
    private static readonly string HeadFamily =
        IsInstalled("Bahnschrift") ? "Bahnschrift" : "Segoe UI Semibold";

    public static readonly Font Head      = new(HeadFamily, 10.5f, FontStyle.Regular);
    public static readonly Font HeadBig   = new(HeadFamily, 13f, FontStyle.Regular);
    public static readonly Font Label     = new(HeadFamily, 8.5f, FontStyle.Regular);
    public static readonly Font Body      = new("Segoe UI", 9f, FontStyle.Regular);
    public static readonly Font BodySmall = new("Segoe UI", 8f, FontStyle.Regular);
    public static readonly Font Mono      = new("Consolas", 9f, FontStyle.Regular);
    public static readonly Font MonoSmall = new("Consolas", 8f, FontStyle.Regular);

    private static bool IsInstalled(string family)
    {
        try
        {
            using var f = new Font(family, 9f);
            return string.Equals(f.Name, family, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}

// Icons (GDI+ lineart, escala p/ um Rectangle).
internal static class CherryIcons
{
    // Desenha o icone 'name' dentro de r, com a cor c. Traco ~1.6, cantos redondos.
    public static void Draw(Graphics g, string name, Rectangle r, Color c)
    {
        var oldSmooth = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float stroke = Math.Max(1.4f, r.Width / 15f);
        using var pen = new Pen(c, stroke)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        using var fill = new SolidBrush(c);

        // Trabalha num quadrado 0..100 e escala p/ r (deixa 18% de padding).
        float pad = r.Width * 0.18f;
        float x0 = r.X + pad, y0 = r.Y + pad;
        float s = (r.Width - pad * 2) / 100f;
        PointF P(float px, float py) => new(x0 + px * s, y0 + py * s);
        RectangleF Rect(float px, float py, float pw, float ph) =>
            new(x0 + px * s, y0 + py * s, pw * s, ph * s);

        switch (name)
        {
            case "mic":
                g.DrawRectRounded(pen, Rect(38, 6, 24, 46), 12 * s);
                g.DrawArcPath(pen, Rect(26, 30, 48, 40), 20, 140);
                g.DrawLine(pen, P(50, 74), P(50, 90));
                g.DrawLine(pen, P(34, 90), P(66, 90));
                break;
            case "micOff":
                g.DrawRectRounded(pen, Rect(38, 6, 24, 46), 12 * s);
                g.DrawArcPath(pen, Rect(26, 30, 48, 40), 20, 140);
                g.DrawLine(pen, P(50, 74), P(50, 90));
                g.DrawLine(pen, P(34, 90), P(66, 90));
                g.DrawLine(pen, P(14, 12), P(86, 92)); // barra do "mudo"
                break;
            case "music":
                g.DrawLine(pen, P(38, 74), P(38, 24));
                g.DrawLine(pen, P(38, 24), P(80, 14));
                g.DrawLine(pen, P(80, 14), P(80, 60));
                g.FillEllipsePt(fill, P(30, 74), 9 * s);
                g.FillEllipsePt(fill, P(72, 60), 9 * s);
                break;
            case "phone":
                // fone de telefone (conectar)
                g.DrawArcPath(pen, Rect(14, 14, 72, 72), 100, 150);
                g.DrawLine(pen, P(30, 40), P(44, 54));
                g.DrawLine(pen, P(56, 66), P(70, 80));
                break;
            case "chat":
                g.DrawRectRounded(pen, Rect(12, 18, 76, 52), 14 * s);
                g.DrawLine(pen, P(30, 44), P(70, 44));
                g.DrawLine(pen, P(30, 56), P(58, 56));
                break;
            case "bell":
                g.DrawArcPath(pen, Rect(28, 12, 44, 44), 180, 180);
                g.DrawLine(pen, P(28, 34), P(28, 66));
                g.DrawLine(pen, P(72, 34), P(72, 66));
                g.DrawLine(pen, P(18, 66), P(82, 66));
                g.DrawArcPath(pen, Rect(40, 70, 20, 16), 0, 180);
                break;
            case "upload":
                g.DrawLine(pen, P(50, 16), P(50, 60));
                g.DrawLine(pen, P(32, 34), P(50, 16));
                g.DrawLine(pen, P(68, 34), P(50, 16));
                g.DrawLine(pen, P(22, 70), P(22, 84));
                g.DrawLine(pen, P(22, 84), P(78, 84));
                g.DrawLine(pen, P(78, 84), P(78, 70));
                break;
            case "refresh":
                g.DrawArcPath(pen, Rect(16, 16, 68, 68), 60, 250);
                // seta
                g.DrawLine(pen, P(82, 20), P(84, 44));
                g.DrawLine(pen, P(84, 44), P(62, 40));
                break;
            case "gear":
                g.DrawEllipse(pen, Rect(34, 34, 32, 32));
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4;
                    var p1 = P(50 + (float)Math.Cos(a) * 34, 50 + (float)Math.Sin(a) * 34);
                    var p2 = P(50 + (float)Math.Cos(a) * 46, 50 + (float)Math.Sin(a) * 46);
                    g.DrawLine(pen, p1, p2);
                }
                break;
            case "headset":
                g.DrawArcPath(pen, Rect(18, 18, 64, 64), 180, 180);
                g.DrawRectRounded(pen, Rect(18, 48, 16, 30), 6 * s);
                g.DrawRectRounded(pen, Rect(66, 48, 16, 30), 6 * s);
                break;
            case "chevronDown":
                g.DrawLine(pen, P(30, 42), P(50, 62));
                g.DrawLine(pen, P(70, 42), P(50, 62));
                break;
            case "minimize":
                g.DrawLine(pen, P(20, 70), P(80, 70));
                break;
            case "close":
                g.DrawLine(pen, P(24, 24), P(76, 76));
                g.DrawLine(pen, P(76, 24), P(24, 76));
                break;
            case "skipNext":
                g.FillPolygon(fill, new[] { P(26, 28), P(26, 72), P(58, 50) });
                g.DrawLine(pen, P(70, 26), P(70, 74));
                break;
            case "skipPrev":
                g.FillPolygon(fill, new[] { P(74, 28), P(74, 72), P(42, 50) });
                g.DrawLine(pen, P(30, 26), P(30, 74));
                break;
            case "playPause":
                // triangulo de play (representa play/pausa)
                g.FillPolygon(fill, new[] { P(34, 26), P(34, 74), P(76, 50) });
                break;
            case "send":
                // aviaozinho de papel
                g.DrawLine(pen, P(14, 52), P(86, 16));
                g.DrawLine(pen, P(86, 16), P(58, 86));
                g.DrawLine(pen, P(58, 86), P(46, 56));
                g.DrawLine(pen, P(46, 56), P(14, 52));
                break;
        }
        g.SmoothingMode = oldSmooth;
    }
}

// Helpers de GraphicsPath usados pelos icones (mantem o Draw legivel).
internal static class GfxExt
{
    public static void DrawRectRounded(this Graphics g, Pen pen, RectangleF r, float radius)
    {
        using var gp = RoundedPath(r, radius);
        g.DrawPath(pen, gp);
    }

    public static void DrawArcPath(this Graphics g, Pen pen, RectangleF r, float start, float sweep)
        => g.DrawArc(pen, r.X, r.Y, r.Width, r.Height, start, sweep);

    public static void FillEllipsePt(this Graphics g, Brush b, PointF center, float d)
        => g.FillEllipse(b, center.X - d / 2, center.Y - d / 2, d, d);

    public static GraphicsPath RoundedPath(RectangleF r, float radius)
    {
        float d = radius * 2;
        var gp = new GraphicsPath();
        if (d <= 0) { gp.AddRectangle(r); gp.CloseFigure(); return gp; }
        gp.AddArc(r.X, r.Y, d, d, 180, 90);
        gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        gp.CloseFigure();
        return gp;
    }

    public static Region RoundedRegion(Size s, int radius)
    {
        using var gp = RoundedPath(new RectangleF(0, 0, s.Width, s.Height), radius);
        return new Region(gp);
    }
}

// ToggleSwitch : Control (pilula pintada).
// Compativel com o uso atual de CheckBox: bool Checked + event CheckedChanged.
internal sealed class ToggleSwitch : Control
{
    private bool _checked;
    private bool _hover;

    public event EventHandler? CheckedChanged;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        Size = new Size(40, 22);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnClick(EventArgs e)
    {
        if (Enabled) Checked = !Checked;
        base.OnClick(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Enabled && (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter))
        {
            Checked = !Checked;
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override bool IsInputKey(Keys keyData)
        => keyData == Keys.Space || keyData == Keys.Enter || base.IsInputKey(keyData);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int h = Height, w = Width;
        var track = new RectangleF(0, (Height - h) / 2f, w, h);
        Color trackColor = _checked
            ? (Enabled ? CherryTheme.Pink : CherryTheme.Line)
            : CherryTheme.Line;
        if (_checked && _hover && Enabled) trackColor = CherryTheme.PinkMid;

        using (var gp = GfxExt.RoundedPath(track, h / 2f))
        using (var br = new SolidBrush(trackColor))
            g.FillPath(br, gp);

        float knobD = h - 6;
        float knobX = _checked ? w - knobD - 3 : 3;
        var knob = new RectangleF(knobX, 3, knobD, knobD);
        using (var kb = new SolidBrush(Enabled ? Color.White : CherryTheme.Soft))
            g.FillEllipse(kb, knob);
        using (var kp = new Pen(_checked ? CherryTheme.PinkMid : CherryTheme.Dim, 1f))
            g.DrawEllipse(kp, knob);
    }
}

// VolumeSlider : Control (trilho slim + fill rosa + knob).
internal sealed class VolumeSlider : Control
{
    private int _value = 50;
    private bool _dragging;

    public event EventHandler? ValueChanged;

    public VolumeSlider()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        Size = new Size(200, 22);
        BackColor = Color.Transparent;
        TabStop = true;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => _value;
        set
        {
            int v = Math.Clamp(value, 0, 100);
            if (v == _value) return;
            _value = v;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private int Knob => Height - 6;

    private void SetFromX(int x)
    {
        float usable = Width - Knob;
        if (usable <= 0) return;
        float rel = (x - Knob / 2f) / usable;
        Value = (int)Math.Round(Math.Clamp(rel, 0f, 1f) * 100f);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (Enabled && e.Button == MouseButtons.Left)
        {
            _dragging = true;
            Focus();
            SetFromX(e.X);
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) SetFromX(e.X);
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e) { _dragging = false; base.OnMouseUp(e); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!Enabled) { base.OnKeyDown(e); return; }
        if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Down) { Value -= 2; e.Handled = true; }
        else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Up) { Value += 2; e.Handled = true; }
        else if (e.KeyCode == Keys.Home) { Value = 0; e.Handled = true; }
        else if (e.KeyCode == Keys.End) { Value = 100; e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override bool IsInputKey(Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down:
            case Keys.Home: case Keys.End:
                return true;
            default:
                return base.IsInputKey(keyData);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int knob = Knob;
        float cy = Height / 2f;
        float trackH = 5f;
        var track = new RectangleF(knob / 2f, cy - trackH / 2f, Width - knob, trackH);

        using (var gp = GfxExt.RoundedPath(track, trackH / 2f))
        using (var br = new SolidBrush(CherryTheme.Line2))
            g.FillPath(br, gp);

        float fillW = (Width - knob) * (_value / 100f);
        if (fillW > 0.5f)
        {
            var fill = new RectangleF(knob / 2f, cy - trackH / 2f, fillW, trackH);
            using var gp = GfxExt.RoundedPath(fill, trackH / 2f);
            using var br = new SolidBrush(Enabled ? CherryTheme.Pink : CherryTheme.Line);
            g.FillPath(br, gp);
        }

        float kx = (Width - knob) * (_value / 100f);
        var kr = new RectangleF(kx, cy - knob / 2f, knob, knob);
        using (var kb = new SolidBrush(Enabled ? Color.White : CherryTheme.Soft))
            g.FillEllipse(kb, kr);
        using (var kp = new Pen(Enabled ? CherryTheme.PinkMid : CherryTheme.Dim, 1.5f))
            g.DrawEllipse(kp, kr);
    }
}

// IconButton : Control (icone pintado + caption opcional).
internal sealed class IconButton : Control
{
    private bool _hover;
    private bool _down;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string IconName { get; set; } = "";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Caption { get; set; } = "";

    // Tamanho fixo do icone em px; 0 = automatico (min(W,H)-22 sem caption, 24 com caption).
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int IconSize { get; set; }

    public IconButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        Size = new Size(66, 56);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Enabled && (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter))
        {
            OnClick(EventArgs.Empty);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override bool IsInputKey(Keys keyData)
        => keyData == Keys.Space || keyData == Keys.Enter || base.IsInputKey(keyData);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (_hover && Enabled)
        {
            using var gp = GfxExt.RoundedPath(new RectangleF(0, 0, Width - 1, Height - 1), 10);
            using var br = new SolidBrush(_down ? CherryTheme.PinkGhost : CherryTheme.Soft);
            g.FillPath(br, gp);
        }

        Color ic = Enabled ? CherryTheme.Text : CherryTheme.Dim;
        int iconSize = IconSize > 0 ? IconSize : (string.IsNullOrEmpty(Caption) ? Math.Min(Width, Height) - 22 : 24);
        int iconY = string.IsNullOrEmpty(Caption) ? (Height - iconSize) / 2 : 6;
        var ir = new Rectangle((Width - iconSize) / 2, iconY, iconSize, iconSize);
        CherryIcons.Draw(g, IconName, ir, ic);

        if (!string.IsNullOrEmpty(Caption))
        {
            var tr = new Rectangle(0, iconY + iconSize + 1, Width, Height - iconY - iconSize - 2);
            TextRenderer.DrawText(g, Caption, CherryTheme.Label, tr,
                Enabled ? CherryTheme.Muted : CherryTheme.Dim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
        }
    }
}

// PillButton : Control (botao rosa full-width com icone, p/ Conectar).
internal sealed class PillButton : Control
{
    private bool _hover;
    private bool _down;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string IconName { get; set; } = "phone";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color FillColor { get; set; } = CherryTheme.Pink;

    public PillButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        Size = new Size(372, 44);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        TabStop = true;
        Font = CherryTheme.Head;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Enabled && (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter))
        {
            OnClick(EventArgs.Empty);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override bool IsInputKey(Keys keyData)
        => keyData == Keys.Space || keyData == Keys.Enter || base.IsInputKey(keyData);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color fill = FillColor;
        if (Enabled && _down) fill = ControlPaint.Dark(FillColor, 0.05f);
        else if (Enabled && _hover) fill = ControlPaint.Light(FillColor, 0.08f);
        if (!Enabled) fill = CherryTheme.Line;

        using (var gp = GfxExt.RoundedPath(new RectangleF(0, 0, Width - 1, Height - 1), Height / 2f - 2))
        using (var br = new SolidBrush(fill))
            g.FillPath(br, gp);

        Color fg = Enabled ? CherryTheme.Text : CherryTheme.Dim;
        var size = TextRenderer.MeasureText(g, Text, Font);
        int iconSize = 20;
        int gap = 8;
        int totalW = iconSize + gap + size.Width;
        int startX = (Width - totalW) / 2;
        int cy = Height / 2;

        var ir = new Rectangle(startX, cy - iconSize / 2, iconSize, iconSize);
        CherryIcons.Draw(g, IconName, ir, fg);

        var tr = new Rectangle(startX + iconSize + gap, 0, size.Width + 4, Height);
        TextRenderer.DrawText(g, Text, Font, tr, fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }
}
