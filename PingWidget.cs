using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DuoVoz;

/// <summary>
/// Widget flutuante sempre-no-topo do CherrySpy — "Cereja Espia". Um cartao rosa
/// arredondado com brilho, a CEREJA ESPIA (mascote) num pod circular a esquerda,
/// nome + subtexto no centro, e dois botoes "jelly" PING/CHAT a direita com selo
/// de nao-lidas em coracao. Animacoes 100% GDI+ por um unico Timer:
///  - idle: bob flutuante + balanco do cabinho + piscadinha/sparkle ocasionais;
///  - ping: a cereja quica (squash&stretch), aneis se espalham, coracoes sobem e
///    mini-cerejas caem pelo cartao.
/// Arrastavel (snap nas bordas), nao rouba foco (WS_EX_NOACTIVATE), cantos redondos.
/// </summary>
public sealed class PingWidget : Form
{
    private const int W = 212, H = 58, Corner = 20, SnapPx = 18;

    // Layout (coords do cartao 212x58)
    private static readonly PointF Pod = new(30, 29);
    private const float PodR = 22f;
    private static readonly RectangleF AvatarBox = new(10, 9, 40, 40);
    private static readonly PointF Seal = new(46, 44);
    private static readonly RectangleF PingRect = new(150, 14, 26, 30);
    private static readonly RectangleF ChatRect = new(180, 14, 26, 30);

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 50 }; // ~20fps
    private long _frame;
    private int _pingFrame = -1;            // -1 = idle; 0..PingLen durante o ping
    private const int PingLen = 55;

    private Color _dot = Color.FromArgb(0xEF, 0x44, 0x44);
    private string _name = "Sem par";
    private bool _updateBadge;
    private int _unread;
    private DateTime _lastPing = DateTime.MinValue;
    private long _unreadPopAt = -999;       // frame em que o badge saltou

    // Estado de status pra animacao (verde respira, vermelho pisca)
    private long _connectedAt = -999;

    // Interacao
    private Point _dragOff;
    private bool _dragging;
    private int _hoverBtn;   // 0 none, 1 ping, 2 chat
    private int _pressBtn;

    // Pool de mini-cerejas do ping (deterministico)
    private struct Mini { public float X, Y, Vx, Vy, Rot, Scale; }
    private Mini[] _minis = Array.Empty<Mini>();

    public event Action? PingClicked;
    public event Action? ChatClicked;
    public event Action? ShowMainClicked;
    public event Action? HideWidgetClicked;
    public event Action<int, int>? Moved;
    public event Action<string>? FileDropped;

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
        Size = new Size(W, H);
        BackColor = Color.FromArgb(0xFD, 0xEE, 0xF3);
        DoubleBuffered = true;
        StartPosition = FormStartPosition.Manual;
        AllowDrop = true;

        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        Location = startX >= 0 && startY >= 0
            ? ClampToScreen(new Point(startX, startY))
            : new Point(wa.Right - Width - 24, wa.Top + 24);

        var menu = new ContextMenuStrip { Font = CherryTheme.Body };
        menu.Items.Add("Mostrar janela principal", null, (_, _) => ShowMainClicked?.Invoke());
        menu.Items.Add("Ocultar widget", null, (_, _) => HideWidgetClicked?.Invoke());
        ContextMenuStrip = menu;

        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
        MouseDoubleClick += (_, e) =>
        {
            if (HitButton(e.Location) == 0) ShowMainClicked?.Invoke();
        };
        MouseLeave += (_, _) => { if (_hoverBtn != 0) { _hoverBtn = 0; Invalidate(); } };

        DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
        };
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] fs && fs.Length > 0) FileDropped?.Invoke(fs[0]);
        };

        _timer.Tick += (_, _) => { _frame++; TickPing(); Invalidate(); };

        Region = GfxExt.RoundedRegion(Size, Corner);
        Resize += (_, _) => Region = GfxExt.RoundedRegion(Size, Corner);
        VisibleChanged += (_, _) => { if (Visible) _timer.Start(); else _timer.Stop(); };
    }

    public void ShowNoActivate()
    {
        if (!Visible) Show();
        _timer.Start();
    }

    public void SetStatus(Color dot, string name)
    {
        var nice = NiceStatus(dot);
        bool wasRed = _dot.R > _dot.G;
        bool nowGood = nice.G >= nice.R;
        if (nice == _dot && name == _name) return;
        if (wasRed && nowGood) _connectedAt = _frame; // acabou de conectar -> comemora
        _dot = nice;
        _name = name;
        Invalidate();
    }

    public void SetUnread(int count)
    {
        if (count > _unread) _unreadPopAt = _frame; // saltinho ao chegar msg
        _unread = count;
        Invalidate();
    }

    public void SetUpdateBadge(bool on)
    {
        _updateBadge = on;
        Invalidate();
    }

    /// <summary>Ping recebido: dispara a coreografia da cereja espia.</summary>
    public void Flash()
    {
        _lastPing = DateTime.Now;
        _pingFrame = 0;
        // semeia 3 mini-cerejas caindo (deterministico, posicoes/fases fixas)
        _minis = new Mini[3];
        float[] sx = { 40, 82, 122 };   // faixa central/esquerda, longe dos botoes
        float[] sr = { -0.6f, 0.4f, -0.3f };
        for (int i = 0; i < 3; i++)
            _minis[i] = new Mini { X = sx[i], Y = 6, Vx = sr[i] * 1.4f, Vy = 0.6f + i * 0.15f, Rot = sr[i], Scale = 0.42f + i * 0.03f };
        _timer.Start();
        Invalidate();
    }

    private void TickPing()
    {
        if (_pingFrame < 0) return;
        _pingFrame++;
        // fisica das mini-cerejas
        for (int i = 0; i < _minis.Length; i++)
        {
            ref Mini m = ref _minis[i];
            m.Vy += 0.16f;
            m.X += m.Vx;
            m.Y += m.Vy;
            m.Rot += m.Vx * 0.03f;
            if (m.Y > H - 10) { m.Y = H - 10; m.Vy *= -0.42f; } // quica no rodape
        }
        if (_pingFrame > PingLen) _pingFrame = -1;
    }

    // ─── PINTURA ──────────────────────────────────────────────────────
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        PaintCard(g);
        PaintPod(g);
        if (_pingFrame >= 0) PaintPingRings(g); // aneis ATRAS da cereja
        PaintAvatar(g);
        PaintText(g);
        PaintButtons(g);
        if (_pingFrame >= 0) PaintPingParticles(g); // coracoes + mini-cerejas + sparkles
        PaintStatusSeal(g);                          // sempre por cima (nunca some no ping)
        if (_updateBadge) PaintUpdateBadge(g);
    }

    private void PaintCard(Graphics g)
    {
        var rect = new RectangleF(0, 0, W, H);
        using (var path = GfxExt.RoundedPath(rect, Corner))
        {
            using (var bg = new LinearGradientBrush(rect, Color.FromArgb(0xFF, 0xF4, 0xF7), Color.FromArgb(0xFD, 0xEE, 0xF3), 90f))
                g.FillPath(bg, path);

            // holofote atras do avatar (esquenta se ha nao-lidas)
            using (var glowPath = new GraphicsPath())
            {
                var gr = new RectangleF(Pod.X - 46, Pod.Y - 40, 92, 80);
                glowPath.AddEllipse(gr);
                Color glowC = _unread > 0 ? Color.FromArgb(0xF7, 0x9F, 0xB6) : Color.FromArgb(0xFD, 0xC3, 0xD1);
                using var glow = new PathGradientBrush(glowPath)
                {
                    CenterColor = Color.FromArgb(70, glowC),
                    SurroundColors = new[] { Color.FromArgb(0, glowC) },
                };
                glow.CenterPoint = Pod;
                g.FillPath(glow, glowPath);
            }
        }
        // borda + highlight de gel
        using (var borderPath = GfxExt.RoundedPath(new RectangleF(0.75f, 0.75f, W - 1.5f, H - 1.5f), Corner - 1))
        using (var bpen = new Pen(Color.FromArgb(150, 0xF7, 0x9F, 0xB6), 1.4f))
            g.DrawPath(bpen, borderPath);
        using (var hiPath = GfxExt.RoundedPath(new RectangleF(2f, 2f, W - 4, H - 4), Corner - 2))
        using (var hpen = new Pen(Color.FromArgb(120, 255, 255, 255), 1f))
        {
            g.SetClip(hiPath);
            g.DrawPath(hpen, hiPath);
            g.ResetClip();
        }
    }

    private void PaintPod(Graphics g)
    {
        var podRect = new RectangleF(Pod.X - PodR, Pod.Y - PodR, PodR * 2, PodR * 2);
        using (var pp = new GraphicsPath())
        {
            pp.AddEllipse(podRect);
            using var pg = new PathGradientBrush(pp)
            {
                CenterColor = Color.White,
                SurroundColors = new[] { Color.FromArgb(0xFD, 0xEE, 0xF3) },
            };
            pg.CenterPoint = new PointF(Pod.X - 7, Pod.Y - 8);
            g.FillPath(pg, pp);
        }
        using var ring = new Pen(Color.FromArgb(0xF7, 0x9F, 0xB6), 2f);
        g.DrawEllipse(ring, podRect);
    }

    private void PaintAvatar(Graphics g)
    {
        float bob, sx = 1f, sy = 1f, sway;
        int wink = 0;

        if (_pingFrame >= 0)
        {
            (bob, sx, sy) = PingAvatarMotion(_pingFrame);
            sway = 1.1f * (float)Math.Sin(_pingFrame * 0.6);
            if (_pingFrame is >= 4 and <= 10) wink = 1; // pisca no salto
        }
        else
        {
            double ph = _frame * (2 * Math.PI / 44.0);
            bob = -3.3f * (float)Math.Sin(ph);
            // squash sutil no fundo do bob
            float land = (float)Math.Max(0, -Math.Sin(ph));
            sy = 1f - 0.05f * land; sx = 1f + 0.05f * land;
            sway = 0.55f * (float)Math.Sin(ph + 0.9);
            long blink = _frame % 130;
            if (blink < 4) wink = 1; // piscadinha rara
            // comemoracao ao conectar
            long since = _frame - _connectedAt;
            if (since >= 0 && since < 16)
            {
                bob -= 4f * (float)Math.Sin(since / 16.0 * Math.PI);
                if (since < 5) wink = 1;
            }
        }

        var box = AvatarBox;
        box.Offset(0, bob);
        float pivotX = box.X + box.Width / 2f;
        float pivotY = box.Y + box.Height * 0.92f; // pivo na base (squash pesa embaixo)

        var st = g.Save();
        g.TranslateTransform(pivotX, pivotY);
        g.ScaleTransform(sx, sy);
        g.TranslateTransform(-pivotX, -pivotY);
        SpyCherry.Draw(g, box, sway, wink);
        g.Restore(st);

        // sparkle idle ocasional perto da folha
        if (_pingFrame < 0)
        {
            long sc = _frame % 150;
            if (sc < 10) DrawSparkle(g, new PointF(box.X + 33, box.Y + 6), 1f - sc / 10f, 5f);
        }
    }

    private static (float bob, float sx, float sy) PingAvatarMotion(int f)
    {
        float t = f / (float)PingLen;
        // quica com amortecimento: 2 saltos que decaem
        float env = (float)Math.Exp(-3.1 * t);
        float hop = -17f * env * Math.Abs((float)Math.Sin(t * Math.PI * 2.4));
        // antecipacao inicial (afunda um tico)
        if (f < 6) hop += (6 - f) * 0.6f;
        // squash: estica no ar (hop bem negativo), achata perto do chao
        float airborne = Math.Min(1f, -hop / 14f);
        float ground = f < 6 ? (6 - f) / 6f : Math.Max(0f, 1f - Math.Abs(hop) / 3f) * env;
        float sy = 1f + 0.22f * airborne - 0.20f * ground;
        float sx = 1f - 0.16f * airborne + 0.18f * ground;
        return (hop, sx, sy);
    }

    private void PaintStatusSeal(Graphics g)
    {
        // respiro/pisca conforme estado
        bool green = _dot.G >= _dot.R && _dot.G > 120;
        bool red = _dot.R > _dot.G && _dot.B < 120 && _dot.G < 120;
        double ph = _frame * (2 * Math.PI / 60.0);
        float haloA = green ? (float)(20 + 8 * Math.Sin(ph))
                    : red ? (float)(10 + 10 * Math.Max(0, Math.Sin(_frame * 0.20)))
                    : 22f;

        using (var halo = new SolidBrush(Color.FromArgb((int)haloA, _dot)))
            g.FillEllipse(halo, Seal.X - 9, Seal.Y - 9, 18, 18);
        // aro branco solido de 2.5px -> separa o dot do corpo vermelho da cereja
        using (var white = new SolidBrush(Color.White))
            g.FillEllipse(white, Seal.X - 7.5f, Seal.Y - 7.5f, 15, 15);
        var core = new RectangleF(Seal.X - 5, Seal.Y - 5, 10, 10);
        using (var cp = new GraphicsPath())
        {
            cp.AddEllipse(core);
            using var pg = new PathGradientBrush(cp)
            {
                CenterColor = Blend(Color.White, _dot, 0.35),
                SurroundColors = new[] { _dot },
            };
            pg.CenterPoint = new PointF(core.X + 3, core.Y + 3);
            g.FillPath(pg, cp);
        }
        using (var glint = new SolidBrush(Color.FromArgb(200, 255, 255, 255)))
            g.FillEllipse(glint, Seal.X - 3.4f, Seal.Y - 3.6f, 2.6f, 2.6f);
    }

    private void PaintText(Graphics g)
    {
        int rightLimit = 146;
        var nameRect = new Rectangle(58, 8, rightLimit - 58, 20);
        TextRenderer.DrawText(g, _name, CherryTheme.Head, nameRect, CherryTheme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        string sub = _updateBadge ? "nova versao!"
            : _lastPing != DateTime.MinValue ? $"ping {_lastPing:HH:mm}" : "na escuta";
        var subRect = new Rectangle(58, 30, rightLimit - 58, 16);
        // vinho-cereja escuro -> contraste alto sobre o rosa (antes sumia).
        Color subColor = Color.FromArgb(0x8C, 0x1E, 0x2D);
        TextRenderer.DrawText(g, sub, CherryTheme.BodySmall, subRect, subColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private void PaintButtons(Graphics g)
    {
        // PING (primario rosa)
        DrawPill(g, PingRect, _hoverBtn == 1, _pressBtn == 1, true);
        DrawPingIcon(g, PingRect);
        // CHAT (claro)
        DrawPill(g, ChatRect, _hoverBtn == 2, _pressBtn == 2, false);
        DrawChatIcon(g, ChatRect);
        if (_unread > 0) DrawUnreadHeart(g);
    }

    private void DrawPill(Graphics g, RectangleF r, bool hover, bool press, bool primary)
    {
        var rr = r;
        if (hover && !press) rr = new RectangleF(r.X - 1, r.Y - 1, r.Width + 2, r.Height + 2);
        if (press) rr = new RectangleF(r.X + 0.5f, r.Y + 1, r.Width - 1, r.Height - 1);
        float rad = rr.Height / 2f;
        using var path = GfxExt.RoundedPath(rr, rad);
        if (primary)
        {
            Color top = press ? Color.FromArgb(0xE9, 0x7E, 0x9C) : Color.FromArgb(0xF7, 0x9F, 0xB6);
            Color bot = press ? Color.FromArgb(0xF7, 0x9F, 0xB6) : Color.FromArgb(0xE9, 0x7E, 0x9C);
            using (var br = new LinearGradientBrush(rr, top, bot, 90f)) g.FillPath(br, path);
            // gel highlight
            var hi = new RectangleF(rr.X + 3, rr.Y + 2, rr.Width - 6, rr.Height * 0.42f);
            using (var hp = GfxExt.RoundedPath(hi, hi.Height / 2f))
            using (var hb = new SolidBrush(Color.FromArgb(hover ? 130 : 90, 255, 255, 255)))
                g.FillPath(hb, hp);
        }
        else
        {
            using (var br = new SolidBrush(hover ? Color.FromArgb(0xFF, 0xF4, 0xF7) : Color.White)) g.FillPath(br, path);
            using var pen = new Pen(Color.FromArgb(0xF7, 0x9F, 0xB6), hover ? 1.6f : 1.2f);
            g.DrawPath(pen, path);
        }
    }

    private void DrawPingIcon(Graphics g, RectangleF r)
    {
        var c = new PointF(r.X + r.Width / 2f, r.Y + r.Height / 2f);
        using var pen = new Pen(Color.White, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        for (int i = 1; i <= 3; i++)
        {
            float rad = i * 3.2f;
            g.DrawArc(pen, c.X - rad, c.Y - rad, rad * 2, rad * 2, -55, 110);
        }
        using var dot = new SolidBrush(Color.White);
        g.FillEllipse(dot, c.X - 1.7f, c.Y - 1.7f, 3.4f, 3.4f);
    }

    private void DrawChatIcon(Graphics g, RectangleF r)
    {
        var box = new RectangleF(r.X + r.Width / 2f - 7, r.Y + r.Height / 2f - 6, 14, 10);
        using var body = GfxExt.RoundedPath(box, 3.5f);
        using (var br = new SolidBrush(Color.FromArgb(0xE9, 0x7E, 0x9C))) g.FillPath(br, body);
        // rabinho
        using (var tail = new GraphicsPath())
        {
            tail.AddPolygon(new[] {
                new PointF(box.X + 3, box.Bottom - 1),
                new PointF(box.X + 3, box.Bottom + 4),
                new PointF(box.X + 8, box.Bottom - 1),
            });
            using var tb = new SolidBrush(Color.FromArgb(0xE9, 0x7E, 0x9C));
            g.FillPath(tb, tail);
        }
        using var dots = new SolidBrush(Color.White);
        for (int i = 0; i < 3; i++)
            g.FillEllipse(dots, box.X + 3 + i * 3.4f, box.Y + box.Height / 2f - 1, 1.8f, 1.8f);
    }

    private void DrawUnreadHeart(Graphics g)
    {
        long since = _frame - _unreadPopAt;
        float pop = since >= 0 && since < 10 ? 1f + 0.22f * (float)Math.Sin(since / 10.0 * Math.PI) : 1f;
        float size = 18 * pop;
        var center = new PointF(ChatRect.Right - 1, ChatRect.Y + 2);
        var hr = new RectangleF(center.X - size / 2f, center.Y - size / 2f, size, size);
        using (var hp = HeartPath(hr))
        {
            using (var ring = new Pen(Color.White, 2.2f)) g.DrawPath(ring, hp); // aro branco separa do botao
            using (var br = new SolidBrush(Color.FromArgb(0xC2, 0x1F, 0x3A))) g.FillPath(br, hp); // cereja saturado
        }
        string txt = _unread > 9 ? "9+" : _unread.ToString();
        TextRenderer.DrawText(g, txt, CherryTheme.MonoSmall,
            new Rectangle((int)hr.X, (int)hr.Y, (int)hr.Width, (int)hr.Height - 3), Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private void PaintUpdateBadge(Graphics g)
    {
        if (_unread > 0) return; // nao empilha com o coracao
        var c = new PointF(W - 12, 11);
        using var br = new SolidBrush(Color.FromArgb(0xE7, 0xB2, 0x4A));
        DrawStar(g, c, 5.5f, br);
    }

    // Aneis do "radar" — atras da cereja, saturados, com alpha decrescente e fade nas bordas.
    private void PaintPingRings(Graphics g)
    {
        int f = _pingFrame;
        using var clip = GfxExt.RoundedPath(new RectangleF(0, 0, W, H), Corner);
        g.SetClip(clip);
        for (int k = 0; k < 3; k++)
        {
            float rf = f - 5 - k * 5;
            if (rf <= 0 || rf > 22) continue;
            float t = rf / 22f;
            float ease = 1 - (1 - t) * (1 - t);       // ease-out
            float rad = 7 + ease * 38;
            int a = (int)(130 * (1 - t));
            using var pen = new Pen(Color.FromArgb(Math.Max(0, a), 0xE8, 0x5A, 0x7A), 2f);
            g.DrawEllipse(pen, Pod.X - rad, Pod.Y - rad, rad * 2, rad * 2);
        }
        g.ResetClip();
    }

    // Particulas na frente: mini-cerejas caindo (faixa central, longe dos botoes),
    // coracoes subindo (rosa claro com contorno) e sparkles no fim.
    private void PaintPingParticles(Graphics g)
    {
        int f = _pingFrame;
        int mA = f > PingLen - 8 ? (int)(255 * (PingLen - f) / 8f) : 255;
        mA = Math.Clamp(mA, 0, 255);
        if (f >= 10)
        {
            foreach (var m in _minis)
            {
                float sz = 34 * m.Scale;
                var box = new RectangleF(m.X - sz / 2f, m.Y - sz / 2f, sz, sz);
                SpyCherry.Draw(g, box, m.Rot, 0, mA);
            }
        }

        DrawRisingHeart(g, f, 8, 0.95f, 15, new PointF(Pod.X + 8, Pod.Y - 6));
        DrawRisingHeart(g, f, 14, 0.78f, 11, new PointF(Pod.X - 4, Pod.Y - 2));

        if (f is >= 20 and <= 40)
        {
            float t = (f - 20) / 20f;
            DrawSparkle(g, new PointF(Pod.X + 5, Pod.Y - 3), 1f - Math.Abs(t - 0.3f) * 2f, 6f);
            DrawSparkle(g, new PointF(Pod.X - 7, Pod.Y + 3), 1f - Math.Abs(t - 0.6f) * 2f, 5f);
        }
    }

    private void DrawRisingHeart(Graphics g, int f, int start, float speed, float size, PointF from)
    {
        int hf = f - start;
        if (hf < 0 || hf > 34) return;
        float rise = hf * speed;
        float drift = hf * 0.35f;
        float pop = hf < 6 ? hf / 6f : 1f;
        int alpha = hf > 24 ? (int)(255 * (34 - hf) / 10f) : 255;
        alpha = Math.Clamp(alpha, 0, 255);
        float s = size * pop;
        if (s < 1.5f) return; // evita brush de tamanho zero no spawn
        float wob = 2.2f * (float)Math.Sin(hf * 0.5); // deriva senoidal
        var hr = new RectangleF(from.X + drift + wob - s / 2f, from.Y - rise - s / 2f, s, s);
        using var hp = HeartPath(hr);
        // rosa claro + contorno -> le como coracao sobre o fundo rosa (nao vira mancha)
        using (var br = new SolidBrush(Color.FromArgb(alpha, 0xFD, 0xC3, 0xD1)))
            g.FillPath(br, hp);
        using (var pen = new Pen(Color.FromArgb(alpha, 0xE9, 0x7E, 0x9C), 1.3f))
            g.DrawPath(pen, hp);
    }

    private static void DrawSparkle(Graphics g, PointF c, float k, float size)
    {
        if (k <= 0) return;
        k = Math.Clamp(k, 0f, 1f);
        int a = (int)(230 * k);
        float r = size * (0.5f + 0.5f * k);
        using var pen = new Pen(Color.FromArgb(a, 255, 255, 255), 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, c.X - r, c.Y, c.X + r, c.Y);
        g.DrawLine(pen, c.X, c.Y - r, c.X, c.Y + r);
        using var br = new SolidBrush(Color.FromArgb(a, 255, 255, 255));
        g.FillEllipse(br, c.X - 1.4f, c.Y - 1.4f, 2.8f, 2.8f);
    }

    private static void DrawStar(Graphics g, PointF c, float r, Brush br)
    {
        var pts = new PointF[10];
        for (int i = 0; i < 10; i++)
        {
            double ang = -Math.PI / 2 + i * Math.PI / 5;
            float rr = (i % 2 == 0) ? r : r * 0.45f;
            pts[i] = new PointF(c.X + (float)Math.Cos(ang) * rr, c.Y + (float)Math.Sin(ang) * rr);
        }
        g.FillPolygon(br, pts);
    }

    private static GraphicsPath HeartPath(RectangleF r)
    {
        var gp = new GraphicsPath();
        float x = r.X, y = r.Y, w = r.Width, h = r.Height;
        var tip = new PointF(x + w / 2f, y + h);
        gp.AddBezier(tip, new PointF(x - w * 0.05f, y + h * 0.55f), new PointF(x + w * 0.02f, y + h * 0.05f), new PointF(x + w * 0.5f, y + h * 0.28f));
        gp.AddBezier(new PointF(x + w * 0.5f, y + h * 0.28f), new PointF(x + w * 0.98f, y + h * 0.05f), new PointF(x + w * 1.05f, y + h * 0.55f), tip);
        gp.CloseFigure();
        return gp;
    }

    private static Color NiceStatus(Color c)
    {
        if (c.G > c.R && c.G > 120) return Color.FromArgb(0x22, 0xC5, 0x5E); // verde
        if (c.R > 180 && c.G > 90 && c.B < 130) return Color.FromArgb(0xE7, 0xB2, 0x4A); // ambar
        return Color.FromArgb(0xF0, 0x39, 0x2B); // vermelho-alaranjado (destaca do corpo crimson)
    }

    private static Color Blend(Color a, Color b, double k) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * k), (int)(a.G + (b.G - a.G) * k), (int)(a.B + (b.B - a.B) * k));

    // ─── INTERACAO ────────────────────────────────────────────────────
    private int HitButton(Point p)
    {
        if (PingRect.Contains(p)) return 1;
        if (ChatRect.Contains(p)) return 2;
        return 0;
    }

    private void OnMouseDown(object? s, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        int b = HitButton(e.Location);
        if (b != 0) { _pressBtn = b; Invalidate(); return; }
        _dragging = true; _dragOff = e.Location;
    }

    private void OnMouseMove(object? s, MouseEventArgs e)
    {
        if (_dragging)
        {
            Location = new Point(Location.X + e.X - _dragOff.X, Location.Y + e.Y - _dragOff.Y);
            return;
        }
        int h = HitButton(e.Location);
        if (h != _hoverBtn) { _hoverBtn = h; Cursor = h != 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
    }

    private void OnMouseUp(object? s, MouseEventArgs e)
    {
        if (_pressBtn != 0)
        {
            int b = _pressBtn; _pressBtn = 0;
            if (HitButton(e.Location) == b)
            {
                if (b == 1) PingClicked?.Invoke(); else ChatClicked?.Invoke();
            }
            Invalidate();
            return;
        }
        if (!_dragging) return;
        _dragging = false;
        var wa = Screen.FromPoint(Location).WorkingArea;
        int nx = Left, ny = Top;
        if (Math.Abs(Left - wa.Left) < SnapPx) nx = wa.Left + 6;
        if (Math.Abs(wa.Right - Right) < SnapPx) nx = wa.Right - Width - 6;
        if (Math.Abs(Top - wa.Top) < SnapPx) ny = wa.Top + 6;
        if (Math.Abs(wa.Bottom - Bottom) < SnapPx) ny = wa.Bottom - Height - 6;
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
}
