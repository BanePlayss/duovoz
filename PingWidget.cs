using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DuoVoz;

/// <summary>
/// Widget flutuante sempre-no-topo (~190x52): bolinha de status, nome da amiga,
/// botoes PING e CHAT, badge de nao-lidas, pulso de cor + hora do ultimo ping.
/// Arrastavel (snap nas bordas; posicao persistida via evento Moved).
/// Nao rouba foco (ShowWithoutActivation + WS_EX_NOACTIVATE).
/// </summary>
public sealed class PingWidget : Form
{
    private const int SnapPx = 18;
    private readonly Button _btnPing;
    private readonly Button _btnChat;
    private readonly System.Windows.Forms.Timer _flashTimer = new() { Interval = 40 };
    private readonly Color _baseBack = Color.FromArgb(30, 30, 36);
    private Color _dot = Color.Firebrick;
    private string _name = "Sem par";
    private bool _updateBadge;
    private int _flashStep = -1;
    private DateTime _lastPing = DateTime.MinValue;
    private Point _dragOff;
    private bool _dragging;

    public event Action? PingClicked;
    public event Action? ChatClicked;
    public event Action? ShowMainClicked;
    public event Action? HideWidgetClicked;
    public event Action<int, int>? Moved;      // posicao final (persistir)
    public event Action<string>? FileDropped;  // arquivo solto no widget

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            return cp;
        }
    }

    public PingWidget(int startX, int startY)
    {
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        Size = new Size(190, 52);
        BackColor = _baseBack;
        DoubleBuffered = true;
        StartPosition = FormStartPosition.Manual;
        AllowDrop = true;

        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        Location = startX >= 0 && startY >= 0
            ? ClampToScreen(new Point(startX, startY))
            : new Point(wa.Right - Width - 24, wa.Top + 24);

        _btnPing = MakeBtn("PING", new Point(102, 6));
        _btnPing.Click += (_, _) => PingClicked?.Invoke();
        _btnChat = MakeBtn("CHAT", new Point(102, 27));
        _btnChat.Click += (_, _) => ChatClicked?.Invoke();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Mostrar janela principal", null, (_, _) => ShowMainClicked?.Invoke());
        menu.Items.Add("Ocultar widget", null, (_, _) => HideWidgetClicked?.Invoke());
        ContextMenuStrip = menu;

        MouseDown += OnDragStart;
        MouseMove += OnDragMove;
        MouseUp += OnDragEnd;
        _flashTimer.Tick += OnFlashTick;

        DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
        };
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] fs && fs.Length > 0) FileDropped?.Invoke(fs[0]);
        };

        Region = RoundRegion(Size, 12);
        Resize += (_, _) => Region = RoundRegion(Size, 12);
    }

    /// <summary>Mostra sem roubar foco (ShowWithoutActivation cobre o Show()).</summary>
    public void ShowNoActivate()
    {
        if (!Visible) Show();
    }

    public void SetStatus(Color dot, string name)
    {
        if (_dot == dot && _name == name) return;
        _dot = dot;
        _name = name;
        Invalidate();
    }

    public void SetUnread(int count)
    {
        _btnChat.Text = count > 0 ? $"CHAT ({Math.Min(count, 99)})" : "CHAT";
        _btnChat.BackColor = count > 0 ? Color.FromArgb(140, 50, 40) : Color.FromArgb(52, 52, 62);
    }

    public void SetUpdateBadge(bool on)
    {
        _updateBadge = on;
        Invalidate();
    }

    /// <summary>Ping recebido: pulso de cor forte ~2s + registra a hora.</summary>
    public void Flash()
    {
        _lastPing = DateTime.Now;
        _flashStep = 0;
        _flashTimer.Start();
    }

    private static readonly Color Cherry = Color.FromArgb(0xD6, 0x28, 0x42); // vermelho cereja

    private void OnFlashTick(object? sender, EventArgs e)
    {
        _flashStep++;
        if (_flashStep > 55) // 55 * 40ms = ~2.2s
        {
            _flashTimer.Stop();
            _flashStep = -1;
            BackColor = _baseBack;
            Invalidate();
            return;
        }
        double k = Math.Abs(Math.Sin(_flashStep * Math.PI / 10.0)) * 0.85; // pulso rosa/cereja
        BackColor = Blend(_baseBack, Cherry, k);
        Invalidate();
    }

    // Easing de "quicada" (bounce) p/ a cerejinha cair e assentar.
    private static double EaseOutBounce(double x)
    {
        const double n1 = 7.5625, d1 = 2.75;
        if (x < 1 / d1) return n1 * x * x;
        if (x < 2 / d1) { x -= 1.5 / d1; return n1 * x * x + 0.75; }
        if (x < 2.5 / d1) { x -= 2.25 / d1; return n1 * x * x + 0.9375; }
        x -= 2.625 / d1; return n1 * x * x + 0.984375;
    }

    // Desenha uma cerejinha (2 frutinhas + cabinho + folha) com escala e alpha.
    private static void DrawCherry(Graphics g, float cx, float baseCy, float scale, int alpha)
    {
        alpha = Math.Clamp(alpha, 0, 255);
        Color cherry = Color.FromArgb(alpha, 0xD6, 0x28, 0x42);
        Color stem = Color.FromArgb(alpha, 0x86, 0xA5, 0x4A);
        Color leaf = Color.FromArgb(alpha, 0x5F, 0xB0, 0x5F);
        Color shine = Color.FromArgb(Math.Min(alpha, 200), 0xFF, 0xEB, 0xF0);

        float r = 5.2f * scale;
        PointF lb = new(cx - r * 0.85f, baseCy);
        PointF rb = new(cx + r * 0.95f, baseCy + r * 0.18f);
        PointF joint = new(cx + r * 0.25f, baseCy - 13f * scale);

        using (var pen = new Pen(stem, Math.Max(1.3f, 1.7f * scale)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawBezier(pen, lb.X, lb.Y - r * 0.6f, cx - r, joint.Y + 4 * scale, cx - scale, joint.Y + 2 * scale, joint.X, joint.Y);
            g.DrawBezier(pen, rb.X, rb.Y - r * 0.6f, cx + r * 1.6f, joint.Y + 5 * scale, cx + r * 0.6f, joint.Y + 2 * scale, joint.X, joint.Y);
        }
        using (var lf = new SolidBrush(leaf))
        {
            var st = g.Save();
            g.TranslateTransform(joint.X, joint.Y);
            g.RotateTransform(-35);
            g.FillEllipse(lf, 0, -3.2f * scale, 9f * scale, 5f * scale);
            g.Restore(st);
        }
        using (var b = new SolidBrush(cherry))
        {
            g.FillEllipse(b, lb.X - r, lb.Y - r, r * 2, r * 2);
            g.FillEllipse(b, rb.X - r, rb.Y - r, r * 2, r * 2);
        }
        using (var s = new SolidBrush(shine))
        {
            g.FillEllipse(s, lb.X - r * 0.55f, lb.Y - r * 0.65f, r * 0.7f, r * 0.7f);
            g.FillEllipse(s, rb.X - r * 0.55f, rb.Y - r * 0.65f, r * 0.7f, r * 0.7f);
        }
    }

    private static Color Blend(Color a, Color b, double k) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * k),
        (int)(a.G + (b.G - a.G) * k),
        (int)(a.B + (b.B - a.B) * k));

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // Durante o ping, a cerejinha animada ocupa a esquerda no lugar da bolinha.
        if (_flashStep < 0)
            using (var br = new SolidBrush(_dot)) g.FillEllipse(br, 10, 12, 10, 10);
        if (_updateBadge)
        {
            using var bg = new SolidBrush(Color.Gold);
            g.FillEllipse(bg, 11, 33, 7, 7);
        }
        using var f1 = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        using var f2 = new Font("Segoe UI", 7f);
        using var white = new SolidBrush(Color.White);
        using var gray = new SolidBrush(Color.Silver);
        g.DrawString(_name, f1, white, new RectangleF(26, 7, 76, 18));
        string sub = _updateBadge ? "atualizacao disponivel"
            : _lastPing != DateTime.MinValue ? $"ping {_lastPing:HH:mm}" : "";
        g.DrawString(sub, f2, gray, new RectangleF(26, 28, 76, 16));

        // Cerejinha do ping: cai com quicada, pulsa e some no fim.
        if (_flashStep >= 0)
        {
            int step = _flashStep;
            double p = Math.Min(1.0, step / 16.0);
            float cy = (float)(8f + (30f - 8f) * EaseOutBounce(p));
            float scale = step > 16 ? 1f + 0.10f * (float)Math.Sin((step - 16) * 0.45) : 1f;
            int alpha = step > 46 ? Math.Max(0, (int)(255 * (55 - step) / 9.0)) : 255;
            DrawCherry(g, 15f, cy, scale, alpha);
        }
    }

    private Button MakeBtn(string text, Point loc)
    {
        var b = new Button
        {
            Text = text, Location = loc, Size = new Size(80, 19),
            FlatStyle = FlatStyle.Flat, ForeColor = Color.White,
            BackColor = Color.FromArgb(52, 52, 62), TabStop = false,
            Font = new Font("Segoe UI", 7f, FontStyle.Bold),
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 92);
        Controls.Add(b);
        return b;
    }

    private void OnDragStart(object? s, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { _dragging = true; _dragOff = e.Location; }
    }

    private void OnDragMove(object? s, MouseEventArgs e)
    {
        if (_dragging)
            Location = new Point(Location.X + e.X - _dragOff.X, Location.Y + e.Y - _dragOff.Y);
    }

    private void OnDragEnd(object? s, MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        var wa = Screen.FromPoint(Location).WorkingArea;
        int nx = Left, ny = Top;
        if (Math.Abs(Left - wa.Left) < SnapPx) nx = wa.Left + 4;
        if (Math.Abs(wa.Right - Right) < SnapPx) nx = wa.Right - Width - 4;
        if (Math.Abs(Top - wa.Top) < SnapPx) ny = wa.Top + 4;
        if (Math.Abs(wa.Bottom - Bottom) < SnapPx) ny = wa.Bottom - Height - 4;
        Location = new Point(nx, ny);
        Moved?.Invoke(Left, Top);
    }

    private Point ClampToScreen(Point p)
    {
        var vs = SystemInformation.VirtualScreen;
        return new Point(
            Math.Clamp(p.X, vs.Left, Math.Max(vs.Left, vs.Right - Width)),
            Math.Clamp(p.Y, vs.Top, Math.Max(vs.Top, vs.Bottom - Height)));
    }

    private static Region RoundRegion(Size s, int r)
    {
        var gp = new GraphicsPath();
        gp.AddArc(0, 0, r * 2, r * 2, 180, 90);
        gp.AddArc(s.Width - r * 2 - 1, 0, r * 2, r * 2, 270, 90);
        gp.AddArc(s.Width - r * 2 - 1, s.Height - r * 2 - 1, r * 2, r * 2, 0, 90);
        gp.AddArc(0, s.Height - r * 2 - 1, r * 2, r * 2, 90, 90);
        gp.CloseFigure();
        return new Region(gp);
    }
}
