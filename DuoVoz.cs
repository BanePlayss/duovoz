// DuoVoz.cs â€” Phase 1
// Intercomunicador de voz ponto-a-ponto (peer-to-peer) na LAN, para dois PCs.
// Canal de voz privado, totalmente separado do Discord (o Discord nunca ve este audio).
//
// Pipeline:
//   - Microfone (WaveInEvent) -> 48k mono 16-bit PCM -> UDP (streamId 0 = voz).
//   - Audio do sistema (WasapiLoopbackCapture, SEM driver) -> downmix mono + resample
//     48k 16-bit -> UDP (streamId 1 = musica). Ligado pelo checkbox "Compartilhar musica".
//   - Recepcao UDP -> jitter buffer por stream (BufferedWaveProvider, ~1s) ->
//     MixingSampleProvider (float 48k mono, ReadFully) -> saida (WaveOut/WasapiOut).
//   - Heartbeat (streamId 2) periodico p/ deteccao de presenca + manter o caminho de
//     envio "quente" no firewall.
//
// Framing do pacote UDP (little-endian):
//   byte 0      : streamId  (0 = voz, 1 = musica, 2 = heartbeat)
//   bytes 1..4  : uint32 sequence (por stream; usado so p/ diagnostico de perda)
//   bytes 5..N  : payload PCM 16-bit mono 48k (960 bytes p/ um frame de 10 ms).
//                 Heartbeat tem payload vazio.
//   Header = 5 bytes. Frame de 10ms = 480 amostras = 960 bytes -> 965 bytes por datagrama,
//   bem abaixo do MTU (sem fragmentacao IP). 100 pacotes/s por stream.
//
// ECO: Fase 1 NAO tem cancelamento de eco acustico (AEC). Se o dispositivo de SAIDA for
// uma caixa de som (e nao um fone), a voz do parceiro tocada na caixa sera recapturada
// pelo microfone e reenviada, causando eco/microfonia. AMBOS DEVEM USAR FONE DE OUVIDO.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Velopack;

namespace DuoVoz;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Velopack: trata os hooks de instalacao/atualizacao/desinstalacao e sai cedo
        // quando invocado pelo instalador. Sem efeito quando rodado via 'dotnet run'.
        VelopackApp.Build().Run();

        // Init explicito (sem depender do ApplicationConfiguration.Initialize gerado).
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        // Instancia unica: duas instancias no mesmo PC se auto-conectavam uma na outra
        // via beacon local e a pessoa passava a SE ESCUTAR SEMPRE. allowlocalpeers=1
        // no config libera 2 instancias de proposito (teste local).
        var bootConfig = Config.Load();
        var singleInstance = new Mutex(true, @"Local\DuoVoz_SingleInstance_2f9d1c4b", out bool isFirstInstance);
        if (!isFirstInstance && !bootConfig.AllowLocalPeers)
        {
            Log.Write("segunda instancia bloqueada (mutex)");
            MessageBox.Show("O CherrySpy ja esta aberto neste computador.\nUse a janela que ja existe (ou o widget flutuante).",
                "CherrySpy", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Log.Write("=== DuoVoz app start ===");
        Application.Run(new MainForm());
        GC.KeepAlive(singleInstance);
    }
}

/// <summary>Item de combo de dispositivo de saida (endpoint WASAPI de render).</summary>
internal sealed record OutputDeviceItem(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Item de combo de dispositivo de entrada (microfone WaveIn).</summary>
internal sealed record InputDeviceItem(int DeviceNumber, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Buffer de re-fatiamento por stream. Acumula bytes capturados e emite frames exatos
/// de FrameBytes bytes. Toda a manipulacao e protegida por lock para ser segura entre a
/// thread de captura (Append/TryDequeue) e a UI thread (Reset no teardown). Usa um array
/// crescivel + Buffer.BlockCopy (sem List.Add por byte) p/ ser amigavel ao tempo real.
/// </summary>
internal sealed class FrameAccumulator
{
    private readonly object _lock = new();
    private byte[] _buf;
    private int _count;

    public FrameAccumulator(int initialCapacity = 8192)
    {
        _buf = new byte[Math.Max(initialCapacity, 1024)];
        _count = 0;
    }

    public void Append(byte[] data, int offset, int length)
    {
        if (length <= 0) return;
        lock (_lock)
        {
            EnsureCapacity(_count + length);
            Buffer.BlockCopy(data, offset, _buf, _count, length);
            _count += length;
        }
    }

    /// <summary>
    /// Remove e copia o proximo frame completo p/ <paramref name="frame"/> (no offset
    /// <paramref name="frameOffset"/>). Retorna false quando nao ha frame completo.
    /// </summary>
    public bool TryDequeueFrame(byte[] frame, int frameOffset, int frameBytes)
    {
        lock (_lock)
        {
            if (_count < frameBytes) return false;
            Buffer.BlockCopy(_buf, 0, frame, frameOffset, frameBytes);
            int rest = _count - frameBytes;
            if (rest > 0) Buffer.BlockCopy(_buf, frameBytes, _buf, 0, rest);
            _count = rest;
            return true;
        }
    }

    public void Reset()
    {
        lock (_lock) { _count = 0; }
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _buf.Length) return;
        int newCap = _buf.Length * 2;
        while (newCap < needed) newCap *= 2;
        Array.Resize(ref _buf, newCap);
    }
}

/// <summary>
/// Downmix de N canais para mono (media dos canais). O ToMono() do NAudio so aceita
/// estereo (2 canais) e lanca em qualquer outra contagem; saidas em 5.1/7.1 entregam
/// o loopback com 6/8 canais, entao precisamos de um downmix generico. Sample providers
/// sempre entregam float, independente do encoding de origem.
/// </summary>
internal sealed class MultiChannelToMonoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private float[] _srcBuffer = Array.Empty<float>();

    public WaveFormat WaveFormat { get; }

    public MultiChannelToMonoSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = Math.Max(1, source.WaveFormat.Channels);
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_channels <= 1) return _source.Read(buffer, offset, count);

        int needed = count * _channels;
        if (_srcBuffer.Length < needed) _srcBuffer = new float[needed];

        int read = _source.Read(_srcBuffer, 0, needed);
        int frames = read / _channels;
        for (int i = 0; i < frames; i++)
        {
            int baseIdx = i * _channels;
            float sum = 0f;
            for (int c = 0; c < _channels; c++) sum += _srcBuffer[baseIdx + c];
            buffer[offset + i] = sum / _channels;
        }
        return frames;
    }
}

