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
    private ToolStripMenuItem _miNoise = null!;
    private ToolStripMenuItem _miWidget = null!;
    private ToolStripMenuItem _miAutoConnect = null!;
    private IconButton _btnUpdate = null!;
    private IconButton _btnSendFile = null!;
    private IconButton _btnChat = null!;
    private IconButton _btnPing = null!;
    private IconButton _btnConfig = null!;
    private IconButton _btnMediaPrev = null!;
    private IconButton _btnMediaPlay = null!;
    private IconButton _btnMediaNext = null!;
    private MediaKeyHook? _mediaHook;
    private Panel _npRow = null!;                 // "tocando agora"
    private string _nowPlayingText = "";
    private System.Windows.Forms.Timer? _npTimer;
    private TrackInfo _peerTrack;                 // faixa que o par mandou (o que ELE escuta)
    private long _peerTrackTicks;
    private string _lastSentNp = "";
    private long _lastNpSentAt;
    private int _unread;
    private long _lastPingSent;
    private bool _initializingPhase2; // suprime a escrita de config nos handlers durante o init

    // Chamado no fim do BuildUi (recebe o Y atual do layout via campo _phase2Y).
    private int _phase2Y;
    private ContextMenuStrip _configMenu = null!;
    private void BuildPhase2Ui()
    {
        int padX = 16;
        int y = _phase2Y > 0 ? _phase2Y : ClientSize.Height - 120;
        int contentW = ClientSize.Width - padX * 2;

        // â”€â”€ 10. Acoes (cada uma UMA vez): Chat, Ping, Arquivo, Atualizar, Config â”€â”€
        int nBtns = 5;
        int gap = 6;
        int bw = (contentW - gap * (nBtns - 1)) / nBtns;
        int bh = 56;
        int bx = padX;

        _btnChat = MakeAction("chat", "Chat", bx, y, bw, bh); bx += bw + gap;
        _btnChat.Click += (_, _) => OpenChat();
        _btnPing = MakeAction("bell", "Ping", bx, y, bw, bh); bx += bw + gap;
        _btnPing.Click += (_, _) => _ = DoPingAsync();
        _btnSendFile = MakeAction("upload", "Arquivo", bx, y, bw, bh); bx += bw + gap;
        _btnSendFile.Click += (_, _) => PickAndSendFile();
        _btnUpdate = MakeAction("refresh", "Atualizar", bx, y, bw, bh); bx += bw + gap;
        _btnUpdate.Click += async (_, _) => await Updater.CheckInteractiveAsync(this, _btnUpdate);
        _btnConfig = MakeAction("gear", "Config", bx, y, bw, bh);
        _btnConfig.Click += (_, _) => _configMenu.Show(_btnConfig, new Point(0, _btnConfig.Height));
        y += bh + 12;

        // â”€â”€ Menu Config (overflow): ruido, widget, auto-conectar, ajuda, sair â”€â”€
        _configMenu = new ContextMenuStrip { Font = CherryTheme.Body };
        _miNoise = new ToolStripMenuItem("Supressao de ruido") { CheckOnClick = true };
        _miNoise.CheckedChanged += (_, _) =>
        {
            if (_noise != null) _noise.Enabled = _miNoise.Checked;
            if (_initializingPhase2) return;
            _config.NoiseSuppress = _miNoise.Checked;
            try { _config.Save(); } catch { }
        };
        _miWidget = new ToolStripMenuItem("Mostrar widget flutuante") { CheckOnClick = true };
        _miWidget.CheckedChanged += (_, _) =>
        {
            if (!_initializingPhase2)
            {
                _config.ShowWidget = _miWidget.Checked;
                try { _config.Save(); } catch { }
            }
            if (_widget == null || _widget.IsDisposed) return;
            if (_miWidget.Checked) _widget.ShowNoActivate(); else _widget.Hide();
        };
        // Espelha o toggle "Conectar sozinho" da tela p/ o menu (mesma flag).
        _miAutoConnect = new ToolStripMenuItem("Conectar sozinho (achar par)") { CheckOnClick = true };
        _miAutoConnect.CheckedChanged += (_, _) =>
        {
            if (_chkAutoConnect != null && _chkAutoConnect.Checked != _miAutoConnect.Checked)
                _chkAutoConnect.Checked = _miAutoConnect.Checked;
        };
        _configMenu.Items.Add(_miNoise);
        _configMenu.Items.Add(_miWidget);
        _configMenu.Items.Add(_miAutoConnect);
        _configMenu.Items.Add(new ToolStripSeparator());
        _configMenu.Items.Add("Ajuda (eco / se escutar sozinho)", null, (_, _) => ShowEchoHelp());
        _configMenu.Items.Add("Sair", null, (_, _) => { _forceQuit = true; Close(); }); // sair de verdade (nao esconder na bandeja)

        // â”€â”€ 11. Botao Conectar/Desconectar full-width (rosa) â”€â”€
        _btnConnect = new PillButton
        {
            Location = new Point(padX, y),
            Size = new Size(contentW, 44),
            Text = "Conectar",
            IconName = "phone",
            FillColor = CherryTheme.Pink,
        };
        _btnConnect.Click += OnConnectClick;
        Controls.Add(_btnConnect);
        y += 52;

        // Ajusta a altura final da janela ao conteudo.
        ClientSize = new Size(ClientSize.Width, y + 8);
    }

    // Linha de controle remoto da musica do par (Spotify etc). Os botoes mandam
    // um comando de midia pro par, que injeta a tecla de midia global no PC dele.
    private void AddMediaRow(int padX, int contentW, ref int y)
    {
        var lbl = new Label
        {
            Text = "Musica do par",
            Location = new Point(padX, y + 12),
            Size = new Size(160, 18),
            Font = CherryTheme.Body,
            ForeColor = CherryTheme.Muted,
            BackColor = CherryTheme.Panel,
        };
        Controls.Add(lbl);

        int bw = 46, bh = 40, gap = 8;
        int bx = padX + contentW - (bw * 3 + gap * 2);
        _btnMediaPrev = new IconButton { IconName = "skipPrev", Size = new Size(bw, bh), Location = new Point(bx, y), Enabled = false };
        _btnMediaPlay = new IconButton { IconName = "playPause", Size = new Size(bw, bh), Location = new Point(bx + bw + gap, y), Enabled = false };
        _btnMediaNext = new IconButton { IconName = "skipNext", Size = new Size(bw, bh), Location = new Point(bx + 2 * (bw + gap), y), Enabled = false };
        _btnMediaPrev.Click += (_, _) => _ = _peerLink?.SendMediaAsync(MediaAction.Prev);
        _btnMediaPlay.Click += (_, _) => _ = _peerLink?.SendMediaAsync(MediaAction.PlayPause);
        _btnMediaNext.Click += (_, _) => _ = _peerLink?.SendMediaAsync(MediaAction.Next);
        Controls.Add(_btnMediaPrev);
        Controls.Add(_btnMediaPlay);
        Controls.Add(_btnMediaNext);
        y += bh + 10;
    }

    // Linha "tocando agora" (musica que estamos ouvindo): icone + titulo/artista.
    private void AddNowPlayingRow(int padX, int contentW, ref int y)
    {
        _npRow = new Panel { Location = new Point(padX, y), Size = new Size(contentW, 22), BackColor = CherryTheme.Panel };
        _npRow.Paint += PaintNowPlaying;
        Controls.Add(_npRow);
        y += 26;
    }

    private void PaintNowPlaying(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        if (string.IsNullOrEmpty(_nowPlayingText))
        {
            TextRenderer.DrawText(g, "Nada tocando", CherryTheme.BodySmall,
                new Rectangle(0, 0, _npRow.Width, _npRow.Height), CherryTheme.Dim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            return;
        }
        var iconR = new Rectangle(0, (_npRow.Height - 16) / 2, 16, 16);
        CherryIcons.Draw(g, "music", iconR, CherryTheme.PinkDeep);
        TextRenderer.DrawText(g, _nowPlayingText, CherryTheme.Body,
            new Rectangle(22, 0, _npRow.Width - 22, _npRow.Height), CherryTheme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    // Le a faixa local (SMTC), envia ao par se eu for o DJ, e mostra a faixa que estamos
    // ouvindo (a do par quando recebo a musica dele; senao a minha).
    private async Task UpdateNowPlaying()
    {
        try
        {
            TrackInfo local;
            try { local = await NowPlaying.GetAsync(); }
            catch { local = default; }
            if (IsDisposed || _npRow == null || _npRow.IsDisposed) return;

            var pl = _peerLink;
            if (pl != null && pl.IsConnected) // sempre envia MINHA faixa (ela mostra o que eu escuto)
            {
                string key = $"{local.Title}{local.Artist}{local.Playing}";
                if (key != _lastSentNp || Environment.TickCount64 - _lastNpSentAt > 10000)
                {
                    _lastSentNp = key;
                    _lastNpSentAt = Environment.TickCount64;
                    _ = pl.SendNowPlayingAsync(local.Title, local.Artist, local.Playing);
                }
            }
            else { _lastSentNp = ""; _lastNpSentAt = 0; }

            // Exibicao usa SO a faixa do par (o que ELA escuta), nunca a minha.
            double peerAge = (DateTime.UtcNow.Ticks - _peerTrackTicks) / (double)TimeSpan.TicksPerSecond;
            bool showPeer = (pl?.IsConnected ?? false) && _peerTrack.HasTrack && peerAge < 30.0;
            TrackInfo show = showPeer ? _peerTrack : default;

            string text = show.HasTrack ? show.Display : "";
            if (text != _nowPlayingText)
            {
                _nowPlayingText = text;
                _npRow.Invalidate();
            }
        }
        catch (Exception ex) { Log.Write("nowplaying tick: " + ex.Message); }
    }

    // True => a tecla de midia foi ENCAMINHADA ao par (e suprimida aqui). So intercepta
    // quando estou OUVINDO a musica do par (recebendo StreamMusic ha < 2s) e NAO estou
    // compartilhando a minha — ai minha tecla controla o player DELE. Fora disso, deixa
    // passar pro meu proprio player local.
    private bool OnLocalMediaKey(MediaAction a)
    {
        var pl = _peerLink;
        if (pl == null || !pl.IsConnected) return false;
        if (_chkShareMusic != null && _chkShareMusic.Checked) return false; // eu sou o DJ
        long ticks = Interlocked.Read(ref _lastMusicRecvTicks);
        double age = (DateTime.UtcNow.Ticks - ticks) / (double)TimeSpan.TicksPerSecond;
        if (age > 2.0) return false; // nao estou recebendo a musica dele agora
        _ = pl.SendMediaAsync(a);
        return true;
    }

    private IconButton MakeAction(string icon, string caption, int x, int y, int w, int h)
    {
        var b = new IconButton
        {
            IconName = icon,
            Caption = caption,
            Location = new Point(x, y),
            Size = new Size(w, h),
        };
        Controls.Add(b);
        return b;
    }

    // Chamado no fim do construtor.
    private void InitPhase2()
    {
        _initializingPhase2 = true;
        _miNoise.Checked = _config.NoiseSuppress;
        _miWidget.Checked = _config.ShowWidget;
        _miAutoConnect.Checked = _chkAutoConnect.Checked;
        _initializingPhase2 = false;
        _noise = new NoiseSuppressor { Enabled = _config.NoiseSuppress };

        // Em teste local (2 instancias compartilham o MESMO config) o machineId seria
        // igual dos dois lados e o dedup do PeerLink mataria a conexao â€” nesse modo
        // usa o id por-execucao. Em producao usa a identidade persistente.
        Guid linkId = _config.AllowLocalPeers ? _instanceId : _config.MachineId;
        _peerLink = new PeerLink(linkId, Environment.MachineName, _config.AllowLocalPeers);
        _peerLink.PeerHello += (id, name) => SafeBeginInvoke(() => OnPeerHello(id, name));
        _peerLink.LinkDown += () => SafeBeginInvoke(() => { _peerTrack = default; UpdateWidget(); });
        _peerLink.ChatReceived += (text, _) => SafeBeginInvoke(() => OnChatReceived(text));
        _peerLink.PingReceived += () => SafeBeginInvoke(OnPingReceived);
        // Comando de midia do par: injeta a tecla de midia global aqui (controla meu player).
        // keybd_event e seguro fora da UI thread; nao precisa marshalar.
        _peerLink.MediaReceived += a => { try { MediaKeys.Send(a); } catch { } };
        _peerLink.NowPlayingReceived += (title, artist, playing) => SafeBeginInvoke(() =>
        {
            _peerTrack = new TrackInfo(title, artist, playing);
            _peerTrackTicks = DateTime.UtcNow.Ticks;
        });

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
        _widget.HideWidgetClicked += () => _miWidget.Checked = false;
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

        // Hook global de teclas de midia (next/prev/play-pause): minha tecla passa a
        // controlar a musica que EU estou ouvindo do par (quando ELE e o DJ).
        try { _mediaHook = new MediaKeyHook { OnMediaKey = OnLocalMediaKey }; }
        catch (Exception ex) { Log.Write("media hook init falhou: " + ex.Message); }

        // Poll da musica "tocando agora" (~2s).
        _npTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _npTimer.Tick += async (_, _) => await UpdateNowPlaying();
        _npTimer.Start();

        Updater.AutoCheckLater(this, _btnUpdate, () => SafeBeginInvoke(() => _widget?.SetUpdateBadge(true)));
    }

    // Chamado no OnFormClosing (roda 1x, apos o guard _closing), antes do TeardownAsync.
    private void ShutdownPhase2()
    {
        try { _phase2Timer?.Stop(); } catch { }
        _phase2Timer = null;
        try { _npTimer?.Stop(); } catch { }
        _npTimer = null;
        try { _mediaHook?.Dispose(); } catch { }
        _mediaHook = null;
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
        // O widget e TopMost; sem isto o chat abre ATRAS dele. O pulo TopMost on->off
        // joga o chat acima da faixa "sempre no topo" e depois solta (nao fica preso la).
        _chat.TopMost = true;
        _chat.BringToFront();
        _chat.Activate();
        _chat.TopMost = false;
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
        // Aceite automatico, sem popup: a imagem/arquivo cai direto na conversa. Nao
        // rouba foco â€” so avisa no chat e marca nao-lida no widget se ele estiver fechado.
        EnsureChat();
        _chat!.AddSystem($"Recebendo {name} ({AppEnv.FormatBytes(size)})...");
        if (!_chat.Visible)
        {
            _unread++;
            _widget?.SetUnread(_unread);
            if (_config.ShowWidget) _widget?.ShowNoActivate();
        }
        _fileTransfer?.AcceptOffer(tid, name, size);
    }

    private void OnTransferDone(string msg, bool ok, string savedPath)
    {
        EnsureChat();
        _chat!.SetTransfer("", 0, false);
        // Imagem recebida: fica APENAS na conversa (thumbnail inline), sem a linha do
        // caminho nem "Abrir pasta". Ela ainda e salva na pasta Recebidos do app.
        if (ok && savedPath.Length > 0 && IsImagePath(savedPath))
        {
            _chat.AddIncomingImage(savedPath);
            return;
        }
        _chat.AddSystem(msg);
        if (ok && savedPath.Length > 0)
        {
            _chat.ShowOpenFolder(savedPath);
        }
    }

    private static bool IsImagePath(string p)
    {
        string e = System.IO.Path.GetExtension(p).ToLowerInvariant();
        return e is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp";
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
            ? _peerLink.PeerName
            : _config.FriendName.Length > 0 ? _config.FriendName + " (off)" : "Sem par";
        _widget.SetStatus(dot, name);
        bool mediaOn = linkUp;
        if (_btnMediaPrev != null) { _btnMediaPrev.Enabled = mediaOn; _btnMediaPlay.Enabled = mediaOn; _btnMediaNext.Enabled = mediaOn; }
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
            "3) Nunca abrir o CherrySpy duas vezes no mesmo PC (o app agora bloqueia sozinho).\n\n" +
            "4) Ambos devem usar FONE DE OUVIDO - caixa de som volta pro microfone e vira eco.",
            "CherrySpy - Ajuda", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void SafeBeginInvoke(Action a)
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(a); } catch { /* handle destruido no meio */ }
    }
}
