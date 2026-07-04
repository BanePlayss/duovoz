using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DuoVoz;

/// <summary>
/// Canal de controle TCP (porta 50780): frames [u32 LE length][JSON UTF-8].
/// Tipos: hello, chatText, ping, media, fileOffer, fileAccept, fileReject, fileCancel, bye.
/// Ambos os lados escutam; quem tem alvo disca (retry ~10s enquanto desconectado â€”
/// funciona mesmo sem broadcast, ex.: amigo salvo em outra subrede/tailnet futura).
///
/// Dedup de discagem simultanea (ORDEM-INDEPENDENTE e SIMETRICO): o vencedor e uma
/// funcao pura dos DOIS machineIds, sem depender de quem discou nem da ordem de
/// chegada. O par com id MENOR fica com a conexao QUE ELE DISCOU (outbound); o par
/// com id MAIOR fica com a conexao QUE ELE RECEBEU (inbound). Os dois lados calculam
/// isso identicamente -> sobrevive exatamente a MESMA conexao fisica nos dois lados.
///
/// Eventos disparam em threads de fundo â€” a UI marshala com BeginInvoke.
/// </summary>
public sealed class PeerLink : IDisposable
{
    public const int Port = 50780;
    private const int MaxFrame = 1 << 20; // sanidade anti-OOM em stream corrompido

    private readonly Guid _myId;
    private readonly string _myName;
    private readonly bool _allowLocalDial; // teste local: permite discar p/ a propria maquina
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private TcpListener? _listener;
    private Link? _link;
    private IPAddress? _target;

    public Guid PeerId { get; private set; }
    public string PeerName { get; private set; } = "";
    public bool IsConnected { get { lock (_gate) return _link != null && _link.HelloOk; } }

    public IPAddress? PeerAddress
    {
        get
        {
            Link? l; lock (_gate) l = _link;
            try { return (l?.Client.Client.RemoteEndPoint as IPEndPoint)?.Address; }
            catch { return null; }
        }
    }

    public event Action<Guid, string>? PeerHello;                // (machineId, nome)
    public event Action? LinkDown;
    public event Action<string, DateTime>? ChatReceived;
    public event Action? PingReceived;
    public event Action<MediaAction>? MediaReceived;             // controle remoto de musica
    public event Action<Guid, string, long>? FileOfferReceived;  // (tid, nome, tamanho)
    public event Action<Guid>? FileAcceptReceived;
    public event Action<Guid, string>? FileRejectReceived;       // (tid, motivo)
    public event Action<Guid>? FileCancelReceived;

    private sealed class Link
    {
        public TcpClient Client = null!;
        public NetworkStream Stream = null!;
        public readonly SemaphoreSlim WriteLock = new(1, 1); // UM escritor por vez
        public bool Outbound;
        public bool HelloOk;
        public Guid PeerId;
        public string PeerName = "";
    }

    public PeerLink(Guid myId, string myName, bool allowLocalDial)
    {
        _myId = myId;
        _myName = myName ?? "";
        _allowLocalDial = allowLocalDial;
    }

