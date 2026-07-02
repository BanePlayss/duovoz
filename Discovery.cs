using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace DuoVoz;

/// <summary>Logger best-effort em %TEMP%\DuoVoz\duovoz.log. Nunca lanca.</summary>
public static class Log
{
    private static readonly object _gate = new();
    private static readonly string _path = BuildPath();

    private static string BuildPath()
    {
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "DuoVoz");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "duovoz.log");
        }
        catch { return string.Empty; }
    }

    public static void Write(string line)
    {
        if (string.IsNullOrEmpty(_path)) return;
        try
        {
            string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            lock (_gate)
                File.AppendAllText(_path, $"[{stamp}] [pid {Environment.ProcessId}] {line}{Environment.NewLine}");
        }
        catch { /* best-effort */ }
    }
}

/// <summary>Persistencia simples key=value em config.txt ao lado do exe. Nunca lanca.</summary>
public sealed class Config
{
    public string PeerIp = "127.0.0.1";
    public int LocalPort = 50777;
    public int RemotePort = 50777;
    public bool AutoConnect = true; // novo campo (default true)

    // Fase 2
    public Guid MachineId = Guid.Empty;   // identidade persistente desta maquina
    public Guid FriendId = Guid.Empty;    // amigo salvo (machineId do par)
    public string FriendName = "";
    public string FriendLastIp = "";
    public int FriendPort = 50777;
    public bool NoiseSuppress = true;
    public bool ShowWidget = true;
    public int WidgetX = -1;
    public int WidgetY = -1;
    public bool AllowLocalPeers = false;  // 1 = permite 2 instancias locais (teste)

    // Em %APPDATA%\DuoVoz: a pasta do exe muda a cada update do Velopack e o
    // config ao lado do exe era PERDIDO na atualizacao.
    private static string PathFile()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DuoVoz");
        try { Directory.CreateDirectory(dir); } catch { }
        return Path.Combine(dir, "config.txt");
    }

    private static string OldPathFile()
        => Path.Combine(AppContext.BaseDirectory, "config.txt");

    /// <summary>
    /// Gera o MachineId persistente se ainda nao existir e persiste UMA vez. Dono unico:
    /// chamado so pelo MainForm (nao pelo bootConfig antes do mutex), p/ evitar que uma
    /// 2a instancia bloqueada â€” ou um Load com config parcial â€” reescreva o arquivo e
    /// perca o registro do amigo. Idempotente: no-op se ja houver id.
    /// </summary>
    public void EnsureMachineId()
    {
        if (MachineId != Guid.Empty) return;
        MachineId = Guid.NewGuid();
        Save();
    }

    public static Config Load()
    {
        var c = new Config();
        try
        {
            string p = PathFile();
            // Migracao unica: config antigo ao lado do exe -> %APPDATA%\DuoVoz.
            if (!File.Exists(p))
            {
                string old = OldPathFile();
                if (File.Exists(old))
                {
                    try { File.Copy(old, p); Log.Write("config migrado p/ AppData"); } catch { }
                }
            }
            if (!File.Exists(p)) return c;
            foreach (string raw in File.ReadAllLines(p))
            {
                string s = raw.Trim();
                if (s.Length == 0 || s.StartsWith('#')) continue;
                int eq = s.IndexOf('=');
                if (eq <= 0) continue;
                string k = s.Substring(0, eq).Trim().ToLowerInvariant();
                string v = s.Substring(eq + 1).Trim();
                switch (k)
                {
                    case "peerip": c.PeerIp = v; break;
                    case "localport": if (int.TryParse(v, out var lp)) c.LocalPort = lp; break;
                    case "remoteport": if (int.TryParse(v, out var rp)) c.RemotePort = rp; break;
                    case "autoconnect": c.AutoConnect = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                    case "machineid": if (Guid.TryParse(v, out var mid)) c.MachineId = mid; break;
                    case "friendid": if (Guid.TryParse(v, out var fid)) c.FriendId = fid; break;
                    case "friendname": c.FriendName = v; break;
                    case "friendlastip": c.FriendLastIp = v; break;
                    case "friendport": if (int.TryParse(v, out var fpt)) c.FriendPort = fpt; break;
                    case "noisesuppress": c.NoiseSuppress = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                    case "showwidget": c.ShowWidget = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                    case "widgetx": if (int.TryParse(v, out var wx)) c.WidgetX = wx; break;
                    case "widgety": if (int.TryParse(v, out var wy)) c.WidgetY = wy; break;
                    case "allowlocalpeers": c.AllowLocalPeers = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                    // chaves desconhecidas (formato antigo): ignoradas -> backward-compat
                }
            }
        }
        catch (Exception ex) { Log.Write("Config.Load falhou: " + ex.Message); }
        // NAO gera/persiste MachineId aqui: Load nunca escreve (senao um Load com config
        // parcial ou uma 2a instancia bloqueada poderia reescrever e perder o amigo).
        // A geracao unica fica em EnsureMachineId(), chamada so pelo MainForm.
        return c;
    }

    public void Save()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("# DuoVoz config");
            sb.AppendLine($"peerip={PeerIp}");
            sb.AppendLine($"localport={LocalPort}");
            sb.AppendLine($"remoteport={RemotePort}");
            sb.AppendLine($"autoconnect={(AutoConnect ? "1" : "0")}");
            sb.AppendLine($"machineid={MachineId}");
            sb.AppendLine($"friendid={FriendId}");
            sb.AppendLine($"friendname={FriendName}");
            sb.AppendLine($"friendlastip={FriendLastIp}");
            sb.AppendLine($"friendport={FriendPort}");
            sb.AppendLine($"noisesuppress={(NoiseSuppress ? "1" : "0")}");
            sb.AppendLine($"showwidget={(ShowWidget ? "1" : "0")}");
            sb.AppendLine($"widgetx={WidgetX}");
            sb.AppendLine($"widgety={WidgetY}");
            sb.AppendLine($"allowlocalpeers={(AllowLocalPeers ? "1" : "0")}");
            // Escrita atomica: grava num .tmp e troca (File.Replace/Move) p/ nunca
            // truncar o config real se o processo morrer no meio da escrita.
            string path = PathFile();
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString());
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
        catch (Exception ex) { Log.Write("Config.Save falhou: " + ex.Message); }
    }
}

