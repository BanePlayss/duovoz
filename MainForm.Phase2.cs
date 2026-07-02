using System;
using System.Drawing;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NAudio.Wave;

namespace DuoVoz;

// Fase 2 do MainForm (partial): canal de controle, chat, ping, widget, arquivos,
// supressao de ruido, process-loopback e botao de atualizacao.
public sealed partial class MainForm
{
    private PeerLink? _peerLink;
    private FileTransferService? _fileTransfer;
    private ChatWindow? _chat;
    private PingWidget? _widget;
    private NoiseSuppressor? _noise;
    private System.Windows.Forms.Timer? _phase2Timer;
    private CheckBox _chkNoise = null!;
    private CheckBox _chkWidget = null!;
    private Button _btnUpdate = null!;
    private Button _btnSendFile = null!;
    private Button _btnChat = null!;
    private Button _btnHelp = null!;
    private int _unread;
    private long _lastPingSent;
    private bool _initializingPhase2; // suprime a escrita de config nos handlers durante o init

    // Chamado no fim do BuildUi (antes do timer da UI).
    private void BuildPhase2Ui()
    {
        ClientSize = new Size(440, 544); // abre espaco pras linhas novas
        var ic = AppEnv.LoadAppIcon();
        if (ic != null) Icon = ic;

        // Criados como false; os valores reais chegam em InitPhase2 sob _initializingPhase2
        // (evita uma escrita de config redundante quando o valor salvo difere do default).
        _chkNoise = new CheckBox { Location = new Point(16, 434), Size = new Size(198, 24), Text = "Supressao de ruido", Checked = false };
        _chkNoise.CheckedChanged += (_, _) =>
        {
            if (_noise != null) _noise.Enabled = _chkNoise.Checked;
            if (_initializingPhase2) return;
            _config.NoiseSuppress = _chkNoise.Checked;
            try { _config.Save(); } catch { }
        };
        Controls.Add(_chkNoise);

        _chkWidget = new CheckBox { Location = new Point(222, 434), Size = new Size(198, 24), Text = "Mostrar widget flutuante", Checked = false };
        _chkWidget.CheckedChanged += (_, _) =>
        {
            if (!_initializingPhase2)
            {
                _config.ShowWidget = _chkWidget.Checked;
                try { _config.Save(); } catch { }
            }
            if (_widget == null || _widget.IsDisposed) return;
            if (_chkWidget.Checked) _widget.ShowNoActivate(); else _widget.Hide();
        };
        Controls.Add(_chkWidget);

        _btnChat = new Button { Location = new Point(16, 464), Size = new Size(128, 30), Text = "Chat" };
        _btnChat.Click += (_, _) => OpenChat();
        _btnSendFile = new Button { Location = new Point(152, 464), Size = new Size(128, 30), Text = "Enviar arquivo" };
        _btnSendFile.Click += (_, _) => PickAndSendFile();
        _btnUpdate = new Button { Location = new Point(288, 464), Size = new Size(132, 30), Text = "Atualizar" };
        _btnUpdate.Click += async (_, _) => await Updater.CheckInteractiveAsync(this, _btnUpdate);
        _btnHelp = new Button { Location = new Point(16, 502), Size = new Size(404, 28), Text = "Ajuda (eco / se escutar sozinho)" };
        _btnHelp.Click += (_, _) => ShowEchoHelp();
        Controls.AddRange(new Control[] { _btnChat, _btnSendFile, _btnUpdate, _btnHelp });
    }