    public void Start()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Start();
            _ = AcceptLoopAsync();
            Log.Write($"peerlink: escutando TCP {Port} (id={_myId:N})");
        }
        catch (Exception ex)
        {
            // Porta ocupada (2a instancia em teste local): segue so discando.
            _listener = null;
            Log.Write("peerlink: listener falhou (segue dial-only): " + ex.Message);
        }
        _ = DialLoopAsync();
    }

    /// <summary>Define/atualiza o alvo de discagem (descoberta, conexao de voz ou amigo salvo).</summary>
    public void SetTarget(IPAddress ip)
    {
        lock (_gate) _target = ip;
    }

    // ----- envio -----
    public Task<bool> SendChatAsync(string text) => SendAsync(new { t = "chatText", text });
    public Task<bool> SendPingAsync() => SendAsync(new { t = "ping" });
    public Task<bool> SendMediaAsync(MediaAction a) => SendAsync(new { t = "media", action = MediaStr(a) });
    public Task<bool> SendFileOfferAsync(Guid tid, string name, long size)
        => SendAsync(new { t = "fileOffer", tid = tid.ToString("N"), name, size });
    public Task<bool> SendFileAcceptAsync(Guid tid) => SendAsync(new { t = "fileAccept", tid = tid.ToString("N") });
    public Task<bool> SendFileRejectAsync(Guid tid, string reason)
        => SendAsync(new { t = "fileReject", tid = tid.ToString("N"), reason });
    public Task<bool> SendFileCancelAsync(Guid tid) => SendAsync(new { t = "fileCancel", tid = tid.ToString("N") });

    private async Task<bool> SendAsync(object msg)
    {
        Link? link; lock (_gate) link = _link;
        if (link == null || !link.HelloOk) return false;
        try { await WriteMsgAsync(link, msg, _cts.Token); return true; }
        catch (Exception ex) { Log.Write("peerlink: envio falhou: " + ex.Message); return false; }
    }

    // ----- loops -----
    private async Task AcceptLoopAsync()
    {
        var l = _listener!;
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync(_cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (Exception ex)
            {
                if (_cts.IsCancellationRequested) return;
                Log.Write("peerlink: accept erro: " + ex.Message);
                try { await Task.Delay(500, _cts.Token); } catch { return; }
                continue;
            }
            _ = RunLinkAsync(c, outbound: false);
        }
    }

    private async Task DialLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            IPAddress? tgt; bool connected;
            lock (_gate) { tgt = _target; connected = _link != null; }
            if (tgt != null && !connected && (_allowLocalDial || !AppEnv.IsOwnAddress(tgt)))
            {
                var c = new TcpClient();
                try
                {
                    using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    connectCts.CancelAfter(4000);
                    await c.ConnectAsync(tgt, Port, connectCts.Token);
                    _ = RunLinkAsync(c, outbound: true);
                }
                catch (Exception ex)
                {
                    try { c.Dispose(); } catch { }
                    if (!_cts.IsCancellationRequested)
                        Log.Write($"peerlink: dial {tgt}:{Port} falhou: {ex.Message}");
                }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(10), _cts.Token); } catch { return; }
        }
    }

    private async Task RunLinkAsync(TcpClient client, bool outbound)
    {
        var link = new Link { Client = client, Outbound = outbound };
        try
        {
            client.NoDelay = true;
            link.Stream = client.GetStream();

            await WriteMsgAsync(link, new { t = "hello", v = 1, id = _myId.ToString("N"), name = _myName }, _cts.Token);

            using var helloCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            helloCts.CancelAfter(5000); // sem hello em 5s => drop
            using (var doc = await ReadMsgAsync(link.Stream, helloCts.Token))
            {
                var r = doc.RootElement;
                if ((r.GetProperty("t").GetString() ?? "") != "hello")
                    throw new InvalidDataException("primeiro frame nao e hello");
                link.PeerId = Guid.ParseExact(r.GetProperty("id").GetString() ?? "", "N");
                link.PeerName = r.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
            }

            if (link.PeerId == _myId)
            {
                Log.Write("peerlink: conexao consigo mesmo, fechando");
                try { client.Close(); } catch { }
                return;
            }

            // Dedup ORDEM-INDEPENDENTE: o "papel de discador" e uma funcao pura dos dois
            // ids. iShouldDial == true no par com id MENOR. Fica valida a conexao cujo
            // sentido (outbound) bate com esse papel; caso contrario fica a inbound. Como
            // ambos os lados calculam o MESMO iShouldDial, os dois convergem p/ a MESMA
            // conexao fisica -> nunca derrubam as duas nem ficam com conexoes diferentes.
            bool iShouldDial = string.CompareOrdinal(_myId.ToString("N"), link.PeerId.ToString("N")) < 0;
            bool keepThis = link.Outbound == iShouldDial;

            Link? loser = null;
            lock (_gate)
            {
                if (_link == null || !_link.HelloOk)
                {
                    if (keepThis) { link.HelloOk = true; _link = link; }
                    else loser = link; // a conexao "certa" ainda vai chegar; descarta esta
                }
                else if (_link.PeerId == link.PeerId)
                {
                    // Ja temos um link com o mesmo par. keepThis decide de forma simetrica
                    // qual sobrevive; se este e o "certo" e o antigo e o "errado", troca.
                    if (keepThis && _link.Outbound != iShouldDial)
                    {
                        loser = _link; link.HelloOk = true; _link = link;
                    }
                    else loser = link; // o link atual ja e o correto (ou este e o incorreto)
                }
                else loser = link; // ja pareado com OUTRO par: rejeita o novo
            }
            if (loser != null)
            {
                _ = SendByeAndCloseAsync(loser, "duplicate");
                if (ReferenceEquals(loser, link)) return;
            }

            PeerId = link.PeerId;
            PeerName = link.PeerName;
            Log.Write($"peerlink: conectado a {link.PeerName} ({link.PeerId:N}) outbound={outbound}");
            try { PeerHello?.Invoke(link.PeerId, link.PeerName); }
            catch (Exception ex) { Log.Write("peerlink: PeerHello handler: " + ex.Message); }

            await ReceiveLoopAsync(link);
        }
        catch (Exception ex)
        {
            if (!_cts.IsCancellationRequested) Log.Write("peerlink: link caiu: " + ex.Message);
        }
        finally
        {
            bool wasActive;
            lock (_gate)
            {
                wasActive = ReferenceEquals(_link, link);
                if (wasActive) _link = null;
            }
            try { client.Close(); } catch { }
            if (wasActive && !_cts.IsCancellationRequested)
            {
                try { LinkDown?.Invoke(); } catch { }
            }
        }
    }

    private async Task ReceiveLoopAsync(Link link)
    {
        while (!_cts.IsCancellationRequested)
        {
            using var doc = await ReadMsgAsync(link.Stream, _cts.Token); // EndOfStream = par fechou
            var r = doc.RootElement;
            string t = r.TryGetProperty("t", out var tp) ? tp.GetString() ?? "" : "";
            try
            {
                switch (t)
                {
                    case "chatText":
                        ChatReceived?.Invoke(r.GetProperty("text").GetString() ?? "", DateTime.Now);
                        break;
                    case "ping":
                        PingReceived?.Invoke();
                        break;
                    case "media":
                        MediaReceived?.Invoke(ParseMedia(
                            r.TryGetProperty("action", out var mp) ? mp.GetString() ?? "" : ""));
                        break;
                    case "fileOffer":
                        FileOfferReceived?.Invoke(Tid(r), r.GetProperty("name").GetString() ?? "arquivo",
                            r.GetProperty("size").GetInt64());
                        break;
                    case "fileAccept": FileAcceptReceived?.Invoke(Tid(r)); break;
                    case "fileReject":
                        FileRejectReceived?.Invoke(Tid(r),
                            r.TryGetProperty("reason", out var rr) ? rr.GetString() ?? "" : "");
                        break;
                    case "fileCancel": FileCancelReceived?.Invoke(Tid(r)); break;
                    case "bye":
                        Log.Write("peerlink: bye do par");
                        return;
                    case "hello": break; // hello tardio: ignora
                    default:
                        Log.Write("peerlink: tipo desconhecido ignorado: " + t); // forward-compat
                        break;
                }
            }
            catch (Exception ex)
            {
                // Handler NUNCA derruba o loop de recepcao.
                Log.Write($"peerlink: handler de '{t}' falhou: {ex.Message}");
            }
        }
    }

    private static Guid Tid(JsonElement r) => Guid.ParseExact(r.GetProperty("tid").GetString() ?? "", "N");

    private static string MediaStr(MediaAction a) => a switch
    {
        MediaAction.Next => "next",
        MediaAction.Prev => "prev",
        _ => "playpause",
    };

    private static MediaAction ParseMedia(string s) => s switch
    {
        "next" => MediaAction.Next,
        "prev" => MediaAction.Prev,
        _ => MediaAction.PlayPause,
    };

    private static async Task WriteMsgAsync(Link link, object msg, CancellationToken ct)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(msg);
        byte[] head = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(head, body.Length);
        await link.WriteLock.WaitAsync(ct);
        try
        {
            await link.Stream.WriteAsync(head, ct);
            await link.Stream.WriteAsync(body, ct);
        }
        finally { link.WriteLock.Release(); }
    }

    private static async Task<JsonDocument> ReadMsgAsync(NetworkStream ns, CancellationToken ct)
    {
        byte[] head = new byte[4];
        await ns.ReadExactlyAsync(head, ct); // sem read parcial
        int len = BinaryPrimitives.ReadInt32LittleEndian(head);
        if (len <= 0 || len > MaxFrame) throw new InvalidDataException("frame invalido: " + len);
        byte[] body = new byte[len];
        await ns.ReadExactlyAsync(body, ct);
        return JsonDocument.Parse(body);
    }

    private async Task SendByeAndCloseAsync(Link link, string reason)
    {
        // So tenta o "bye" se ainda nao estamos em teardown (evita ruido 'envio falhou'
        // depois do Dispose(), quando o _cts ja foi cancelado).
        if (!_cts.IsCancellationRequested)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                cts.CancelAfter(1000);
                await WriteMsgAsync(link, new { t = "bye", reason }, cts.Token);
            }
            catch { /* par pode ja ter fechado: silencioso */ }
        }
        Log.Write("peerlink: fechando link (" + reason + ")");
        try { link.Client.Close(); } catch { }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        Link? l;
        lock (_gate) { l = _link; _link = null; }
        if (l != null) { try { l.Client.Close(); } catch { } }
        // _cts nao e disposed de proposito: loops em drenagem ainda leem o token.
    }
}