/// <summary>
/// Descoberta de par na LAN via UDP broadcast (porta 50779), robusta a multiplos NICs
/// (VirtualBox) e a duas instancias no mesmo PC. Thread-safe. Start/Stop/Dispose limpos.
///
/// Beacon payload (little-endian):
///   magic "DUOVOZ1" (7 bytes ASCII)
///   instanceId       (16 bytes, Guid)
///   voicePort        (2 bytes ushort LE) -- porta de voz que esta instancia escuta
///   displayName      (UTF-8, resto do datagrama)
///
/// NOTA sobre duas instancias no MESMO PC: a entrega para AMBAS as instancias e
/// garantida pelo broadcast limitado (255.255.255.255), que o Windows entrega a TODOS
/// os listeners ligados em 0.0.0.0:50779 com SO_REUSEADDR. O envio extra p/ 127.0.0.1 e
/// redundancia inofensiva (so chega a UMA das instancias) e nao e o que carrega o caso
/// de duas instancias -- mantido apenas como belt-and-suspenders.
/// </summary>
public sealed class DiscoveryService : IDisposable
{
    public const int DiscoveryPort = 50779;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("DUOVOZ1");  // 7 bytes (v1, sem machineId)
    private static readonly byte[] Magic2 = Encoding.ASCII.GetBytes("DUOVOZ2"); // 7 bytes (v2: + machineId antes do nome)

    // Re-emite PeerDiscovered se o mesmo (ip,port) nao for visto ha mais de este TTL,
    // mesmo sem mudanca de ip/porta -- recuperacao extra alem do ForgetPeers().
    private static readonly TimeSpan RaiseTtl = TimeSpan.FromSeconds(12);

    private readonly Guid _instanceId;
    private readonly ushort _voicePort;
    private readonly byte[] _nameBytes;
    private readonly Guid _machineId; // identidade PERSISTENTE da maquina (config)

    private Socket? _listen;     // bound 0.0.0.0:50779, recebe beacons
    private Socket? _send;       // socket de envio com broadcast habilitado
    private Thread? _rxThread;
    private System.Threading.Timer? _beaconTimer;
    private volatile bool _running;
    private long _beaconsSent;   // diagnostico (Interlocked)
    private readonly object _gate = new();

    // Debounce: ultimo (ip,port) visto por instanceId remoto.
    private readonly Dictionary<Guid, (IPAddress ip, ushort port, DateTime last)> _seen = new();

    /// <summary>Disparado quando um beacon de par chega (1a vez, ip/porta muda, ou apos TTL).
    /// O Guid final e o machineId persistente do remoto (Guid.Empty em beacon v1 antigo).</summary>
    public event Action<IPAddress, ushort, string, Guid>? PeerDiscovered;

