using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace DuoVoz;

/// <summary>
/// Transferencia de arquivos grandes (5 GB+) via TCP 50781. Oferta/aceite/cancel
/// correm no canal de controle (PeerLink); os dados correm crus aqui:
/// preamble (magic "DVFT1" + transferId) + bytes do arquivo em chunks de 1 MiB.
/// TUDO long-safe (nada de int em tamanho/offset/acumulador). Uma por vez.
/// Receptor escuta 50781 ANTES de mandar o accept; emissor conecta e despeja.
/// SHA-256 incremental dos dois lados vai pro log (verificacao pelo orquestrador).
///
/// Seguranca LAN: o receptor SO aceita a conexao de dados do MESMO IP do par de
/// controle (o transferId sozinho nao autentica â€” trafega em claro). Conexoes de
/// outros hosts sao rejeitadas e o accept segue esperando ate o timeout.
/// </summary>
public sealed class FileTransferService : IDisposable
{
    public const int DataPort = 50781;
    private const int Chunk = 1 << 20; // 1 MiB
    private static readonly byte[] Magic = { (byte)'D', (byte)'V', (byte)'F', (byte)'T', (byte)'1' };

    private readonly PeerLink _link;
    private readonly object _gate = new();
    private Active? _active;

    private sealed class Active
    {
        public Guid Tid;
        public string Name = "";
        public long Size;
        public string Path = ""; // origem (envio) ou destino (recepcao)
        public bool Sending;
        public readonly CancellationTokenSource Cts = new();
        public TcpListener? Listener;
    }

    public event Action<Guid, string, long>? OfferReceived;   // UI pergunta aceitar/recusar
    public event Action<string, int>? Progress;               // (texto, 0..100)
    public event Action<string, bool, string>? Completed;     // (mensagem, ok, caminhoSalvo|"")

    public FileTransferService(PeerLink link)
    {
        _link = link;
        link.FileOfferReceived += OnOffer;
        link.FileAcceptReceived += OnAccept;
        link.FileRejectReceived += OnReject;
        link.FileCancelReceived += OnCancelMsg;
    }

    public bool Busy { get { lock (_gate) return _active != null; } }

