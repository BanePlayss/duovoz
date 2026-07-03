using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DuoVoz;

/// <summary>
/// Janela de chat (nao TopMost). Enter envia. Historico persistido em
/// %APPDATA%\DuoVoz\chat-history.txt (append; carrega as ultimas 200 linhas).
/// Aceita arrastar-e-soltar arquivo p/ enviar. Fechar so esconde (ForceClose fecha).
/// </summary>
public sealed class ChatWindow : Form
{
    private readonly TextBox _history;
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

    public event Action? CancelTransfer;

    public ChatWindow(Func<string, Task<bool>> sendChat, Action<string> sendFile)
    {
        _sendChat = sendChat;
        _sendFile = sendFile;
        Text = "CherrySpy - Chat";
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(420, 470);
        MinimumSize = new Size(380, 340);
        StartPosition = FormStartPosition.CenterScreen;
        var ic = AppEnv.LoadAppIcon();
        if (ic != null) Icon = ic;
        AllowDrop = true;

        _history = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Location = new Point(10, 10), Size = new Size(400, 328),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Color.White,
        };
        Controls.Add(_history);

        _transferPanel = new Panel
        {
            Location = new Point(10, 346), Size = new Size(400, 54),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Visible = false, BorderStyle = BorderStyle.FixedSingle,
        };
        _lblTransfer = new Label { Location = new Point(6, 4), Size = new Size(300, 18), Font = new Font("Segoe UI", 8f) };
        _pbTransfer = new ProgressBar { Location = new Point(6, 26), Size = new Size(308, 18), Minimum = 0, Maximum = 100 };
        var btnCancel = new Button { Location = new Point(320, 24), Size = new Size(74, 22), Text = "Cancelar", Font = new Font("Segoe UI", 7.5f) };
        btnCancel.Click += (_, _) => CancelTransfer?.Invoke();
        _btnOpenFolder = new Button { Location = new Point(310, 2), Size = new Size(84, 20), Text = "Abrir pasta", Font = new Font("Segoe UI", 7.5f), Visible = false };
        _btnOpenFolder.Click += (_, _) => OpenFolder();
        _transferPanel.Controls.AddRange(new Control[] { _lblTransfer, _pbTransfer, btnCancel, _btnOpenFolder });
        Controls.Add(_transferPanel);

        _input = new TextBox
        {
            Location = new Point(10, 410), Size = new Size(246, 24),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };
        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.Handled = true; e.SuppressKeyPress = true; DoSend(); }
        };
        Controls.Add(_input);

        var btnSend = new Button { Location = new Point(262, 408), Size = new Size(70, 27), Text = "Enviar", Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        btnSend.Click += (_, _) => DoSend();
        Controls.Add(btnSend);

        var btnFile = new Button { Location = new Point(338, 408), Size = new Size(72, 27), Text = "Arquivo", Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
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

        FormClosing += (_, e) =>
        {
            if (!_forceClose && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        };

        LoadHistory();
    }

    public void ForceClose()
    {
        _forceClose = true;
        try { Close(); } catch { }
    }

    public void AddIncoming(string from, string text) => AppendLine($"[{DateTime.Now:HH:mm}] {from}: {text}", true);

    public void AddSystem(string text) => AppendLine($"[{DateTime.Now:HH:mm}] * {text}", true);

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

    private async void DoSend()
    {
        string text = _input.Text.Trim();
        if (text.Length == 0) return;
        _input.Clear();
        bool ok = false;
        try { ok = await _sendChat(text); } catch { }
        AppendLine($"[{DateTime.Now:HH:mm}] Eu: {text}{(ok ? "" : "  (nao entregue - sem conexao)")}", true);
    }

    private void PickFile()
    {
        using var dlg = new OpenFileDialog { Title = "Enviar arquivo" };
        if (dlg.ShowDialog(this) == DialogResult.OK) _sendFile(dlg.FileName);
    }

    private void AppendLine(string line, bool persist)
    {
        try { _history.AppendText(line + Environment.NewLine); } catch { }
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
                _history.AppendText(lines[i] + Environment.NewLine);
        }
        catch (Exception ex) { Log.Write("chat: historico nao carregou: " + ex.Message); }
    }
}