    // Chamado no fim do construtor.
    private void InitPhase2()
    {
        _initializingPhase2 = true;
        _chkNoise.Checked = _config.NoiseSuppress;
        _chkWidget.Checked = _config.ShowWidget;
        _initializingPhase2 = false;
        _noise = new NoiseSuppressor { Enabled = _config.NoiseSuppress };

        // Em teste local (2 instancias compartilham o MESMO config) o machineId seria
        // igual dos dois lados e o dedup do PeerLink mataria a conexao â€” nesse modo
        // usa o id por-execucao. Em producao usa a identidade persistente.
        Guid linkId = _config.AllowLocalPeers ? _instanceId : _config.MachineId;
        _peerLink = new PeerLink(linkId, Environment.MachineName, _config.AllowLocalPeers);
        _peerLink.PeerHello += (id, name) => SafeBeginInvoke(() => OnPeerHello(id, name));
        _peerLink.LinkDown += () => SafeBeginInvoke(UpdateWidget);
        _peerLink.ChatReceived += (text, _) => SafeBeginInvoke(() => OnChatReceived(text));
        _peerLink.PingReceived += () => SafeBeginInvoke(OnPingReceived);

        _fileTransfer = new FileTransferService(_peerLink);
        _fileTransfer.OfferReceived += (tid, nm, sz) => SafeBeginInvoke(() => OnFileOffer(tid, nm, sz));
        _fileTransfer.Progress += (txt, pct) => SafeBeginInvoke(() => _chat?.SetTransfer(txt, pct, true));
        _fileTransfer.Completed += (msg, ok, path) => SafeBeginInvoke(() => OnTransferDone(msg, ok, path));
        _peerLink.Start();

        // Amigo salvo: discagem direta mesmo sem broadcast (outra subrede / tailnet futura).
        // Tambem semeia os campos de voz p/ que um "Conectar" manual reutilize o ultimo IP/porta
        // (o auto-connect de VOZ ainda depende de um beacon na LAN; documentado nas notas).
        if (!string.IsNullOrWhiteSpace(_config.FriendLastIp) && IPAddress.TryParse(_config.FriendLastIp, out var fip))
        {
            _peerLink.SetTarget(fip);
            if (!_connected && !_connecting)
            {
                try { _txtPeerIp.Text = _config.FriendLastIp; } catch { }
                try { _numRemotePort.Value = Math.Clamp(_config.FriendPort, 1, 65535); } catch { }
            }
        }

        _widget = new PingWidget(_config.WidgetX, _config.WidgetY);
        _widget.PingClicked += () => _ = DoPingAsync();
        _widget.ChatClicked += () => OpenChat();
        _widget.ShowMainClicked += () =>
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        };
        _widget.HideWidgetClicked += () => _chkWidget.Checked = false;
        _widget.Moved += (wx, wy) =>
        {
            _config.WidgetX = wx;
            _config.WidgetY = wy;
            try { _config.Save(); } catch { }
        };
        _widget.FileDropped += path => { OpenChat(); _fileTransfer?.OfferFile(path); };
        if (_config.ShowWidget) _widget.ShowNoActivate();
        UpdateWidget();

        _phase2Timer = new System.Windows.Forms.Timer { Interval = 500 };
        _phase2Timer.Tick += (_, _) => UpdateWidget();
        _phase2Timer.Start();

