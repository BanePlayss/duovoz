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

    private void OnFlashTick(object? sender, EventArgs e)
    {
        _flashStep++;
        if (_flashStep > 50) // 50 * 40ms = 2s
        {
            _flashTimer.Stop();
            _flashStep = -1;
            BackColor = _baseBack;
            Invalidate();
            return;
        }
        double k = Math.Abs(Math.Sin(_flashStep * Math.PI / 10.0)); // ~5 pulsos em 2s
        BackColor = Blend(_baseBack, Color.FromArgb(235, 120, 20), k);
        Invalidate();
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
