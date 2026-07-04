using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace DuoVoz;

/// <summary>
/// A mascote do CherrySpy desenhada em GDI+: uma cereja vermelha usando mascara de
/// espiao (bandit/domino) com lentes brilhantes + tirinhas laterais, cabinho marrom e
/// folha verde. Escala p/ qualquer 'box' quadrado. Parametros de animacao:
///   sway  -1..1  balanco (rotacao leve do conjunto),
///   wink   0..1  1 = lente direita piscando (olho fechado),
///   alpha  0..255.
/// Todas as cores derivam da identidade do app (crimson #D62842, mascara preta).
/// </summary>
public static class SpyCherry
{
    public static void Draw(Graphics g, RectangleF box, float sway = 0f, float wink = 0f, int alpha = 255)
    {
        alpha = Math.Clamp(alpha, 0, 255);
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var st = g.Save();

        var mid = new PointF(box.X + box.Width / 2f, box.Y + box.Height / 2f);
        g.TranslateTransform(mid.X, mid.Y);
        g.RotateTransform(sway * 5f);
        g.TranslateTransform(-mid.X, -mid.Y);

        float s = box.Width / 100f;
        PointF P(float x, float y) => new(box.X + x * s, box.Y + y * s);
        RectangleF R(float x, float y, float w, float h) => new(box.X + x * s, box.Y + y * s, w * s, h * s);
        Color A(int rr, int gg, int bb, int a = 255) => Color.FromArgb(a * alpha / 255, rr, gg, bb);

        Color crimson = A(0xD6, 0x28, 0x42);
        Color crimsonLight = A(0xF2, 0x5E, 0x77);
        Color crimsonDark = A(0x8E, 0x14, 0x2E);
        Color stemCol = A(0x6E, 0x4A, 0x2B);
        Color leafHi = A(0x74, 0xC1, 0x63);
        Color leafLo = A(0x36, 0x93, 0x45);
        Color maskBlack = A(0x15, 0x13, 0x18);
        Color maskGrey = A(0x3A, 0x36, 0x40);
        Color glint = A(0xFA, 0xF6, 0xFF);

        // 1) cabinho marrom (atras do corpo) — traco grosso, assinatura do icone
        using (var stemPen = new Pen(stemCol, 4.2f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawBezier(stemPen, P(54, 36), P(60, 20), P(72, 18), P(82, 10));

        // 2) folha
        {
            var lst = g.Save();
            var leafC = P(86, 9);
            g.TranslateTransform(leafC.X, leafC.Y);
            g.RotateTransform(-32);
            var leafRect = new RectangleF(-4 * s, -7 * s, 24 * s, 13 * s);
            using var lp = new GraphicsPath();
            lp.AddArc(leafRect.X, leafRect.Y, leafRect.Width, leafRect.Height, 180, 180);
            lp.AddArc(leafRect.X, leafRect.Y, leafRect.Width, leafRect.Height, 0, 180);
            lp.CloseFigure();
            using (var lb = new LinearGradientBrush(leafRect, leafHi, leafLo, 60f))
                g.FillPath(lb, lp);
            using (var vein = new Pen(A(0x2A, 0x74, 0x38), 1.2f * s))
                g.DrawLine(vein, leafRect.X + 2 * s, leafRect.Y + leafRect.Height / 2, leafRect.Right - 2 * s, leafRect.Y + leafRect.Height / 2);
            g.Restore(lst);
        }

        // 3) corpo (radial gradient p/ volume)
        var bodyRect = R(18, 30, 60, 62);
        using (var bp = new GraphicsPath())
        {
            bp.AddEllipse(bodyRect);
            using (var pgb = new PathGradientBrush(bp)
            {
                CenterColor = crimsonLight,
                SurroundColors = new[] { crimsonDark },
                FocusScales = new PointF(0.25f, 0.25f),
            })
            {
                pgb.CenterPoint = P(40, 50);
                g.FillPath(pgb, bp);
            }
            using var overlay = new SolidBrush(Color.FromArgb(70 * alpha / 255, crimson));
            g.FillEllipse(overlay, bodyRect);
        }

        // specular (gloss suave, radial -> transparente)
        var hlRect = R(27, 37, 22, 16);
        using (var hp = new GraphicsPath())
        {
            hp.AddEllipse(hlRect);
            using var hg = new PathGradientBrush(hp)
            {
                CenterColor = A(0xFF, 0xEA, 0xEF, 200),
                SurroundColors = new[] { A(0xFF, 0xEA, 0xEF, 0) },
            };
            hg.CenterPoint = new PointF(hlRect.X + hlRect.Width * 0.42f, hlRect.Y + hlRect.Height * 0.36f);
            g.FillPath(hg, hp);
        }

        // 4) mascara de espiao: tirinhas laterais + ponte FINA + 2 lentes SEPARADAS
        // (o vao claro entre elas garante leitura de "oculos" ate em tamanho pequeno).
        using (var mb = new SolidBrush(maskBlack))
        {
            g.FillPolygon(mb, new[] { P(28, 50), P(11, 47), P(11, 54), P(28, 60) });
            g.FillPolygon(mb, new[] { P(72, 50), P(89, 47), P(89, 54), P(72, 60) });
        }
        using (var bridge = new Pen(maskBlack, 2.4f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(bridge, P(47, 55), P(53, 55));
        DrawLens(g, R(26, 46, 21, 17), maskBlack, maskGrey, glint, false, s, -7f);
        DrawLens(g, R(53, 46, 21, 17), maskBlack, maskGrey, glint, wink > 0.5f, s, 7f);

        g.Restore(st);
        g.SmoothingMode = old;
    }

    private static void DrawLens(Graphics g, RectangleF r, Color black, Color grey, Color glint, bool winking, float s, float tilt)
    {
        var c = new PointF(r.X + r.Width / 2f, r.Y + r.Height / 2f);
        var tst = g.Save();
        g.TranslateTransform(c.X, c.Y);
        g.RotateTransform(tilt);
        g.TranslateTransform(-c.X, -c.Y);

        if (winking)
        {
            using var wp = new Pen(black, 4.2f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(wp, r.X, r.Y - r.Height * 0.2f, r.Width, r.Height * 1.3f, 200, 140);
            g.Restore(tst);
            return;
        }

        using (var lp = new GraphicsPath())
        {
            lp.AddEllipse(r);
            using (var lg = new LinearGradientBrush(r, grey, black, 90f))
                g.FillPath(lg, lp);
            using var edge = new Pen(black, 1.4f * s);
            g.DrawEllipse(edge, r);
        }
        using (var gb = new SolidBrush(glint))
        {
            // glint principal REDONDO e opaco (le como olhinho brilhante) + um menor
            var gl = g.Save();
            g.TranslateTransform(r.X + r.Width * 0.33f, r.Y + r.Height * 0.34f);
            g.RotateTransform(-30);
            g.FillEllipse(gb, -2.6f * s, -2.4f * s, 5.2f * s, 4.4f * s);
            g.FillEllipse(gb, 1.4f * s, 1.6f * s, 2.4f * s, 1.6f * s);
            g.Restore(gl);
        }

        g.Restore(tst);
    }
}