public sealed partial class MainForm : Form
{
    // â”€â”€â”€ Formatos fixos â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private static readonly WaveFormat Pcm48Mono = new(48000, 16, 1);            // (rate, BITS, channels)
    private static readonly WaveFormat Float48Mono = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);

    // Frame de 10 ms = 480 amostras * 2 bytes = 960 bytes de payload.
    private const int FrameBytes = 960;
    private const int HeaderBytes = 5;

    private const byte StreamVoice = 0;
    private const byte StreamMusic = 1;
    private const byte StreamHeartbeat = 2;

    // Alvo de pre-enchimento do jitter buffer (ms) antes de comecar a tocar de verdade.
    private const int JitterPrimeMs = 50;

    // â”€â”€â”€ Audio â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private WaveInEvent? _mic;
    private IWaveIn? _loopback; // WasapiLoopbackCapture (legado) ou ProcessLoopbackCapture
    private IWavePlayer? _output;
    private MixingSampleProvider? _mixer;
    private BufferedWaveProvider? _voiceJitter;   // voz recebida do parceiro
    private BufferedWaveProvider? _musicJitter;   // musica recebida do parceiro

    // Cadeia de conversao do loopback (construida UMA vez quando o formato real e conhecido).
    private BufferedWaveProvider? _loopSrcBuf;
    private SampleToWaveProvider16? _loopTo16;
    private readonly object _loopChainLock = new();

    // Cadeia de conversao do microfone (so usada quando 48k mono nao e nativo no MME).
    private BufferedWaveProvider? _micSrcBuf;
    private SampleToWaveProvider16? _micTo16;
    private readonly object _micChainLock = new();
    private volatile bool _micNeedsConvert;

    // Acumuladores p/ re-fatiar a captura em frames exatos de 960 bytes (thread-safe).
    private readonly FrameAccumulator _micAcc = new();
    private readonly FrameAccumulator _musicAcc = new();

    // Buffer de pacote reutilizado (HeaderBytes + FrameBytes). Cada stream tem o seu p/
    // evitar que mic e musica disputem o mesmo array (threads de captura distintas).
    private readonly byte[] _micPacket = new byte[HeaderBytes + FrameBytes];
    private readonly byte[] _musicPacket = new byte[HeaderBytes + FrameBytes];

    // â”€â”€â”€ Rede â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private UdpClient? _udp;
    private IPEndPoint? _remoteEp;
    private Thread? _rxThread;
    private volatile bool _running;
    private uint _voiceSeq;
    private uint _musicSeq;
    private System.Threading.Timer? _heartbeatTimer;

    // Timestamp do ultimo pacote recebido (ticks) â€” escrito na rx thread, lido na UI timer.
    private long _lastPacketTicks;
    private long _hasReceivedAny; // 0/1 via Interlocked

    // Diagnostico de perda (so leitura informacional).
    private long _voiceLastSeq = -1;
    private long _musicLastSeq = -1;
    private long _packetsLost;

    // Priming dos jitter buffers (insere silencio inicial p/ ter folga de jitter).
    private long _voicePrimed; // 0/1 via Interlocked
    private long _musicPrimed; // 0/1 via Interlocked

    // â”€â”€â”€ VU meters (peak amostrado pela UI timer, nao por frame) â”€â”€â”€â”€â”€
    private int _micPeak;       // 0..100, escrito no callback do mic
    private int _voicePeak;     // 0..100, escrito no callback de recepcao de voz

    // â”€â”€â”€ Estado de controle â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private bool _connected;
    private volatile bool _muted;
    private volatile bool _pushToTalk;
    private volatile bool _pttKeyDown;
    private bool _closing;          // FormClosing ja iniciado
    private bool _readyToClose;     // teardown concluido -> pode fechar de verdade

    // â”€â”€â”€ Bandeja do sistema â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Fechar no X apenas esconde na bandeja; so sai de verdade pelo menu "Sair".
    private NotifyIcon? _tray;
    private bool _forceQuit;        // true => fechar de verdade (menu Sair / shutdown)
    private bool _trayHintShown;    // balao "continua rodando" ja exibido 1x

    // â”€â”€â”€ Volume ao vivo (mixer) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Mantidos como campos p/ ajustar o volume enquanto o audio toca (write de float
    // e atomico; alterar .Volume da UI thread durante o playback e seguro).
    private VolumeSampleProvider? _voiceVol;
    private VolumeSampleProvider? _musicVol;

    // â”€â”€â”€ Controles WinForms â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private ComboBox _cboInput = null!;
    private ComboBox _cboOutput = null!;
    private TextBox _txtPeerIp = null!;
    private NumericUpDown _numLocalPort = null!;
    private NumericUpDown _numRemotePort = null!;
    private PillButton _btnConnect = null!;
    private ToggleSwitch _chkMute = null!;   // ON = microfone aberto (nao mudo)
    private ToggleSwitch _chkPtt = null!;
    private ToggleSwitch _chkShareMusic = null!;
    private Label _lblStatus = null!;
    private Label _lblDiag = null!;
    private ProgressBar _vuMicOut = null!;
    private ProgressBar _vuPeerIn = null!;
    private System.Windows.Forms.Timer _uiTimer = null!;
    private ToggleSwitch _chkAutoConnect = null!;
    private VolumeSlider _sldVoice = null!;
    private VolumeSlider _sldMusic = null!;
    private Label _lblVoicePct = null!;
    private Label _lblMusicPct = null!;
    private Label _lblStatusDot = null!;
    private Label _lblPeerChip = null!;

    // Header / janela sem borda (arrastavel pelo header).
    private Panel _header = null!;
    private const int HeaderH = 52;

    // Descoberta na rede + guardas de auto-conexao
    private DiscoveryService? _discovery;
    private readonly Guid _instanceId = Guid.NewGuid();
    private Config _config = new();
    private bool _connecting;            // Connect() em andamento (anti-reentrancia)
    private Guid _chosenPeerId;          // par escolhido p/ esta sessao (anti-hijack)
    private long _autoConnectCooldownTicks; // cooldown apos auto-connect falho

    public MainForm()
    {
        _config = Config.Load();
        _config.EnsureMachineId(); // dono unico da geracao do id persistente (Load nao escreve)
        BuildUi();
        PopulateDevices();
        ApplyConfigToUi();
        WireKeyboardForPtt();
        StartDiscovery();
        InitPhase2(); // canal de controle, chat, widget, supressao de ruido, updater
    }

    // â”€â”€â”€ CONSTRUCAO DA UI â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private void BuildUi()
    {
        // â”€â”€â”€ Janela sem borda, arredondada, arrastavel pelo header (CherrySpy) â”€â”€â”€
        Text = "CherrySpy";
        Font = CherryTheme.Body;
        FormBorderStyle = FormBorderStyle.None;
        BackColor = CherryTheme.Panel;
        ForeColor = CherryTheme.Text;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(404, 560);
        KeyPreview = true; // p/ capturar a tecla de push-to-talk
        var appIcon = AppEnv.LoadAppIcon();
        if (appIcon != null) Icon = appIcon;

        int padX = 16;      // padding lateral do painel
        int contentW = ClientSize.Width - padX * 2; // 372
        int y = 0;

        // â”€â”€ 1. HEADER (unica alca de arraste) â”€â”€
        _header = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(ClientSize.Width, HeaderH),
            BackColor = CherryTheme.Panel,
        };
        _header.Paint += PaintHeader;
        _header.MouseDown += Header_MouseDown; // arraste via WM_NCLBUTTONDOWN/HTCAPTION
        Controls.Add(_header);
        // Botoes minimizar/fechar (pintados) no canto direito do header.
        var btnClose = new IconButton { IconName = "close", Size = new Size(30, 30), Location = new Point(ClientSize.Width - 38, 11) };
        btnClose.Click += (_, _) => Close();          // dispara o FormClosing/teardown existente
        var btnMin = new IconButton { IconName = "minimize", Size = new Size(30, 30), Location = new Point(ClientSize.Width - 72, 11) };
        btnMin.Click += (_, _) => WindowState = FormWindowState.Minimized;
        _header.Controls.Add(btnClose);
        _header.Controls.Add(btnMin);
        y = HeaderH + 6;

        // â”€â”€ 2. Status row: bolinha + "Conectado â€” <peer>" + chip de iniciais â”€â”€
        _lblStatusDot = new Label
        {
            Location = new Point(padX, y + 4),
            Size = new Size(12, 12),
            BackColor = CherryTheme.Panel,
        };
        _lblStatusDot.Paint += (s, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var br = new SolidBrush(_connected ? CherryTheme.PinkDeep : CherryTheme.Dim);
            e.Graphics.FillEllipse(br, 0, 0, 11, 11);
        };
        Controls.Add(_lblStatusDot);

        _lblStatus = new Label
        {
            Location = new Point(padX + 20, y),
            Size = new Size(contentW - 20 - 40, 20),
            Text = "Desconectado",
            Font = CherryTheme.Head,
            ForeColor = CherryTheme.Text,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = CherryTheme.Panel,
        };
        Controls.Add(_lblStatus);

        _lblPeerChip = new Label
        {
            Location = new Point(ClientSize.Width - padX - 34, y - 5),
            Size = new Size(34, 30),
            Text = "?",
            Font = CherryTheme.Label,
            ForeColor = CherryTheme.PinkDeep,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = CherryTheme.PinkGhost,
        };
        _lblPeerChip.Paint += (s, e) =>
        {
            var lbl = (Label)s!;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var gp = GfxExt.RoundedPath(new RectangleF(0, 0, lbl.Width - 1, lbl.Height - 1), 8))
            using (var br = new SolidBrush(CherryTheme.PinkGhost))
                e.Graphics.FillPath(br, gp);
            TextRenderer.DrawText(e.Graphics, lbl.Text, CherryTheme.Label, lbl.ClientRectangle,
                CherryTheme.PinkDeep, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };
        Controls.Add(_lblPeerChip);
        y += 34;

        AddDivider(padX, ref y, contentW);

        // â”€â”€ 4. Dispositivos (combos existentes, restilizados) â”€â”€
        AddSmallLabel("MICROFONE", padX, y);
        y += 18;
        _cboInput = new ComboBox
        {
            Location = new Point(padX, y),
            Size = new Size(contentW, 26),
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            DrawMode = DrawMode.OwnerDrawFixed,
            BackColor = CherryTheme.Soft,
            ForeColor = CherryTheme.Text,
            Font = CherryTheme.Body,
            ItemHeight = 20,
        };
        _cboInput.DrawItem += Combo_DrawItem;
        Controls.Add(_cboInput);
        y += 34;

        AddSmallLabel("SAIDA", padX, y);
        y += 18;
        _cboOutput = new ComboBox
        {
            Location = new Point(padX, y),
            Size = new Size(contentW, 26),
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            DrawMode = DrawMode.OwnerDrawFixed,
            BackColor = CherryTheme.Soft,
            ForeColor = CherryTheme.Text,
            Font = CherryTheme.Body,
            ItemHeight = 20,
        };
        _cboOutput.DrawItem += Combo_DrawItem;
        Controls.Add(_cboOutput);
        y += 36;

        AddDivider(padX, ref y, contentW);

        // â”€â”€ 6. VOLUME (voz + musica) â”€â”€
        AddSmallLabel("VOLUME", padX, y);
        y += 20;

        var lblVoiceCap = new Label { Text = "Voz", Location = new Point(padX, y + 1), Size = new Size(44, 20), Font = CherryTheme.Body, ForeColor = CherryTheme.Muted, BackColor = CherryTheme.Panel };
        Controls.Add(lblVoiceCap);
        _sldVoice = new VolumeSlider { Location = new Point(padX + 48, y), Size = new Size(contentW - 48 - 44, 22) };
        _lblVoicePct = new Label { Location = new Point(ClientSize.Width - padX - 40, y + 1), Size = new Size(40, 20), Text = "90%", Font = CherryTheme.Mono, ForeColor = CherryTheme.Text, TextAlign = ContentAlignment.MiddleRight, BackColor = CherryTheme.Panel };
        _sldVoice.ValueChanged += (_, _) =>
        {
            _lblVoicePct.Text = _sldVoice.Value + "%";
            var vv = _voiceVol; if (vv != null) vv.Volume = _sldVoice.Value / 100f;
            _config.VoiceVolume = _sldVoice.Value;
            if (!_initializingPhase2) { try { _config.Save(); } catch { } }
        };
        Controls.Add(_sldVoice);
        Controls.Add(_lblVoicePct);
        y += 28;

        var lblMusicCap = new Label { Text = "Musica", Location = new Point(padX, y + 1), Size = new Size(48, 20), Font = CherryTheme.Body, ForeColor = CherryTheme.Muted, BackColor = CherryTheme.Panel };
        Controls.Add(lblMusicCap);
        _sldMusic = new VolumeSlider { Location = new Point(padX + 48, y), Size = new Size(contentW - 48 - 44, 22) };
        _lblMusicPct = new Label { Location = new Point(ClientSize.Width - padX - 40, y + 1), Size = new Size(40, 20), Text = "50%", Font = CherryTheme.Mono, ForeColor = CherryTheme.Text, TextAlign = ContentAlignment.MiddleRight, BackColor = CherryTheme.Panel };
        _sldMusic.ValueChanged += (_, _) =>
        {
            _lblMusicPct.Text = _sldMusic.Value + "%";
            var mv = _musicVol; if (mv != null) mv.Volume = _sldMusic.Value / 100f;
            _config.MusicVolume = _sldMusic.Value;
            if (!_initializingPhase2) { try { _config.Save(); } catch { } }
        };
        Controls.Add(_sldMusic);
        Controls.Add(_lblMusicPct);
        y += 32;

        AddDivider(padX, ref y, contentW);

        // â”€â”€ 8. Toggles (microfone aberto / compartilhar musica / PTT) â”€â”€
        // Microfone aberto: ON = NAO mudo (invertido em relacao a _muted).
        _chkMute = AddToggleRow("micOff", "Microfone aberto", "desligado = mudo", padX, ref y);
        _chkMute.Checked = true; // abre por padrao (nao mudo)
        _chkMute.CheckedChanged += (_, _) => _muted = !_chkMute.Checked;

        _chkShareMusic = AddToggleRow("music", "Compartilhar musica", "manda o som do seu PC", padX, ref y);
        _chkShareMusic.CheckedChanged += OnShareMusicChanged;
        _chkShareMusic.Enabled = false; // so apos conectar

        _chkPtt = AddToggleRow("mic", "Falar apertando (push-to-talk)", "segura Espaco pra falar (janela em foco)", padX, ref y);
        _chkPtt.CheckedChanged += (_, _) =>
        {
            _pushToTalk = _chkPtt.Checked;
            _pttKeyDown = false;
        };

        _chkAutoConnect = AddToggleRow("refresh", "Conectar sozinho", "acha o par na rede", padX, ref y);
        _chkAutoConnect.Checked = true;
        // Back-sync: flip do switch na tela atualiza o check do menu Config (o guard de
        // igualdade em cada lado evita loop de CheckedChanged). _miAutoConnect e criado
        // em BuildPhase2Ui, que roda logo abaixo antes de qualquer evento de UI disparar.
        _chkAutoConnect.CheckedChanged += (_, _) =>
        {
            if (_miAutoConnect != null && _miAutoConnect.Checked != _chkAutoConnect.Checked)
                _miAutoConnect.Checked = _chkAutoConnect.Checked;
        };

        AddDivider(padX, ref y, contentW);

        // Campos de rede (ocultos: usados pela conexao/descoberta, sem UI no mockup).
        _txtPeerIp = new TextBox { Text = "127.0.0.1", Visible = false };
        _numLocalPort = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 50777, Visible = false };
        _numRemotePort = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 50777, Visible = false };
        Controls.Add(_txtPeerIp);
        Controls.Add(_numLocalPort);
        Controls.Add(_numRemotePort);

        // VU meters (mantidos, discretos) â€” voz recebida e microfone enviado.
        _vuMicOut = new ProgressBar { Location = new Point(padX, y), Size = new Size(contentW, 6), Minimum = 0, Maximum = 100, Style = ProgressBarStyle.Continuous };
        Controls.Add(_vuMicOut);
        y += 10;
        _vuPeerIn = new ProgressBar { Location = new Point(padX, y), Size = new Size(contentW, 6), Minimum = 0, Maximum = 100, Style = ProgressBarStyle.Continuous };
        Controls.Add(_vuPeerIn);
        y += 12;

        // Diagnostico (jitter / perda).
        _lblDiag = new Label
        {
            Location = new Point(padX, y),
            Size = new Size(contentW, 16),
            Text = "",
            ForeColor = CherryTheme.Dim,
            Font = CherryTheme.MonoSmall,
            BackColor = CherryTheme.Panel,
        };
        Controls.Add(_lblDiag);
        y += 20;

        _phase2Y = y;
        BuildPhase2Ui(); // acoes (chat/ping/arquivo/atualizar/config) + botao Conectar

        // Timer da UI (status + VU meters) ~250 ms.
        _uiTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _uiTimer.Tick += OnUiTick;
        _uiTimer.Start();

        FormClosing += OnFormClosing;
        Resize += (_, _) => { if (WindowState == FormWindowState.Normal) ApplyRoundedRegion(); };
        Load += (_, _) => ApplyRoundedRegion();

        SetupTray();
    }

    // â”€â”€â”€ BANDEJA DO SISTEMA â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Icone na bandeja + menu (Abrir / Sair). Fechar no X esconde aqui em vez de sair.
    private void SetupTray()
    {
        var menu = new ContextMenuStrip();
        var miOpen = new ToolStripMenuItem("Abrir");
        miOpen.Font = new Font(miOpen.Font, FontStyle.Bold); // acao padrao (double-click)
        miOpen.Click += (_, _) => RestoreFromTray();
        var miQuit = new ToolStripMenuItem("Sair");
        miQuit.Click += (_, _) => { _forceQuit = true; Close(); };
        menu.Items.Add(miOpen);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miQuit);

        _tray = new NotifyIcon
        {
            Text = "CherrySpy",
            Icon = AppEnv.LoadAppIcon() ?? Icon ?? SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => RestoreFromTray();
    }

    // Esconde a janela na bandeja (chamado ao fechar no X).
    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
        if (!_trayHintShown && _tray != null)
        {
            _trayHintShown = true;
            try
            {
                _tray.ShowBalloonTip(3000, "CherrySpy",
                    "Continua rodando aqui. Clique p/ abrir; botao direito p/ Sair.",
                    ToolTipIcon.Info);
            }
            catch { }
        }
    }

    // Traz a janela de volta da bandeja.
    private void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    // â”€â”€ Helpers de layout da nova UI â”€â”€
    private void AddDivider(int x, ref int y, int w)
    {
        var d = new Panel { Location = new Point(x, y), Size = new Size(w, 1), BackColor = CherryTheme.Line };
        Controls.Add(d);
        y += 13;
    }

    private void AddSmallLabel(string text, int x, int y)
    {
        var l = new Label
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(260, 16),
            Font = CherryTheme.Label,
            ForeColor = CherryTheme.Dim,
            BackColor = CherryTheme.Panel,
        };
        Controls.Add(l);
    }

    // Linha de toggle: icone + titulo + hint + switch a direita. Devolve o switch.
    private ToggleSwitch AddToggleRow(string icon, string title, string hint, int x, ref int y)
    {
        int w = ClientSize.Width - x * 2;
        var ic = new Label { Location = new Point(x, y + 2), Size = new Size(22, 22), BackColor = CherryTheme.Panel };
        ic.Paint += (s, e) => CherryIcons.Draw(e.Graphics, icon, new Rectangle(0, 0, 21, 21), CherryTheme.PinkDeep);
        Controls.Add(ic);

        var lblTitle = new Label { Text = title, Location = new Point(x + 30, y), Size = new Size(w - 30 - 50, 18), Font = CherryTheme.Head, ForeColor = CherryTheme.Text, BackColor = CherryTheme.Panel };
        Controls.Add(lblTitle);
        var lblHint = new Label { Text = hint, Location = new Point(x + 30, y + 18), Size = new Size(w - 30 - 50, 16), Font = CherryTheme.BodySmall, ForeColor = CherryTheme.Muted, BackColor = CherryTheme.Panel };
        Controls.Add(lblHint);

        var sw = new ToggleSwitch { Location = new Point(x + w - 40, y + 6) };
        Controls.Add(sw);

        y += 40;
        return sw;
    }

    // Header: fundo + logo + "CherrySpy" + "VOZ · LAN".
    private void PaintHeader(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var br = new SolidBrush(CherryTheme.Panel)) g.FillRectangle(br, _header.ClientRectangle);

        // Logo (icone do app) 26px.
        var ic = Icon;
        int logo = 26;
        if (ic != null)
        {
            using var bmp = ic.ToBitmap();
            g.DrawImage(bmp, new Rectangle(16, (HeaderH - logo) / 2, logo, logo));
        }
        int tx = 16 + logo + 10;
        TextRenderer.DrawText(g, "CherrySpy", CherryTheme.HeadBig, new Rectangle(tx, 8, 200, 22),
            CherryTheme.Text, TextFormatFlags.Left);
        TextRenderer.DrawText(g, "VOZ · LAN", CherryTheme.Label, new Rectangle(tx, 30, 200, 16),
            CherryTheme.PinkDeep, TextFormatFlags.Left);

        // Linha inferior sutil.
        using var pen = new Pen(CherryTheme.Line2, 1f);
        g.DrawLine(pen, 0, HeaderH - 1, _header.Width, HeaderH - 1);
    }

    // Combo flat: pinta o item com as cores do tema (bg soft, selecionado rosa claro).
    private void Combo_DrawItem(object? sender, DrawItemEventArgs e)
    {
        var cbo = (ComboBox)sender!;
        e.DrawBackground();
        bool sel = (e.State & DrawItemState.Selected) != 0;
        using (var bg = new SolidBrush(sel ? CherryTheme.PinkGhost : CherryTheme.Soft))
            e.Graphics.FillRectangle(bg, e.Bounds);
        string text = e.Index >= 0 ? cbo.Items[e.Index]?.ToString() ?? "" : "";
        var textRect = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 26, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, text, CherryTheme.Body, textRect, CherryTheme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        // Chevron a direita.
        var chev = new Rectangle(e.Bounds.Right - 20, e.Bounds.Y + (e.Bounds.Height - 14) / 2, 14, 14);
        CherryIcons.Draw(e.Graphics, "chevronDown", chev, CherryTheme.Muted);
    }

    // Arraste da janela sem borda pelo header (equivale a HTCAPTION).
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 0x2;
    private void Header_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        // Nao arrasta se o clique caiu sobre um controle-filho (botoes min/close).
        if (_header.GetChildAtPoint(e.Location) != null) return;
        NativeReleaseCaptureAndDrag();
    }
    private void NativeReleaseCaptureAndDrag()
    {
        ReleaseCapture();
        SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private void ApplyRoundedRegion()
    {
        try { Region = GfxExt.RoundedRegion(ClientSize, 16); } catch { }
    }

    // Borda rosa 1px por cima do painel (borderless).
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var gp = GfxExt.RoundedPath(new RectangleF(0.5f, 0.5f, ClientSize.Width - 1.5f, ClientSize.Height - 1.5f), 15.5f);
        using var pen = new Pen(CherryTheme.Line, 1f);
        e.Graphics.DrawPath(pen, gp);
    }

    private void PopulateDevices()
    {
        // Entradas (microfones) via WaveInEvent (enumeracao estatica disponivel).
        _cboInput.Items.Clear();
        _cboInput.Items.Add(new InputDeviceItem(-1, "Padrao do sistema"));
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            try
            {
                var caps = WaveInEvent.GetCapabilities(i);
                _cboInput.Items.Add(new InputDeviceItem(i, $"{i}: {caps.ProductName}"));
            }
            catch { /* dispositivo indisponivel: ignora */ }
        }
        _cboInput.SelectedIndex = _cboInput.Items.Count > 1 ? 1 : 0;

        // Saidas (fones) via WASAPI â€” WaveOut estatico foi removido no build .NET.
        _cboOutput.Items.Clear();
        _cboOutput.Items.Add(new OutputDeviceItem("", "Padrao do sistema"));
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (MMDevice d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                _cboOutput.Items.Add(new OutputDeviceItem(d.ID, d.FriendlyName));
            }
        }
        catch { /* sem WASAPI: fica so o padrao */ }
        _cboOutput.SelectedIndex = 0;
    }

    private void WireKeyboardForPtt()
    {
        // Push-to-talk: transmite so enquanto a Barra de espaco esta pressionada
        // e a janela esta em foco. KeyPreview=true garante a entrega aqui.
        // LIMITACAO (documentada na label do checkbox): nao ha hotkey global, entao se
        // o usuario alt-tabbar p/ outro app o PTT para de transmitir. Para um intercom
        // de casal em LAN isto e aceitavel na Fase 1 (usar "sempre ligado" + Mudo, ou
        // manter a janela em foco). Um hook global de teclado fica p/ a Fase 2.
        KeyDown += (_, e) =>
        {
            if (_pushToTalk && e.KeyCode == Keys.Space)
            {
                _pttKeyDown = true;
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };
        KeyUp += (_, e) =>
        {
            if (_pushToTalk && e.KeyCode == Keys.Space)
            {
                _pttKeyDown = false;
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };
        // Se a janela perde foco com a tecla apertada, para de transmitir.
        Deactivate += (_, _) => _pttKeyDown = false;
    }

    // --- DESCOBERTA NA REDE ---
    private void ApplyConfigToUi()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_config.PeerIp)) _txtPeerIp.Text = _config.PeerIp;
            _numLocalPort.Value = Math.Clamp(_config.LocalPort, 1, 65535);
            _numRemotePort.Value = Math.Clamp(_config.RemotePort, 1, 65535);
            _chkAutoConnect.Checked = _config.AutoConnect;

            _initializingPhase2 = true;
            _sldVoice.Value = _config.VoiceVolume;
            _sldMusic.Value = _config.MusicVolume;
            _lblVoicePct.Text = _config.VoiceVolume + "%";
            _lblMusicPct.Text = _config.MusicVolume + "%";
            _initializingPhase2 = false;
        }
        catch { /* valores fora de faixa: ignora */ }
    }

    private void SaveConfig()
    {
        try
        {
            _config.PeerIp = _txtPeerIp.Text.Trim();
            _config.LocalPort = (int)_numLocalPort.Value;
            _config.RemotePort = (int)_numRemotePort.Value;
            _config.AutoConnect = _chkAutoConnect.Checked;
            _config.Save();
        }
        catch { }
    }

    private void StartDiscovery()
    {
        try
        {
            ushort voicePort = (ushort)(int)_numLocalPort.Value;
            // A porta de voz e fixada na construcao da descoberta. Trava o controle de
            // porta local p/ o beacon nunca anunciar uma porta diferente da que sera
            // efetivamente bindada no Connect() (evita mismatch silencioso).
            _numLocalPort.Enabled = false;
            _discovery = new DiscoveryService(_instanceId, voicePort, Environment.MachineName, _config.MachineId);
            _discovery.PeerDiscovered += OnPeerDiscovered;
            _discovery.Start();
            Log.Write($"discovery start (instanceId={_instanceId}, voicePort={voicePort}, name={Environment.MachineName})");
        }
        catch (Exception ex)
        {
            Log.Write("discovery start FAILED: " + ex.Message);
        }
    }

    // Vem de uma thread de rede -> marshala p/ a UI thread.
    private void OnPeerDiscovered(IPAddress ip, ushort voicePort, string name, Guid machineId)
    {
        if (!IsHandleCreated || IsDisposed) return;
        try
        {
            BeginInvoke((Action)(() =>
            {
                if (IsDisposed) return;
                Log.Write($"peer discovered ip={ip} voicePort={voicePort} name={name} machineId={machineId:N}");

                // A PROPRIA maquina (2a instancia / loopback) nunca vira par â€” era a
                // rota do "se escutar sozinha". allowlocalpeers=1 libera p/ teste.
                if (ShouldIgnoreBeacon(ip, machineId, name))
                {
                    Log.Write("  skip: beacon da propria maquina");
                    return;
                }
                if (machineId != Guid.Empty && machineId == _config.FriendId)
                    Log.Write("  amigo conhecido: " + _config.FriendName);

                // Canal de controle (chat/ping/arquivo) independe da voz.
                _peerLink?.SetTarget(ip);

                if (!_chkAutoConnect.Checked)
                {
                    Log.Write("  skip: autoconnect desmarcado");
                    return;
                }

                // Anti-hijack: depois de escolher um par, ignora beacons de outros ids
                // (nova sessao do MESMO par tem id novo -> tratada no ramo connected abaixo).
                if (_connecting)
                {
                    Log.Write("  skip: conexao em andamento");
                    return;
                }

                if (_connected)
                {
                    // Conectado mas possivelmente morto: se nao recebemos pacotes ha
                    // tempo, o par voltou (PC dormiu/relancou) -> re-aponta p/ a nova sessao.
                    long ticks = Interlocked.Read(ref _lastPacketTicks);
                    bool gotAny = Interlocked.Read(ref _hasReceivedAny) == 1;
                    double ageSec = gotAny
                        ? (DateTime.UtcNow.Ticks - ticks) / (double)TimeSpan.TicksPerSecond
                        : double.MaxValue;
                    if (ageSec > 6)
                    {
                        Log.Write($"  reconnecting: par voltou (sem pacotes ha {ageSec:0}s) -> {ip}:{voicePort}");
                        _chosenPeerId = Guid.Empty;
                        _ = ReconnectToPeerAsync(ip, voicePort);
                    }
                    else
                    {
                        Log.Write("  skip: conectado e recebendo pacotes");
                    }
                    return;
                }

                // Cooldown apos falha de auto-connect (evita pilha de modais/tentativas).
                long cd = Interlocked.Read(ref _autoConnectCooldownTicks);
                if (cd != 0 && DateTime.UtcNow.Ticks < cd)
                {
                    Log.Write("  skip: em cooldown apos falha de auto-connect");
                    return;
                }

                // Semeia IP + porta remota do beacon e tenta conectar (modo auto, sem modal).
                _chosenPeerId = Guid.Empty;
                _txtPeerIp.Text = ip.ToString();
                try { _numRemotePort.Value = Math.Clamp((int)voicePort, 1, 65535); } catch { }
                Log.Write($"auto-connect attempt -> {ip}:{voicePort} (local {(int)_numLocalPort.Value})");
                TryAutoConnect();
            }));
        }
        catch { /* handle destruido entre o check e o invoke */ }
    }

    // Conexao iniciada pela descoberta: silenciosa (sem MessageBox que a namorada nao
    // consegue fechar) + cooldown ao falhar, p/ retry no proximo beacon.
    private void TryAutoConnect()
    {
        if (_connected || _connecting) return;
        _connecting = true;
        try
        {
            Connect();
            Interlocked.Exchange(ref _autoConnectCooldownTicks, 0);
        }
        catch (Exception ex)
        {
            Log.Write("auto-connect FAILED: " + ex.Message);
            Interlocked.Exchange(ref _autoConnectCooldownTicks,
                DateTime.UtcNow.Ticks + TimeSpan.FromSeconds(6).Ticks);
            _ = TeardownAsync();
        }
        finally
        {
            _connecting = false;
        }
    }

    // Re-aponta p/ uma nova sessao do par (apos detectar conectado-mas-morto).
    private async Task ReconnectToPeerAsync(IPAddress ip, ushort voicePort)
    {
        if (_connecting) return;
        _connecting = true;
        try
        {
            await TeardownAsync();
            _txtPeerIp.Text = ip.ToString();
            try { _numRemotePort.Value = Math.Clamp((int)voicePort, 1, 65535); } catch { }
            Connect();
            Interlocked.Exchange(ref _autoConnectCooldownTicks, 0);
        }
        catch (Exception ex)
        {
            Log.Write("reconnect FAILED: " + ex.Message);
            Interlocked.Exchange(ref _autoConnectCooldownTicks,
                DateTime.UtcNow.Ticks + TimeSpan.FromSeconds(6).Ticks);
            try { await TeardownAsync(); } catch { }
        }
        finally
        {
            _connecting = false;
        }
    }

    // â”€â”€â”€ CONECTAR / DESCONECTAR â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private async void OnConnectClick(object? sender, EventArgs e)
    {
        if (_connected)
        {
            await TeardownAsync();
            return;
        }

        try
        {
            Connect();
        }
        catch (Exception ex)
        {
            Log.Write("connect FAILED (manual): " + ex.Message);
            await TeardownAsync();
            if (!IsDisposed)
                MessageBox.Show(this, "Falha ao conectar: " + ex.Message, "CherrySpy",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void Connect()
    {
        // Auto-escolhe defaults p/ a namorada nunca precisar tocar nos combos:
        // entrada = primeiro microfone real (indice 1, se houver), senao "Padrao";
        // saida = "Padrao do sistema" (indice 0).
        if (_cboInput.SelectedIndex < 0)
            _cboInput.SelectedIndex = _cboInput.Items.Count > 1 ? 1 : 0;
        if (_cboOutput.SelectedIndex < 0)
            _cboOutput.SelectedIndex = 0;
        Log.Write($"connect: input='{_cboInput.SelectedItem}' output='{_cboOutput.SelectedItem}'");

        string ipText = _txtPeerIp.Text.Trim();
        if (!IPAddress.TryParse(ipText, out var peerIp))
            throw new ArgumentException("IP do parceiro invalido.");

        int localPort = (int)_numLocalPort.Value;
        int remotePort = (int)_numRemotePort.Value;
        _remoteEp = new IPEndPoint(peerIp, remotePort);

        // UDP connectionless (sem Connect()) p/ permitir 2 instancias no mesmo PC
        // com portas locais/remotas diferentes (127.0.0.1).
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, localPort));
        DisableUdpConnReset(_udp); // evita WSAECONNRESET(10054) ao enviar p/ peer ausente

        // Jitter buffers (1s, descarta no overflow, preenche silencio no underrun).
        _voiceJitter = new BufferedWaveProvider(Pcm48Mono)
        {
            BufferDuration = TimeSpan.FromSeconds(1),
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };
        _musicJitter = new BufferedWaveProvider(Pcm48Mono)
        {
            BufferDuration = TimeSpan.FromSeconds(1),
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };
        Interlocked.Exchange(ref _voicePrimed, 0);
        Interlocked.Exchange(ref _musicPrimed, 0);

        // Mixer float que nunca termina. Cada entrada passa por um VolumeSampleProvider
        // p/ deixar headroom e evitar clipping quando voz + musica somam alto. A voz fica
        // mais forte que a musica (a musica e "fundo").
        _mixer = new MixingSampleProvider(Float48Mono) { ReadFully = true };
        _voiceVol = new VolumeSampleProvider(_voiceJitter.ToSampleProvider()) { Volume = _config.VoiceVolume / 100f };
        _musicVol = new VolumeSampleProvider(_musicJitter.ToSampleProvider()) { Volume = _config.MusicVolume / 100f };
        _mixer.AddMixerInput(_voiceVol);
        _mixer.AddMixerInput(_musicVol);

        // Saida: WasapiOut no endpoint escolhido; senao WaveOutEvent padrao.
        // O mixer e ISampleProvider (float). IWavePlayer.Init exige IWaveProvider, e o
        // WaveOutEvent normalmente quer PCM16 â€” entao adaptamos com SampleToWaveProvider16,
        // que funciona tanto p/ WaveOut quanto p/ WasapiOut (shared aceita PCM16).
        _output = BuildOutput();
        IWaveProvider outProvider = new SampleToWaveProvider16(_mixer);
        _output.Init(outProvider);
        _output.Play();

        // Microfone.
        StartMic();

        // Loop de recepcao.
        _running = true;
        _rxThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "DuoVoz-RX" };
        _rxThread.Start();

        // Heartbeat a cada 1s.
        _heartbeatTimer = new System.Threading.Timer(_ => SendHeartbeat(), null,
            TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1));

        Interlocked.Exchange(ref _hasReceivedAny, 0);
        Interlocked.Exchange(ref _lastPacketTicks, 0);
        Interlocked.Exchange(ref _packetsLost, 0);
        Interlocked.Exchange(ref _voiceLastSeq, -1);
        Interlocked.Exchange(ref _musicLastSeq, -1);
        _voiceSeq = 0;
        _musicSeq = 0;

        _connected = true;
        _btnConnect.Text = "Desconectar";
        _btnConnect.IconName = "close";
        _btnConnect.FillColor = CherryTheme.Line2;
        _btnConnect.Invalidate();
        _chkShareMusic.Enabled = true;
        SetControlsEnabled(false);
        _lblStatus.Text = "Conectado (aguardando pacotes...)";
        _lblStatus.ForeColor = CherryTheme.PinkDeep;
        _lblStatusDot.Invalidate();
        Log.Write($"connect success local={localPort} remote={peerIp}:{remotePort}");
        SaveConfig();
        OnVoiceConnected(peerIp); // canal de controle TCP 50780 + widget
    }

    private IWavePlayer BuildOutput()
    {
        if (_cboOutput.SelectedItem is OutputDeviceItem o && !string.IsNullOrEmpty(o.Id))
        {
            try
            {
                using var en = new MMDeviceEnumerator();
                var dev = en.GetDevice(o.Id);
                return new WasapiOut(dev, AudioClientShareMode.Shared, true, 50);
            }
            catch
            {
                // cai pro padrao
            }
        }
        return new WaveOutEvent { DesiredLatency = 60, NumberOfBuffers = 3, DeviceNumber = -1 };
    }

    private void StartMic()
    {
        int devNum = _cboInput.SelectedItem is InputDeviceItem im ? im.DeviceNumber : -1;
        _micAcc.Reset();
        _micNeedsConvert = false;
        lock (_micChainLock) { _micSrcBuf = null; _micTo16 = null; }

        // Tenta 48k/mono/16 nativo primeiro (caminho rapido, sem conversao).
        try
        {
            var mic = new WaveInEvent
            {
                WaveFormat = Pcm48Mono,
                BufferMilliseconds = 20,
                NumberOfBuffers = 3,
                DeviceNumber = devNum,
            };
            mic.DataAvailable += OnMicData;
            mic.RecordingStopped += OnMicStopped;
            mic.StartRecording();
            _mic = mic;
            return;
        }
        catch
        {
            // O dispositivo nao aceita 48k mono via MME. Cai p/ um formato comum e
            // normaliza (downmix + resample) p/ 48k mono 16-bit, igual ao loopback.
        }

        // Fallback: 44.1k estereo 16-bit (amplamente aceito via MME) + conversao.
        var fmt = new WaveFormat(44100, 16, 2);
        var micFb = new WaveInEvent
        {
            WaveFormat = fmt,
            BufferMilliseconds = 20,
            NumberOfBuffers = 3,
            DeviceNumber = devNum,
        };

        lock (_micChainLock)
        {
            _micSrcBuf = new BufferedWaveProvider(fmt)
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true,
                ReadFully = false,
            };
            ISampleProvider sp = _micSrcBuf.ToSampleProvider();
            if (sp.WaveFormat.Channels > 1)
                sp = new MultiChannelToMonoSampleProvider(sp);
            if (sp.WaveFormat.SampleRate != 48000)
                sp = new WdlResamplingSampleProvider(sp, 48000);
            _micTo16 = new SampleToWaveProvider16(sp);
            _micNeedsConvert = true;
        }

        micFb.DataAvailable += OnMicData;
        micFb.RecordingStopped += OnMicStopped;
        micFb.StartRecording();
        _mic = micFb;
    }

    private void OnMicStopped(object? sender, StoppedEventArgs a)
    {
        if (a.Exception != null) PostStatus("Microfone parou: " + a.Exception.Message, Color.Firebrick);
    }

    // â”€â”€â”€ COMPARTILHAR MUSICA (WASAPI loopback) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private void OnShareMusicChanged(object? sender, EventArgs e)
    {
        if (!_connected)
        {
            _chkShareMusic.Checked = false;
            return;
        }

        if (_chkShareMusic.Checked)
        {
            try { StartLoopback(); }
            catch (Exception ex)
            {
                _chkShareMusic.Checked = false;
                MessageBox.Show(this, "Falha ao iniciar captura de musica: " + ex.Message,
                    "CherrySpy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        else
        {
            StopLoopback();
        }
    }

    private void StartLoopback()
    {
        _loopback = CreateLoopbackCapture(); // process-loopback (exclui o DuoVoz) c/ fallback legado
        _musicAcc.Reset();

        // Constroi a cadeia de conversao UMA vez, com o formato REAL do loopback
        // (tipicamente IEEE float, estereo, taxa do dispositivo).
        lock (_loopChainLock)
        {
            _loopSrcBuf = new BufferedWaveProvider(_loopback.WaveFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true,
                ReadFully = false,
            };

            ISampleProvider sp = _loopSrcBuf.ToSampleProvider();
            if (sp.WaveFormat.Channels > 1)
                sp = new MultiChannelToMonoSampleProvider(sp);           // downmix N canais -> mono (5.1/7.1 ok)
            if (sp.WaveFormat.SampleRate != 48000)
                sp = new WdlResamplingSampleProvider(sp, 48000);          // resample puro-managed
            _loopTo16 = new SampleToWaveProvider16(sp);                   // -> 16-bit PCM
        }

        _loopback.DataAvailable += OnLoopbackData;
        _loopback.RecordingStopped += OnLoopbackStopped;
        _loopback.StartRecording();
    }

    private void OnLoopbackStopped(object? sender, StoppedEventArgs a)
    {
        if (a.Exception != null) PostStatus("Captura de musica parou: " + a.Exception.Message, Color.Firebrick);
    }

    private void StopLoopback()
    {
        var lb = _loopback;
        _loopback = null;
        if (lb != null)
        {
            // Desliga o handler ANTES de parar p/ que nenhum callback em voo escreva no
            // acumulador depois do Reset.
            lb.DataAvailable -= OnLoopbackData;
            lb.RecordingStopped -= OnLoopbackStopped;
            try { lb.StopRecording(); } catch { }
            try { lb.Dispose(); } catch { }
        }
        lock (_loopChainLock)
        {
            _loopSrcBuf = null;
            _loopTo16 = null;
        }
        _musicAcc.Reset();
    }

    // â”€â”€â”€ CALLBACKS DE CAPTURA â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private void OnMicData(object? sender, WaveInEventArgs a)
    {
        try
        {
            bool transmit = !_muted && (!_pushToTalk || _pttKeyDown);

            // VU coerente com o que e enviado -> zera quando nao transmite.
            if (!transmit) { _micPeak = 0; return; }

            if (!_micNeedsConvert)
            {
                // Caminho nativo: buffer ja e 48k mono 16-bit.
                _micPeak = ComputePeak16(a.Buffer, a.BytesRecorded);
                _micAcc.Append(a.Buffer, 0, a.BytesRecorded);
            }
            else
            {
                // Caminho com conversao: alimenta a cadeia e drena PCM16 48k mono.
                BufferedWaveProvider? src;
                SampleToWaveProvider16? to16;
                lock (_micChainLock) { src = _micSrcBuf; to16 = _micTo16; }
                if (src == null || to16 == null) return;

                src.AddSamples(a.Buffer, 0, a.BytesRecorded);

                byte[] tmp = new byte[FrameBytes];
                int read;
                int peak = 0;
                // Drena ate Read retornar 0 (vazio de verdade). Short read != EOF em
                // cadeias com resample, entao NAO quebramos no primeiro short read.
                while ((read = to16.Read(tmp, 0, tmp.Length)) > 0)
                {
                    int p = ComputePeak16(tmp, read);
                    if (p > peak) peak = p;
                    _micAcc.Append(tmp, 0, read);
                }
                _micPeak = peak;
            }

            FlushVoiceFramesAndSend(); // = FlushFramesAndSend + supressao de ruido por frame de 10ms
        }
        catch (Exception ex)
        {
            PostStatus("Erro no microfone: " + ex.Message, Color.Firebrick);
        }
    }

    private void OnLoopbackData(object? sender, WaveInEventArgs a)
    {
        try
        {
            BufferedWaveProvider? src;
            SampleToWaveProvider16? to16;
            lock (_loopChainLock)
            {
                src = _loopSrcBuf;
                to16 = _loopTo16;
            }
            if (src == null || to16 == null) return;

            // Alimenta o buffer fonte com o audio bruto do sistema.
            src.AddSamples(a.Buffer, 0, a.BytesRecorded);

            // Drena a cadeia (downmix + resample + 16-bit) ate Read retornar 0.
            // IMPORTANTE: short read NAO e fim de dados em cadeias com resample â€”
            // por isso lemos ate 0 de verdade, acumulando tudo que vier.
            byte[] tmp = new byte[FrameBytes];
            int read;
            while ((read = to16.Read(tmp, 0, tmp.Length)) > 0)
                _musicAcc.Append(tmp, 0, read);

            FlushFramesAndSend(_musicAcc, _musicPacket, StreamMusic, ref _musicSeq);
        }
        catch (Exception ex)
        {
            PostStatus("Erro na musica: " + ex.Message, Color.Firebrick);
        }
    }

    /// <summary>Envia todos os frames completos de 960 bytes acumulados em <paramref name="acc"/>.</summary>
    private void FlushFramesAndSend(FrameAccumulator acc, byte[] packet, byte streamId, ref uint seq)
    {
        var udp = _udp;
        var ep = _remoteEp;
        if (udp == null || ep == null) return;

        while (acc.TryDequeueFrame(packet, HeaderBytes, FrameBytes))
        {
            packet[0] = streamId;
            // sequence little-endian
            packet[1] = (byte)(seq & 0xFF);
            packet[2] = (byte)((seq >> 8) & 0xFF);
            packet[3] = (byte)((seq >> 16) & 0xFF);
            packet[4] = (byte)((seq >> 24) & 0xFF);
            seq++;

            try { udp.Send(packet, packet.Length, ep); }
            catch (SocketException) { /* peer ausente: ignora */ }
            catch (ObjectDisposedException) { return; }
        }
    }

    private void SendHeartbeat()
    {
        var udp = _udp;
        var ep = _remoteEp;
        if (udp == null || ep == null) return;
        try
        {
            byte[] hb = new byte[HeaderBytes];
            hb[0] = StreamHeartbeat; // seq fica 0 â€” irrelevante p/ heartbeat
            udp.Send(hb, hb.Length, ep);
        }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        catch { }
    }

    // â”€â”€â”€ LOOP DE RECEPCAO (thread dedicada) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private void ReceiveLoop()
    {
        var remoteEp = new IPEndPoint(IPAddress.Any, 0);
        while (_running)
        {
            try
            {
                var udp = _udp;
                if (udp == null) break;

                byte[] data = udp.Receive(ref remoteEp);
                if (data.Length < HeaderBytes) continue;

                Interlocked.Exchange(ref _lastPacketTicks, DateTime.UtcNow.Ticks);
                Interlocked.Exchange(ref _hasReceivedAny, 1);

                byte streamId = data[0];
                uint seq = (uint)(data[1] | (data[2] << 8) | (data[3] << 16) | (data[4] << 24));
                int payloadLen = data.Length - HeaderBytes;

                switch (streamId)
                {
                    case StreamVoice:
                        TrackLoss(ref _voiceLastSeq, seq);
                        PrimeJitter(_voiceJitter, ref _voicePrimed);
                        _voiceJitter?.AddSamples(data, HeaderBytes, payloadLen);
                        _voicePeak = ComputePeak16(data, HeaderBytes, payloadLen);
                        break;

                    case StreamMusic:
                        TrackLoss(ref _musicLastSeq, seq);
                        PrimeJitter(_musicJitter, ref _musicPrimed);
                        _musicJitter?.AddSamples(data, HeaderBytes, payloadLen);
                        break;

                    case StreamHeartbeat:
                        // so atualiza presenca (ja feito acima)
                        break;
                }
            }
            catch (ObjectDisposedException)
            {
                break; // socket fechado no teardown
            }
            catch (SocketException)
            {
                // Erro transiente (ex.: 10054, 10040). Pequeno backoff p/ nao girar a
                // CPU em 100% caso o socket entre num estado de erro persistente.
                if (!_running) break;
                Thread.Sleep(2);
            }
            catch (Exception ex)
            {
                PostStatus("Erro de rede: " + ex.Message, Color.Firebrick);
            }
        }
    }

    /// <summary>
    /// Pre-enche o jitter buffer com ~JitterPrimeMs de silencio na PRIMEIRA amostra
    /// recebida, p/ dar folga contra jitter/perda e estabilizar a latencia de playout
    /// (evita underrun/click imediato com o buffer vazio + ReadFully).
    /// </summary>
    private static void PrimeJitter(BufferedWaveProvider? jitter, ref long primedFlag)
    {
        if (jitter == null) return;
        if (Interlocked.CompareExchange(ref primedFlag, 1, 0) != 0) return;
        int silenceBytes = jitter.WaveFormat.AverageBytesPerSecond * JitterPrimeMs / 1000;
        silenceBytes -= silenceBytes % jitter.WaveFormat.BlockAlign; // alinha ao frame
        if (silenceBytes <= 0) return;
        byte[] silence = new byte[silenceBytes];
        try { jitter.AddSamples(silence, 0, silence.Length); } catch { }
    }

    private void TrackLoss(ref long lastSeqField, uint seq)
    {
        long last = Interlocked.Read(ref lastSeqField);
        if (last >= 0)
        {
            long gap = (long)seq - last - 1;
            if (gap > 0) Interlocked.Add(ref _packetsLost, gap);
        }
        Interlocked.Exchange(ref lastSeqField, seq);
    }

    // â”€â”€â”€ TIMER DA UI: status + VU â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private void OnUiTick(object? sender, EventArgs e)
    {
        // VU meters (amostrados aqui, NUNCA por frame de audio).
        SafeSetBar(_vuMicOut, _micPeak);
        SafeSetBar(_vuPeerIn, _voicePeak);

        // Decai suavemente os picos p/ o medidor "respirar".
        _micPeak = Math.Max(0, _micPeak - 12);
        _voicePeak = Math.Max(0, _voicePeak - 12);

        if (!_connected)
        {
            _lblStatus.Text = "Desconectado";
            _lblStatus.ForeColor = Color.DimGray;
            _lblDiag.Text = "";
            return;
        }

        bool got = Interlocked.Read(ref _hasReceivedAny) == 1;
        if (!got)
        {
            _lblStatus.Text = "Conectado (aguardando pacotes...)";
            _lblStatus.ForeColor = Color.DarkOrange;
        }
        else
        {
            long ticks = Interlocked.Read(ref _lastPacketTicks);
            double ageSec = (DateTime.UtcNow.Ticks - ticks) / (double)TimeSpan.TicksPerSecond;
            if (ageSec < 2)
            {
                _lblStatus.Text = "Conectado";
                _lblStatus.ForeColor = Color.ForestGreen;
            }
            else if (ageSec < 10)
            {
                _lblStatus.Text = $"Sem pacotes ha {ageSec:0}s";
                _lblStatus.ForeColor = Color.DarkOrange;
            }
            else
            {
                _lblStatus.Text = $"Sem pacotes ha {ageSec:0}s (parceiro offline?)";
                _lblStatus.ForeColor = Color.Firebrick;
            }
        }

        // Diagnostico jitter/perda.
        int vBuf = _voiceJitter != null ? (int)_voiceJitter.BufferedDuration.TotalMilliseconds : 0;
        int mBuf = _musicJitter != null ? (int)_musicJitter.BufferedDuration.TotalMilliseconds : 0;
        long lost = Interlocked.Read(ref _packetsLost);
        _lblDiag.Text = $"jitter voz={vBuf}ms musica={mBuf}ms  perdidos={lost}";
    }

    // â”€â”€â”€ MARSHALLING SEGURO P/ UI â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private void PostStatus(string text, Color color)
    {
        if (!IsHandleCreated || IsDisposed) return;
        try
        {
            BeginInvoke(() =>
            {
                if (IsDisposed) return;
                _lblStatus.Text = text;
                _lblStatus.ForeColor = color;
            });
        }
        catch { /* handle destruido entre o check e o invoke */ }
    }

    private void SafeSetBar(ProgressBar bar, int value)
    {
        // Ja estamos na UI thread (chamado pela Forms.Timer), mas guardamos mesmo assim.
        int v = Math.Clamp(value, 0, 100);
        if (bar.IsDisposed) return;
        if (bar.Value != v) bar.Value = v;
    }

    private void SetControlsEnabled(bool enabled)
    {
        _cboInput.Enabled = enabled;
        _cboOutput.Enabled = enabled;
        _txtPeerIp.Enabled = enabled;
        _numLocalPort.Enabled = enabled;
        _numRemotePort.Enabled = enabled;
    }

    // â”€â”€â”€ PICO P/ VU METER â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private static int ComputePeak16(byte[] buffer, int count) => ComputePeak16(buffer, 0, count);

    private static int ComputePeak16(byte[] buffer, int offset, int count)
    {
        int max = 0;
        int end = offset + count;
        if (end > buffer.Length) end = buffer.Length;
        for (int i = offset; i + 1 < end; i += 2)
        {
            short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
            int abs = sample == short.MinValue ? short.MaxValue : Math.Abs(sample);
            if (abs > max) max = abs;
        }
        return (int)(max / 32768.0 * 100.0);
    }

    // â”€â”€â”€ FORM CLOSING (teardown off-thread p/ evitar self-deadlock) â”€
    private async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // Segundo Close() (apos o teardown concluir): deixa fechar de verdade.
        if (_readyToClose) return;

        // Fechar pelo X / Alt+F4 apenas esconde na bandeja. Sair de verdade so pelo
        // menu "Sair" (_forceQuit) ou shutdown/logoff do Windows (reason != UserClosing).
        if (!_forceQuit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        // Primeiro Close(): cancela, para o timer, faz o teardown OFF da UI thread e
        // so entao fecha. Isso evita o self-deadlock de StopRecording()/Stop()
        // marshalando o evento de parada de volta p/ a UI thread bloqueada no Stop.
        e.Cancel = true;
        if (_closing) return;
        _closing = true;

        try { _uiTimer.Stop(); } catch { }
        try { SaveConfig(); } catch { }
        var disc = _discovery; _discovery = null;
        if (disc != null)
        {
            disc.PeerDiscovered -= OnPeerDiscovered;
            try { disc.Dispose(); } catch { }
            Log.Write("discovery stopped (form closing)");
        }
        ShutdownPhase2(); // widget, chat, canal de controle, transferencias
        await TeardownAsync();

        try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; } } catch { }

        _readyToClose = true;
        Close();
    }

    // â”€â”€â”€ TEARDOWN (idempotente; Desconectar e FormClosing) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Roda o trabalho bloqueante (StopRecording/Stop/Dispose) FORA da UI thread p/
    // evitar self-deadlock: WaveInEvent/WaveOutEvent capturam o SynchronizationContext
    // da UI no construtor e marshalam o evento de parada de volta â€” se a UI thread
    // estiver bloqueada dentro de Stop(), trava. Detachamos os handlers antes e
    // executamos as paradas em Task.Run.
    private async Task TeardownAsync()
    {
        bool wasConnected = _connected;
        Log.Write(wasConnected ? "teardown (disconnect)" : "teardown (no active connection)");
        // Limpa o debounce da descoberta p/ o proximo beacon do par re-disparar
        // PeerDiscovered (caso contrario auto-reconexao nunca acontece apos desconectar).
        if (wasConnected)
        {
            try { _discovery?.ForgetPeers(); Log.Write("discovery debounce limpo no disconnect"); } catch { }
        }
        _running = false;

        // Captura referencias e desliga handlers AINDA na UI thread; depois zera campos.
        var mic = _mic; _mic = null;
        if (mic != null)
        {
            mic.DataAvailable -= OnMicData;
            mic.RecordingStopped -= OnMicStopped;
        }
        var loopback = _loopback; _loopback = null;
        if (loopback != null)
        {
            loopback.DataAvailable -= OnLoopbackData;
            loopback.RecordingStopped -= OnLoopbackStopped;
        }
        var output = _output; _output = null;
        var heartbeat = _heartbeatTimer; _heartbeatTimer = null;
        var udp = _udp; _udp = null;
        var rxThread = _rxThread; _rxThread = null;

        await Task.Run(() =>
        {
            // 1) Para captura de mic (espera a parada antes de limpar acumuladores).
            if (mic != null)
            {
                try { mic.StopRecording(); } catch { }
                try { mic.Dispose(); } catch { }
            }
            // 2) Para loopback.
            if (loopback != null)
            {
                try { loopback.StopRecording(); } catch { }
                try { loopback.Dispose(); } catch { }
            }

            // 3) Drena o heartbeat ANTES de fechar o socket (Dispose com WaitHandle
            //    espera o callback em voo terminar -> sem use-after-dispose no Send).
            if (heartbeat != null)
            {
                try
                {
                    using var wh = new ManualResetEvent(false);
                    if (heartbeat.Dispose(wh)) wh.WaitOne(500);
                }
                catch { try { heartbeat.Dispose(); } catch { } }
            }

            // 4) Fecha o socket -> desbloqueia a rx thread (ObjectDisposedException).
            try { udp?.Close(); } catch { }
            try { udp?.Dispose(); } catch { }

            // 5) Junta a rx thread. Close() normalmente desbloqueia o Receive na hora;
            //    damos folga (2s) e seguimos mesmo se nao juntar (thread e background).
            try
            {
                if (rxThread != null && rxThread.IsAlive)
                    rxThread.Join(2000);
            }
            catch { }

            // 6) Para a saida (sem PlaybackStopped inscrito, mas Stop ainda marshala).
            if (output != null)
            {
                try { output.Stop(); } catch { }
                try { output.Dispose(); } catch { }
            }
        }).ConfigureAwait(true);

        // De volta na UI thread: limpa estado de audio/cadeias/acumuladores. As capturas
        // ja estao paradas e os handlers desligados, entao nenhum callback escreve mais.
        try { _mixer?.RemoveAllMixerInputs(); } catch { }
        _mixer = null;
        _voiceVol = null;
        _musicVol = null;
        _voiceJitter = null;
        _musicJitter = null;

        lock (_loopChainLock) { _loopSrcBuf = null; _loopTo16 = null; }
        lock (_micChainLock) { _micSrcBuf = null; _micTo16 = null; }
        _micNeedsConvert = false;

        _micAcc.Reset();
        _musicAcc.Reset();
        _remoteEp = null;

        if (_connected)
        {
            _connected = false;
            if (IsHandleCreated && !IsDisposed)
            {
                try
                {
                    _btnConnect.Text = "Conectar";
                    _btnConnect.IconName = "phone";
                    _btnConnect.FillColor = CherryTheme.Pink;
                    _btnConnect.Invalidate();
                    _chkShareMusic.Checked = false;
                    _chkShareMusic.Enabled = false;
                    SetControlsEnabled(true);
                    _lblStatus.Text = "Desconectado";
                    _lblStatus.ForeColor = CherryTheme.Muted;
                    _lblStatusDot.Invalidate();
                }
                catch { }
            }
        }
    }

    // â”€â”€â”€ SIO_UDP_CONNRESET: desliga o WSAECONNRESET(10054) no socket UDP â”€
    private static void DisableUdpConnReset(UdpClient udp)
    {
        try
        {
            const int SIO_UDP_CONNRESET = -1744830452; // 0x9800000C
            udp.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
        }
        catch
        {
            // nao critico: em alguns ambientes o ioctl nao esta disponivel
        }
    }
}