    // ----- lado EMISSOR -----
    public async void OfferFile(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) { Completed?.Invoke("Arquivo nao encontrado: " + path, false, ""); return; }
            if (!_link.IsConnected)
            {
                Completed?.Invoke("Sem conexao com o par. Conecte a voz/chat antes de enviar.", false, "");
                return;
            }
            var a = new Active { Tid = Guid.NewGuid(), Name = fi.Name, Size = fi.Length, Path = path, Sending = true };
            lock (_gate)
            {
                if (_active != null)
                {
                    Completed?.Invoke("Ja existe uma transferencia em andamento. Aguarde ela terminar.", false, "");
                    return;
                }
                _active = a;
            }
            Log.Write($"ft: oferta {a.Name} ({a.Size} bytes) tid={a.Tid:N}");
            Progress?.Invoke($"Oferecendo {a.Name} ({AppEnv.FormatBytes(a.Size)})... aguardando aceite", 0);
            if (!await _link.SendFileOfferAsync(a.Tid, a.Name, a.Size))
                Finish(a, "Sem conexao com o par para oferecer o arquivo.", false, "");
        }
        catch (Exception ex) { Log.Write("ft: OfferFile erro: " + ex.Message); }
    }

    private void OnAccept(Guid tid)
    {
        Active? a; lock (_gate) a = _active;
        if (a == null || a.Tid != tid || !a.Sending) return;
        _ = SendAsync(a);
    }

    private async Task SendAsync(Active a)
    {
        var sw = Stopwatch.StartNew(); long lastMs = -1000, lastBytes = 0;
        try
        {
            var ip = _link.PeerAddress ?? throw new InvalidOperationException("sem endereco do par");
            using var client = new TcpClient();
            client.SendBufferSize = Chunk;
            using (var connCts = CancellationTokenSource.CreateLinkedTokenSource(a.Cts.Token))
            {
                connCts.CancelAfter(15000);
                await client.ConnectAsync(ip, DataPort, connCts.Token);
            }
            var ns = client.GetStream();

            byte[] pre = new byte[Magic.Length + 16];
            Buffer.BlockCopy(Magic, 0, pre, 0, Magic.Length);
            Buffer.BlockCopy(a.Tid.ToByteArray(), 0, pre, Magic.Length, 16);
            await ns.WriteAsync(pre, a.Cts.Token);

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var fs = new FileStream(a.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                Chunk, FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] buf = new byte[Chunk];
            long sent = 0; // long: >4 GB seguro
            int read;
            while ((read = await fs.ReadAsync(buf.AsMemory(0, Chunk), a.Cts.Token)) > 0)
            {
                await ns.WriteAsync(buf.AsMemory(0, read), a.Cts.Token);
                sha.AppendData(buf, 0, read);
                sent += read;
                Throttle(sw, ref lastMs, ref lastBytes, sent, a.Size, "Enviando " + a.Name);
            }
            string hash = Convert.ToHexString(sha.GetHashAndReset());
            Log.Write($"ft: envio ok {a.Name} bytes={sent} sha256={hash}");
            Finish(a, $"Arquivo enviado: {a.Name} ({AppEnv.FormatBytes(sent)})", true, "");
        }
        catch (OperationCanceledException) { Finish(a, "Envio cancelado.", false, ""); }
        catch (Exception ex)
        {
            Log.Write("ft: envio falhou: " + ex.Message);
            Finish(a, "Falha no envio: " + ex.Message, false, "");
        }
    }

    // ----- lado RECEPTOR -----
    private void OnOffer(Guid tid, string name, long size)
    {
        lock (_gate)
        {
            if (_active != null)
            {
                _ = _link.SendFileRejectAsync(tid, "Ja existe uma transferencia em andamento");
                return;
            }
        }
        OfferReceived?.Invoke(tid, name, size);
    }

    public void RejectOffer(Guid tid) => _ = _link.SendFileRejectAsync(tid, "recusado");

    /// <summary>Chamado pela UI quando o usuario aceita a oferta.</summary>
    public async void AcceptOffer(Guid tid, string name, long size)
    {
        var a = new Active { Tid = tid, Name = name, Size = size, Sending = false };
        lock (_gate)
        {
            if (_active != null) { _ = _link.SendFileRejectAsync(tid, "ocupado"); return; }
            _active = a;
        }
        // IP esperado da conexao de dados = IP do par no canal de controle.
        IPAddress? expectedIp = _link.PeerAddress;
        var sw = Stopwatch.StartNew(); long lastMs = -1000, lastBytes = 0;
        try
        {
            string dest = UniquePath(Path.Combine(AppEnv.DownloadsDir, SanitizeName(name)));
            a.Path = dest;
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(dest) ?? "C:\\");
                if (drive.AvailableFreeSpace < size + (100L << 20))
                    throw new IOException("espaco em disco insuficiente");
            }
            catch (IOException) { throw; }
            catch { /* DriveInfo pode falhar em caminho incomum: segue */ }

            // Escuta ANTES de mandar o accept (o emissor conecta imediatamente).
            a.Listener = new TcpListener(IPAddress.Any, DataPort);
            a.Listener.Start(4);
            if (!await _link.SendFileAcceptAsync(tid)) throw new IOException("sem conexao de controle");

            // Aceita conexoes ate achar uma do IP esperado (rejeita spoof de outro host
            // na LAN). Fecha as intrusas e continua ate o timeout global.
            TcpClient client;
            using (var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(a.Cts.Token))
            {
                acceptCts.CancelAfter(20000);
                while (true)
                {
                    var candidate = await a.Listener.AcceptTcpClientAsync(acceptCts.Token);
                    var rip = (candidate.Client.RemoteEndPoint as IPEndPoint)?.Address;
                    if (expectedIp != null && rip != null && rip.Equals(expectedIp))
                    {
                        client = candidate;
                        break;
                    }
                    Log.Write($"ft: conexao de dados de host inesperado ({rip}) rejeitada; esperado {expectedIp}");
                    try { candidate.Close(); } catch { }
                }
            }
            using var _c = client;
            client.ReceiveBufferSize = Chunk;
            var ns = client.GetStream();

            byte[] pre = new byte[Magic.Length + 16];
            await ns.ReadExactlyAsync(pre, a.Cts.Token);
            for (int i = 0; i < Magic.Length; i++)
                if (pre[i] != Magic[i]) throw new InvalidDataException("preamble invalido");
            byte[] tidB = new byte[16];
            Buffer.BlockCopy(pre, Magic.Length, tidB, 0, 16);
            if (new Guid(tidB) != tid) throw new InvalidDataException("transferId nao confere");

            string part = dest + ".duovozpart";
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long got = 0;
            await using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None,
                Chunk, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                byte[] buf = new byte[Chunk];
                long remaining = size; // long!
                while (remaining > 0)
                {
                    int want = (int)Math.Min(Chunk, remaining); // NUNCA ler alem do total
                    int n = await ns.ReadAsync(buf.AsMemory(0, want), a.Cts.Token);
                    if (n == 0) throw new EndOfStreamException("conexao de dados caiu");
                    await fs.WriteAsync(buf.AsMemory(0, n), a.Cts.Token);
                    sha.AppendData(buf, 0, n);
                    remaining -= n; got += n;
                    Throttle(sw, ref lastMs, ref lastBytes, got, size, "Recebendo " + name);
                }
            }
            if (new FileInfo(part).Length != size) throw new InvalidDataException("tamanho final nao confere");
            File.Move(part, dest, overwrite: false);
            string hash = Convert.ToHexString(sha.GetHashAndReset());
            Log.Write($"ft: recepcao ok {name} bytes={got} sha256={hash} -> {dest}");
            Finish(a, "Arquivo recebido: " + dest, true, dest);
        }
        catch (OperationCanceledException)
        {
            TryDeletePart(a);
            Finish(a, "Recebimento cancelado.", false, "");
        }
        catch (Exception ex)
        {
            Log.Write("ft: recepcao falhou: " + ex.Message);
            TryDeletePart(a);
            Finish(a, "Falha ao receber: " + ex.Message, false, "");
        }
    }

    // ----- comum -----
    public void Cancel()
    {
        Active? a; lock (_gate) a = _active;
        if (a == null) return;
        _ = _link.SendFileCancelAsync(a.Tid);
        try { a.Cts.Cancel(); } catch { }
        try { a.Listener?.Stop(); } catch { }
    }

    private void OnCancelMsg(Guid tid)
    {
        Active? a; lock (_gate) a = _active;
        if (a == null || a.Tid != tid) return;
        Log.Write("ft: cancelado pelo par");
        try { a.Cts.Cancel(); } catch { }
        try { a.Listener?.Stop(); } catch { }
    }

    private void OnReject(Guid tid, string reason)
    {
        Active? a; lock (_gate) a = _active;
        if (a == null || a.Tid != tid || !a.Sending) return;
        Finish(a, "O par recusou o arquivo" + (reason.Length > 0 ? $" ({reason})" : "") + ".", false, "");
    }

    private void Finish(Active a, string msg, bool ok, string savedPath)
    {
        lock (_gate) { if (ReferenceEquals(_active, a)) _active = null; }
        try { a.Listener?.Stop(); } catch { }
        try { Completed?.Invoke(msg, ok, savedPath); } catch { }
    }

    private void Throttle(Stopwatch sw, ref long lastMs, ref long lastBytes, long done, long total, string verb)
    {
        long ms = sw.ElapsedMilliseconds;
        if (ms - lastMs < 100 && done != total) return; // >=100ms entre eventos (UI nao afoga)
        double dt = Math.Max(1, ms - lastMs) / 1000.0;
        double mbs = (done - lastBytes) / dt / (1024.0 * 1024.0);
        lastMs = ms; lastBytes = done;
        int pct = total > 0 ? (int)(done * 100L / total) : 100;
        double etaSec = mbs > 0.01 ? (total - done) / (mbs * 1024 * 1024) : 0;
        string eta = $"{(int)(etaSec / 60)}:{(int)(etaSec % 60):00}";
        try { Progress?.Invoke($"{verb}: {pct}% ({AppEnv.FormatBytes(done)}/{AppEnv.FormatBytes(total)}) {mbs:0.0} MB/s ETA {eta}", pct); }
        catch { }
    }

    private static void TryDeletePart(Active a)
    {
        try
        {
            string p = a.Path + ".duovozpart";
            if (a.Path.Length > 0 && File.Exists(p)) File.Delete(p);
        }
        catch { }
    }

    private static string SanitizeName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "arquivo" : name;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!;
        string stem = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            string p = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }

    public void Dispose()
    {
        _link.FileOfferReceived -= OnOffer;
        _link.FileAcceptReceived -= OnAccept;
        _link.FileRejectReceived -= OnReject;
        _link.FileCancelReceived -= OnCancelMsg;
        Active? a;
        lock (_gate) { a = _active; _active = null; }
        if (a != null)
        {
            try { a.Cts.Cancel(); } catch { }
            try { a.Listener?.Stop(); } catch { }
        }
    }
}
