using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DuoVoz;

/// <summary>
/// Janela de chat estilo widget (borderless arredondada, tema CherrySpy). Links no
/// historico sao clicaveis (RichTextBox.DetectUrls -> abre no navegador). Enter envia.
/// Historico persistido em %APPDATA%\DuoVoz\chat-history.txt (append; ultimas 200 linhas).
/// Aceita arrastar-e-soltar arquivo. Fechar (X do header / Alt+F4) so esconde; ForceClose fecha.
/// </summary>
public sealed class ChatWindow : Form
{
    private readonly RichTextBox _history;
    private readonly TextBox _input;
    private readonly Panel _transferPanel;
    private readonly Label _lblTransfer;
    private readonly ProgressBar _pbTransfer;
    private readonly Button _btnOpenFolder;
    private readonly Func<string, Task<bool>> _sendChat;
    private readonly Action<string> _sendFile;
    private readonly string _historyPath = Path.Combine(AppEnv.DataDir, "chat-history.txt");
    private string _savedPath = "";
    private bool _forceClose;

    private Point _dragOff;
    private bool _dragging;

    public event Action? CancelTransfer;

    public ChatWindow(Func<string, Task<bool>> sendChat, Action<string> sendFile)
    {
        _sendChat = sendChat;
        _sendFile = sendFile;

        Text = "CherrySpy - Chat";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(400, 466);
        BackColor = CherryTheme.Page;
        Font = CherryTheme.Body;
        AllowDrop = true;
        DoubleBuffered = true;
        KeyPreview = true; // p/ interceptar Ctrl+V (colar imagem) antes do campo de texto
        var ic = AppEnv.LoadAppIcon();
        if (ic != null) Icon = ic;

        // â”€â”€ Header rosa (titulo + fechar), arrastavel â”€â”€
        var header = new Panel { Location = new Point(0, 0), Size = new Size(ClientSize.Width, 42), BackColor = CherryTheme.Pink };
        var title = new Label
        {
            Text = "Chat",
            Location = new Point(16, 0),
            Size = new Size(240, 42),
            Font = CherryTheme.HeadBig,
            ForeColor = CherryTheme.Text,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.Transparent,
        };
        var btnClose = new IconButton { IconName = "close", Size = new Size(36, 36), IconSize = 18, Location = new Point(ClientSize.Width - 44, 3) };
        btnClose.Click += (_, _) => Hide();
        header.Controls.Add(title);
        header.Controls.Add(btnClose);
        header.MouseDown += OnDragStart; header.MouseMove += OnDragMove; header.MouseUp += OnDragEnd;
        title.MouseDown += OnDragStart; title.MouseMove += OnDragMove; title.MouseUp += OnDragEnd;
        Controls.Add(header);

        // â”€â”€ Historico (links clicaveis) â”€â”€
        _history = new RichTextBox
        {
            ReadOnly = true,
            DetectUrls = true,
            BorderStyle = BorderStyle.None,
            Location = new Point(12, 50),
            Size = new Size(376, 300),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Color.White,
            ForeColor = CherryTheme.Text,
            Font = CherryTheme.Body,
        };
        _history.LinkClicked += (_, e) => OpenUrl(e.LinkText);
        Controls.Add(_history);

        // â”€â”€ Painel de transferencia â”€â”€
        _transferPanel = new Panel
        {
            Location = new Point(12, 356), Size = new Size(376, 48),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Visible = false, BackColor = CherryTheme.Soft, BorderStyle = BorderStyle.FixedSingle,
        };
        _lblTransfer = new Label { Location = new Point(8, 4), Size = new Size(280, 16), Font = CherryTheme.BodySmall, ForeColor = CherryTheme.Text, BackColor = Color.Transparent };
        _pbTransfer = new ProgressBar { Location = new Point(8, 24), Size = new Size(288, 16), Minimum = 0, Maximum = 100 };
        var btnCancel = new Button { Location = new Point(302, 22), Size = new Size(66, 20), Text = "Cancelar", Font = CherryTheme.BodySmall, FlatStyle = FlatStyle.Flat };
        btnCancel.Click += (_, _) => CancelTransfer?.Invoke();
        _btnOpenFolder = new Button { Location = new Point(288, 2), Size = new Size(80, 18), Text = "Abrir pasta", Font = CherryTheme.BodySmall, FlatStyle = FlatStyle.Flat, Visible = false };
        _btnOpenFolder.Click += (_, _) => OpenFolder();
        _transferPanel.Controls.AddRange(new Control[] { _lblTransfer, _pbTransfer, btnCancel, _btnOpenFolder });
        Controls.Add(_transferPanel);

        // â”€â”€ Linha de entrada: campo + Enviar + Arquivo â”€â”€
        _input = new TextBox
        {
            Location = new Point(12, 412), Size = new Size(236, 28),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White, ForeColor = CherryTheme.Text,
            Font = CherryTheme.Body,
        };
        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.Handled = true; e.SuppressKeyPress = true; DoSend(); }
        };
        Controls.Add(_input);

        var btnSend = new PillButton { Text = "Enviar", IconName = "send", Location = new Point(254, 410), Size = new Size(84, 32), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, FillColor = CherryTheme.Pink };
        btnSend.Click += (_, _) => DoSend();
        Controls.Add(btnSend);

        var btnFile = new IconButton { IconName = "upload", Location = new Point(344, 408), Size = new Size(44, 36), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        btnFile.Click += (_, _) => PickFile();
        Controls.Add(btnFile);

        DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
        };
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) _sendFile(files[0]);
        };

        // Colar (Ctrl+V): imagem do clipboard ou arquivos copiados viram envio.
        // Texto normal cai pro campo (nao suprimido).
        KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.V && (ClipboardHasImage() || ClipboardHasFiles()))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                PasteFromClipboard();
            }
        };

        FormClosing += (_, e) =>
        {
            if (!_forceClose && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        };
        Load += (_, _) => ApplyRegion();
        Resize += (_, _) => ApplyRegion();

        LoadHistory();
    }

    private void ApplyRegion()
    {
        try { Region = GfxExt.RoundedRegion(Size, 14); } catch { }
    }

    // â”€â”€ Arraste pelo header â”€â”€
    private void OnDragStart(object? s, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { _dragging = true; _dragOff = e.Location; }
    }
    private void OnDragMove(object? s, MouseEventArgs e)
    {
        // e.Location e relativo ao controle do header; soma a origem do header (0,0).
        if (_dragging) Location = new Point(Location.X + e.X - _dragOff.X, Location.Y + e.Y - _dragOff.Y);
    }
    private void OnDragEnd(object? s, MouseEventArgs e) => _dragging = false;

    public void ForceClose()
    {
        _forceClose = true;
        try { Close(); } catch { }
    }

    public void AddIncoming(string from, string text) => Append($"[{DateTime.Now:HH:mm}] {from}: {text}", CherryTheme.Text, true);

    public void AddSystem(string text) => Append($"[{DateTime.Now:HH:mm}] * {text}", CherryTheme.Muted, true);

    public void SetTransfer(string text, int pct, bool visible)
    {
        if (visible) _btnOpenFolder.Visible = false;
        _transferPanel.Visible = visible || _btnOpenFolder.Visible;
        _lblTransfer.Text = text;
        try { _pbTransfer.Value = Math.Clamp(pct, 0, 100); } catch { }
    }

    public void ShowOpenFolder(string savedPath)
    {
        _savedPath = savedPath;
        _btnOpenFolder.Visible = true;
        _transferPanel.Visible = true;
        _lblTransfer.Text = "Concluido: " + Path.GetFileName(savedPath);
        _pbTransfer.Value = 100;
    }

    private void OpenFolder()
    {
        try { Process.Start("explorer.exe", $"/select,\"{_savedPath}\""); }
        catch (Exception ex) { Log.Write("abrir pasta falhou: " + ex.Message); }
    }

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("abrir link falhou: " + ex.Message); }
    }

    private async void DoSend()
    {
        string text = _input.Text.Trim();
        if (text.Length == 0) return;
        _input.Clear();
        bool ok = false;
        try { ok = await _sendChat(text); } catch { }
        Append($"[{DateTime.Now:HH:mm}] Eu: {text}{(ok ? "" : "  (nao entregue - sem conexao)")}", CherryTheme.PinkDeep, true);
    }

    private void PickFile()
    {
        using var dlg = new OpenFileDialog { Title = "Enviar arquivo" };
        if (dlg.ShowDialog(this) == DialogResult.OK) _sendFile(dlg.FileName);
    }

    private static bool ClipboardHasImage()
    {
        try { return Clipboard.ContainsImage(); } catch { return false; }
    }

    private static bool ClipboardHasFiles()
    {
        try { return Clipboard.ContainsFileDropList(); } catch { return false; }
    }

    // Colar do clipboard: imagem -> salva PNG temporario, mostra thumbnail e envia por
    // transferencia de arquivo; arquivos copiados -> envia cada um.
    private void PasteFromClipboard()
    {
        try
        {
            if (Clipboard.ContainsImage())
            {
                using Image? img = Clipboard.GetImage();
                if (img == null) return;
                string tmp = Path.Combine(Path.GetTempPath(), $"imagem_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
                img.Save(tmp, ImageFormat.Png);
                Append($"[{DateTime.Now:HH:mm}] Eu enviei uma imagem:", CherryTheme.PinkDeep, true);
                AppendImageInline(img);
                _sendFile(tmp);
            }
            else if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                foreach (string? f in files)
                    if (!string.IsNullOrEmpty(f) && File.Exists(f)) _sendFile(f);
            }
        }
        catch (Exception ex) { Log.Write("chat: colar falhou: " + ex.Message); }
    }

    // Imagem recebida (arquivo de imagem que chegou): mostra o thumbnail no historico.
    public void AddIncomingImage(string path)
    {
        try
        {
            using var img = Image.FromFile(path);
            AppendImageInline(img);
        }
        catch (Exception ex) { Log.Write("chat: img recebida nao renderizou: " + ex.Message); }
    }

    // Insere um thumbnail (max ~240px de largura) inline no RichTextBox via RTF.
    private void AppendImageInline(Image img)
    {
        try
        {
            const int maxW = 240;
            double sc = img.Width > maxW ? (double)maxW / img.Width : 1.0;
            int w = Math.Max(1, (int)(img.Width * sc));
            int h = Math.Max(1, (int)(img.Height * sc));
            using var thumb = new Bitmap(w, h);
            using (var g = Graphics.FromImage(thumb))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(img, 0, 0, w, h);
            }
            string rtf = ImageToRtf(thumb);
            _history.SelectionStart = _history.TextLength;
            _history.SelectionLength = 0;
            _history.SelectedRtf = rtf;
            _history.AppendText(Environment.NewLine);
            _history.SelectionStart = _history.TextLength;
            _history.ScrollToCaret();
        }
        catch (Exception ex) { Log.Write("chat: thumb inline falhou: " + ex.Message); }
    }

    // Converte uma imagem em RTF (\wmetafile8) p/ inserir no RichTextBox. O RichEdit
    // do WinForms nao aceita \pngblip; a via confiavel e desenhar num metafile EMF,
    // converter p/ WMF (GdipEmfToWmfBits) e serializar em hex.
    private const int MM_ANISOTROPIC = 8;

    [DllImport("gdiplus.dll")]
    private static extern uint GdipEmfToWmfBits(IntPtr hEmf, uint bufferSize, byte[]? buffer, int mappingMode, int flags);
    [DllImport("gdi32.dll")]
    private static extern int DeleteEnhMetaFile(IntPtr hemf);

    private static string ImageToRtf(Image image)
    {
        Metafile mf;
        using (var gRef = Graphics.FromHwnd(IntPtr.Zero))
        {
            IntPtr hdc = gRef.GetHdc();
            try { mf = new Metafile(hdc, EmfType.EmfOnly); }
            finally { gRef.ReleaseHdc(hdc); }
        }
        using (var gMf = Graphics.FromImage(mf))
            gMf.DrawImage(image, 0, 0, image.Width, image.Height);

        IntPtr hEmf = mf.GetHenhmetafile();
        byte[] buffer;
        try
        {
            uint size = GdipEmfToWmfBits(hEmf, 0, null, MM_ANISOTROPIC, 0);
            buffer = new byte[size];
            GdipEmfToWmfBits(hEmf, size, buffer, MM_ANISOTROPIC, 0);
        }
        finally { DeleteEnhMetaFile(hEmf); mf.Dispose(); }

        int hmW = (int)Math.Round(image.Width * 2540.0 / 96.0);  // himetric (0.01mm)
        int hmH = (int)Math.Round(image.Height * 2540.0 / 96.0);
        int goalW = (int)Math.Round(image.Width * 1440.0 / 96.0); // twips
        int goalH = (int)Math.Round(image.Height * 1440.0 / 96.0);
        var sb = new StringBuilder(buffer.Length * 2 + 160);
        sb.Append(@"{\rtf1\ansi{\pict\wmetafile8\picw").Append(hmW).Append(@"\pich").Append(hmH)
          .Append(@"\picwgoal").Append(goalW).Append(@"\pichgoal").Append(goalH).Append(' ');
        foreach (byte b in buffer) sb.Append(b.ToString("x2"));
        sb.Append(@"}}");
        return sb.ToString();
    }

    // Acrescenta uma linha (cor opcional) e rola pro fim. Links sao auto-detectados.
    private void Append(string line, Color color, bool persist)
    {
        try
        {
            _history.SelectionStart = _history.TextLength;
            _history.SelectionLength = 0;
            _history.SelectionColor = color;
            _history.AppendText(line + Environment.NewLine);
            _history.SelectionColor = _history.ForeColor;
            _history.SelectionStart = _history.TextLength;
            _history.ScrollToCaret();
        }
        catch { }
        if (!persist) return;
        try { File.AppendAllText(_historyPath, line + Environment.NewLine); } catch { }
    }

    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(_historyPath)) return;
            string[] lines = File.ReadAllLines(_historyPath);
            int start = Math.Max(0, lines.Length - 200);
            for (int i = start; i < lines.Length; i++)
                Append(lines[i], CherryTheme.Muted, false);
        }
        catch (Exception ex) { Log.Write("chat: historico nao carregou: " + ex.Message); }
    }
}