    public DiscoveryService(Guid instanceId, ushort voicePort, string displayName, Guid machineId)
    {
        _instanceId = instanceId;
        _voicePort = voicePort;
        _nameBytes = Encoding.UTF8.GetBytes(displayName ?? string.Empty);
        _machineId = machineId;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;

            // Listener: REUSEADDR + ExclusiveAddressUse=false ANTES do Bind (duas
            // instancias no mesmo PC podem ambas ouvir; bind nunca falha por porta ocupada).
            _listen = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _listen.ExclusiveAddressUse = false;
            _listen.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listen.EnableBroadcast = true;
            DisableUdpConnReset(_listen);
            _listen.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));

            // Socket de envio (broadcast habilitado; porta efemera).
            _send = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _send.EnableBroadcast = true;
            DisableUdpConnReset(_send);

            _running = true;
            _rxThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "DuoVoz-Discovery-RX" };
            _rxThread.Start();

            // Beacon a cada ~2s, primeiro quase imediato.
            _beaconTimer = new System.Threading.Timer(_ => SafeBeacon(), null,
                TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>
    /// Limpa o cache de debounce -> o proximo beacon de qualquer par re-dispara
    /// PeerDiscovered. Chamado no teardown/disconnect p/ permitir auto-reconexao.
    /// </summary>
    public void ForgetPeers()
    {
        lock (_gate) { _seen.Clear(); }
    }

    public long BeaconsSent => Interlocked.Read(ref _beaconsSent);

    private void SafeBeacon()
    {
        try { SendBeacon(); } catch { /* nunca deixa escapar do timer */ }
    }

    private void SendBeacon()
    {
        var snd = _send;
        if (snd == null || !_running) return;

        // Payload v2: magic2(7) + instanceId(16) + voicePort(2 LE) + machineId(16) + utf8 name.
        // (machineId em campo FIXO antes do nome: o nome e "resto do datagrama" no parse.)
        byte[] payload = new byte[Magic2.Length + 16 + 2 + 16 + _nameBytes.Length];
        int o = 0;
        Buffer.BlockCopy(Magic2, 0, payload, o, Magic2.Length); o += Magic2.Length;
        Buffer.BlockCopy(_instanceId.ToByteArray(), 0, payload, o, 16); o += 16;
        payload[o++] = (byte)(_voicePort & 0xFF);
        payload[o++] = (byte)((_voicePort >> 8) & 0xFF);
        Buffer.BlockCopy(_machineId.ToByteArray(), 0, payload, o, 16); o += 16;
        Buffer.BlockCopy(_nameBytes, 0, payload, o, _nameBytes.Length);

        // 1) loopback: redundancia (so chega a UMA instancia local; o caso de duas
        //    instancias no mesmo PC e coberto pelo broadcast limitado abaixo).
        TrySend(snd, payload, new IPEndPoint(IPAddress.Loopback, DiscoveryPort));
        // 2) limited broadcast (cobre mascaras estranhas E entrega a TODAS as instancias
        //    locais ligadas com REUSEADDR -> garante o caso de duas instancias num PC).
        TrySend(snd, payload, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));
        // 3) broadcast DIRIGIDO por interface (corrige o bug do VirtualBox: cada
        //    endereco dirigido sai pela NIC certa em vez de uma so escolhida pelo OS).
        foreach (IPAddress b in DirectedBroadcasts())
            TrySend(snd, payload, new IPEndPoint(b, DiscoveryPort));

        Interlocked.Increment(ref _beaconsSent);
    }

    private static void TrySend(Socket s, byte[] data, IPEndPoint ep)
    {
        try { s.SendTo(data, ep); }
        catch (SocketException) { /* interface sem rota / broadcast negado: ignora */ }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Calcula o broadcast dirigido (addr | ~mask) de cada IPv4 unicast Up.</summary>
    private static IEnumerable<IPAddress> DirectedBroadcasts()
    {
        var result = new List<IPAddress>();
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
        catch { return result; }

        foreach (var nic in nics)
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); } catch { continue; }

            foreach (UnicastIPAddressInformation u in props.UnicastAddresses)
            {
                if (u.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                byte[] addr = u.Address.GetAddressBytes();
                if (addr.Length != 4) continue;
                if (addr[0] == 169 && addr[1] == 254) continue; // APIPA/link-local
                if (addr[0] == 127) continue;                   // loopback (ja enviado)

                IPAddress maskAddr = u.IPv4Mask;
                if (maskAddr == null) continue;
                byte[] mask = maskAddr.GetAddressBytes();
                if (mask.Length != 4) continue;
                if (mask[0] == 0 && mask[1] == 0 && mask[2] == 0 && mask[3] == 0) continue; // sem mascara util

                byte[] bc = new byte[4];
                for (int i = 0; i < 4; i++) bc[i] = (byte)(addr[i] | (~mask[i] & 0xFF));
                result.Add(new IPAddress(bc));
            }
        }
        return result;
    }

    private void ReceiveLoop()
    {
        var buf = new byte[2048];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        while (_running)
        {
            var sock = _listen;
            if (sock == null) break;
            int n;
            try
            {
                n = sock.ReceiveFrom(buf, ref from);
            }
            catch (ObjectDisposedException) { break; }
            catch (SocketException)
            {
                if (!_running) break;
                Thread.Sleep(2); // evita spin em erro transiente (ex.: 10054)
                continue;
            }
            catch { if (!_running) break; Thread.Sleep(2); continue; }

            try { HandleBeacon(buf, n, ((IPEndPoint)from).Address); }
            catch { /* pacote malformado: ignora */ }
        }
    }

    private void HandleBeacon(byte[] buf, int n, IPAddress srcIp)
    {
        int min1 = Magic.Length + 16 + 2;
        if (n < min1) return;
        bool v2 = true;
        for (int i = 0; i < Magic2.Length; i++)
            if (buf[i] != Magic2[i]) { v2 = false; break; }
        if (!v2)
            for (int i = 0; i < Magic.Length; i++)
                if (buf[i] != Magic[i]) return;
        int min = v2 ? min1 + 16 : min1;
        if (n < min) return;

        int o = Magic.Length;
        var idBytes = new byte[16];
        Buffer.BlockCopy(buf, o, idBytes, 0, 16); o += 16;
        var remoteId = new Guid(idBytes);
        if (remoteId == _instanceId) return; // self-filter (por processo)

        ushort voicePort = (ushort)(buf[o] | (buf[o + 1] << 8)); o += 2;
        Guid remoteMachineId = Guid.Empty;
        if (v2)
        {
            Buffer.BlockCopy(buf, o, idBytes, 0, 16); o += 16;
            remoteMachineId = new Guid(idBytes);
        }
        string name = n > min ? Encoding.UTF8.GetString(buf, o, n - min) : string.Empty;

        bool raise;
        lock (_gate)
        {
            if (_seen.TryGetValue(remoteId, out var prev)
                && prev.ip.Equals(srcIp) && prev.port == voicePort
                && (DateTime.UtcNow - prev.last) < RaiseTtl)
            {
                _seen[remoteId] = (srcIp, voicePort, DateTime.UtcNow);
                raise = false; // debounce: mesmo ip/porta visto recentemente -> nao redispara
            }
            else
            {
                _seen[remoteId] = (srcIp, voicePort, DateTime.UtcNow);
                raise = true;  // 1a vez, ip/porta mudou, ou TTL expirou
            }
        }
        if (raise) PeerDiscovered?.Invoke(srcIp, voicePort, name, remoteMachineId);
    }

    public void Stop()
    {
        Thread? rx;
        Socket? listen, send;
        System.Threading.Timer? timer;
        lock (_gate)
        {
            if (!_running && _listen == null) return;
            _running = false;
            rx = _rxThread; _rxThread = null;
            listen = _listen; _listen = null;
            send = _send; _send = null;
            timer = _beaconTimer; _beaconTimer = null;
        }

        // Drena o beacon em voo antes de fechar o socket de envio.
        if (timer != null)
        {
            try { using var wh = new ManualResetEvent(false); if (timer.Dispose(wh)) wh.WaitOne(300); }
            catch { try { timer.Dispose(); } catch { } }
        }
        try { listen?.Close(); } catch { } // desbloqueia ReceiveFrom
        try { listen?.Dispose(); } catch { }
        try { send?.Close(); } catch { }
        try { send?.Dispose(); } catch { }
        try { if (rx != null && rx.IsAlive) rx.Join(1000); } catch { }
    }

    public void Dispose() => Stop();

    private static void DisableUdpConnReset(Socket s)
    {
        try
        {
            const int SIO_UDP_CONNRESET = -1744830452; // 0x9800000C
            s.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
        }
        catch { /* nao critico */ }
    }
}