        Updater.AutoCheckLater(this, _btnUpdate, () => SafeBeginInvoke(() => _widget?.SetUpdateBadge(true)));
    }

    // Chamado no OnFormClosing (roda 1x, apos o guard _closing), antes do TeardownAsync.
    private void ShutdownPhase2()
    {
        try { _phase2Timer?.Stop(); } catch { }
        _phase2Timer = null;
        try { _widget?.Close(); _widget?.Dispose(); } catch { }
        _widget = null;
        try { _chat?.ForceClose(); } catch { }
        _chat = null;
        var ft = _fileTransfer; _fileTransfer = null;
        var pl = _peerLink; _peerLink = null;
        try { ft?.Dispose(); } catch { }
        try { pl?.Dispose(); } catch { } // so fecha sockets/cancela: nao bloqueia a UI
        try { _noise?.Dispose(); } catch { }
        _noise = null;
    }

    // Filtro anti "se escutar sozinha": beacon da PROPRIA maquina nunca vira par.
    private bool ShouldIgnoreBeacon(IPAddress ip, Guid machineId, string name)
    {
        if (_config.AllowLocalPeers) return false; // modo de teste local liberado
        if (AppEnv.IsOwnAddress(ip)) return true;
        if (machineId != Guid.Empty && machineId == _config.MachineId) return true;
        if (!string.IsNullOrEmpty(name) &&
            string.Equals(name, Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Chamado no fim do Connect() com a voz de pe.
    private void OnVoiceConnected(IPAddress peerIp)
    {
        _peerLink?.SetTarget(peerIp);
        if (_config.ShowWidget) _widget?.ShowNoActivate();
        UpdateWidget();
    }

    private void OnPeerHello(Guid id, string name)
    {
        // Virou amigo: salva identidade + ultimo IP p/ reconectar facil depois.
        _config.FriendId = id;
        _config.FriendName = name;
        var addr = _peerLink?.PeerAddress;
        if (addr != null) _config.FriendLastIp = addr.ToString();
        _config.FriendPort = (int)_numRemotePort.Value;
        try { _config.Save(); } catch { }
        Log.Write($"amigo salvo: {name} ({id:N}) ip={_config.FriendLastIp}");
        _chat?.AddSystem("Conectado com " + name);
        if (_config.ShowWidget) _widget?.ShowNoActivate();
        UpdateWidget();
    }

    private void OnChatReceived(string text)
    {
        Log.Write($"chat: recebido ({text.Length} chars)");
        EnsureChat();
        _chat!.AddIncoming(DisplayPeerName(), text);
        if (!_chat.Visible)
        {
            _unread++;
            _widget?.SetUnread(_unread);
            if (_config.ShowWidget) _widget?.ShowNoActivate();
        }
    }

    private async Task DoPingAsync()
    {
        long now = Environment.TickCount64;
        if (now - _lastPingSent < 2000) return; // anti-spam: max 1 ping / 2s
        _lastPingSent = now;
        if (_peerLink != null && await _peerLink.SendPingAsync())
            _chat?.AddSystem("Ping enviado");
    }

    private void OnPingReceived()
    {
        Log.Write("ping: recebido");
        if (_config.ShowWidget) _widget?.ShowNoActivate(); // pisca mesmo com a janela minimizada
        _widget?.Flash();   // pulso de cor forte ~2s + hora do ultimo ping
        AppEnv.PlayChime(); // som de atencao gerado em memoria (2 tons)
        _chat?.AddSystem("Ping recebido");
    }

    private void EnsureChat()
    {
        if (_chat != null && !_chat.IsDisposed) return;
        _chat = new ChatWindow(
            async text => _peerLink != null && await _peerLink.SendChatAsync(text),
            path => _fileTransfer?.OfferFile(path));
        _chat.CancelTransfer += () => _fileTransfer?.Cancel();
    }

    private void OpenChat()
    {
        EnsureChat();
        _chat!.Show();
        _chat.WindowState = FormWindowState.Normal;
        _chat.BringToFront();
        _unread = 0;
        _widget?.SetUnread(0);
    }

    private void PickAndSendFile()
    {
        using var dlg = new OpenFileDialog { Title = "Enviar arquivo" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        OpenChat();
        _fileTransfer?.OfferFile(dlg.FileName);
    }

    private void OnFileOffer(Guid tid, string name, long size)
    {
        var res = MessageBox.Show(this,
            $"{DisplayPeerName()} quer te enviar um arquivo:\n\n{name}\nTamanho: {AppEnv.FormatBytes(size)}\n\nAceitar e salvar em Downloads\\DuoVoz?",
            "DuoVoz - Receber arquivo", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (res == DialogResult.Yes)
        {
            OpenChat();
            _fileTransfer?.AcceptOffer(tid, name, size);
        }
        else
        {
            _fileTransfer?.RejectOffer(tid);
        }
    }

    private void OnTransferDone(string msg, bool ok, string savedPath)
    {
        EnsureChat();
        _chat!.SetTransfer("", 0, false);
        _chat.AddSystem(msg);
        if (ok && savedPath.Length > 0) _chat.ShowOpenFolder(savedPath);
    }

    private string DisplayPeerName()
    {
        var pl = _peerLink;
        if (pl != null && !string.IsNullOrEmpty(pl.PeerName)) return pl.PeerName;
        return _config.FriendName.Length > 0 ? _config.FriendName : "Par";
    }

    private void UpdateWidget()
    {
        if (_widget == null || _widget.IsDisposed) return;
        bool linkUp = _peerLink?.IsConnected ?? false;
        Color dot;
        if (_connected)
        {
            long ticks = Interlocked.Read(ref _lastPacketTicks);
            bool got = Interlocked.Read(ref _hasReceivedAny) == 1;
            double age = got
                ? (DateTime.UtcNow.Ticks - ticks) / (double)TimeSpan.TicksPerSecond
                : double.MaxValue;
            dot = age < 3 ? Color.LimeGreen : Color.Orange; // verde ok / laranja sem pacotes
        }
        else
        {
            dot = linkUp ? Color.Orange : Color.Firebrick;  // vermelho offline
        }
        string name = linkUp && !string.IsNullOrEmpty(_peerLink!.PeerName)
            ? "Amiga: " + _peerLink.PeerName
            : _config.FriendName.Length > 0 ? "Amiga: " + _config.FriendName + " (off)" : "Sem par";
        _widget.SetStatus(dot, name);
    }

    // Substitui o FlushFramesAndSend na voz: aplica supressao de ruido POR FRAME
    // exato de 960 bytes (o FrameAccumulator garante 480 amostras/10ms por frame).
    private void FlushVoiceFramesAndSend()
    {
        var udp = _udp;
        var ep = _remoteEp;
        if (udp == null || ep == null) return;

        while (_micAcc.TryDequeueFrame(_micPacket, HeaderBytes, FrameBytes))
        {
            _noise?.Process(_micPacket, HeaderBytes, FrameBytes);
            uint seq = _voiceSeq++;
            _micPacket[0] = StreamVoice;
            _micPacket[1] = (byte)(seq & 0xFF);
            _micPacket[2] = (byte)((seq >> 8) & 0xFF);
            _micPacket[3] = (byte)((seq >> 16) & 0xFF);
            _micPacket[4] = (byte)((seq >> 24) & 0xFF);
            try { udp.Send(_micPacket, _micPacket.Length, ep); }
            catch (System.Net.Sockets.SocketException) { /* peer ausente: ignora */ }
            catch (ObjectDisposedException) { return; }
        }
    }

    // Compartilhar musica: process-loopback excluindo o PROPRIO DuoVoz (a musica
    // enviada NUNCA contem a voz do parceiro tocada por nos). Fallback silencioso.
    private IWaveIn CreateLoopbackCapture()
    {
        try
        {
            var plc = ProcessLoopbackCapture.ExcludingSelf();
            plc.Prepare(); // ativa aqui: qualquer falha cai no fallback abaixo
            Log.Write("loopback: process-loopback ativo (exclui o audio do proprio DuoVoz)");
            return plc;
        }
        catch (Exception ex)
        {
            Log.Write("loopback: process-loopback indisponivel, captura legada: " + ex.Message);
            return new WasapiLoopbackCapture(); // legado: musica pode conter eco da chamada
        }
    }

    private void ShowEchoHelp()
    {
        MessageBox.Show(this,
            "Se alguem SE ESCUTA SEMPRE (mesmo sem compartilhar musica), verificar NO PC DE QUEM SE ESCUTA:\n\n" +
            "1) Painel de Som > Gravacao > Microfone > Propriedades > aba Escutar > DESMARCAR \"Ouvir este dispositivo\".\n\n" +
            "2) Sidetone/retorno do headset no software do fabricante (ex.: Logitech G HUB) - desligar.\n\n" +
            "3) Nunca abrir o DuoVoz duas vezes no mesmo PC (o app agora bloqueia sozinho).\n\n" +
            "4) Ambos devem usar FONE DE OUVIDO - caixa de som volta pro microfone e vira eco.",
            "DuoVoz - Ajuda", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void SafeBeginInvoke(Action a)
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(a); } catch { /* handle destruido no meio */ }
    }
}
