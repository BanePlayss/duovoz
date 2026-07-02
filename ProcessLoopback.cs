using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wasapi.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace DuoVoz;

/// <summary>
/// WASAPI process loopback (Win10 19041+): captura o audio do sistema EXCLUINDO a
/// arvore de processos alvo. Com ExcludingSelf() o "compartilhar musica" NUNCA
/// recaptura a voz do parceiro tocada pelo proprio DuoVoz (mata o eco por construcao).
/// Drop-in de WasapiLoopbackCapture via IWaveIn. Reusa os tipos publicos do NAudio;
/// o unico interop novo e ActivateAudioInterfaceAsync com PROPVARIANT(VT_BLOB).
/// </summary>
public sealed class ProcessLoopbackCapture : IWaveIn
{
    public enum Mode { IncludeProcessTree = 0, ExcludeProcessTree = 1 } // PROCESS_LOOPBACK_MODE

    private const string VirtualDevice = "VAD\\Process_Loopback"; // VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK
    private const int ActivationTypeProcessLoopback = 1;          // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
    private const ushort VtBlob = 65;                             // VARENUM VT_BLOB
    private static readonly TimeSpan ActivateTimeout = TimeSpan.FromSeconds(5);

    private readonly uint _targetPid;
    private readonly Mode _mode;
    private AudioClient? _client;
    private AudioCaptureClient? _capture;
    private EventWaitHandle? _frameEvent;
    private Thread? _thread;
    private volatile bool _stop;

    /// <summary>Formato PEDIDO ao engine (GetMixFormat nao existe no device virtual;
    /// o engine converte pro que pedirmos). Setar ANTES de Prepare/StartRecording.</summary>
    public WaveFormat WaveFormat { get; set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    public ProcessLoopbackCapture(uint targetProcessId, Mode mode)
    {
        _targetPid = targetProcessId;
        _mode = mode;
    }

    /// <summary>Fix do eco: tudo do sistema MENOS o playback do proprio DuoVoz.</summary>
    public static ProcessLoopbackCapture ExcludingSelf()
        => new((uint)Environment.ProcessId, Mode.ExcludeProcessTree);

    /// <summary>Ativa e inicializa ja (lanca aqui se indisponivel -> chamador faz fallback).</summary>
    public void Prepare()
    {
        if (_client != null) return;
        if (!IsSupported) throw new PlatformNotSupportedException("requer Windows 10 build 19041+");
        _client = Activate(_targetPid, _mode);
        // 2_000_000 = 200 ms em unidades de 100 ns; flags LOOPBACK|EVENTCALLBACK.
        _client.Initialize(AudioClientShareMode.Shared,
            AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback,
            2_000_000, 0, WaveFormat, Guid.Empty);
        _frameEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
        _client.SetEventHandle(_frameEvent.SafeWaitHandle.DangerousGetHandle());
        _capture = _client.AudioCaptureClient; // GetService: SO depois do Initialize
    }

    public void StartRecording()
    {
        if (_thread != null) throw new InvalidOperationException("captura ja iniciada");
        Prepare();
        _client!.Start();
        _stop = false;
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "DuoVoz-ProcLoopback" };
        _thread.Start();
    }

    public void StopRecording()
    {
        _stop = true;
        try { _frameEvent?.Set(); } catch { }
        var t = _thread; _thread = null;
        if (t != null && t.IsAlive && t != Thread.CurrentThread) t.Join(1000);
        try { _client?.Stop(); } catch { }
    }

    public void Dispose()
    {
        StopRecording();
        try { _capture?.Dispose(); } catch { } _capture = null;
        try { _client?.Dispose(); } catch { } _client = null;
        try { _frameEvent?.Dispose(); } catch { } _frameEvent = null;
    }

    private void CaptureLoop()
    {
        Exception? err = null;
        try
        {
            var cap = _capture!;
            int blockAlign = WaveFormat.BlockAlign;
            byte[] buf = new byte[Math.Max(WaveFormat.AverageBytesPerSecond / 4, 1 << 16)];
            while (!_stop)
            {
                // TIMEOUT obrigatorio: sistema em silencio => NENHUM evento/pacote
                // (process loopback nao gera pacotes de silencio).
                _frameEvent!.WaitOne(100);
                if (_stop) break;
                while (!_stop && cap.GetNextPacketSize() > 0)
                {
                    IntPtr p = cap.GetBuffer(out int frames, out AudioClientBufferFlags flags);
                    int bytes = frames * blockAlign;
                    if (buf.Length < bytes) buf = new byte[bytes];
                    if ((flags & AudioClientBufferFlags.Silent) != 0) Array.Clear(buf, 0, bytes);
                    else Marshal.Copy(p, buf, 0, bytes);
                    cap.ReleaseBuffer(frames);
                    // Contrato identico ao WasapiLoopbackCapture: buffer REUTILIZADO,
                    // o handler copia sincronamente (OnLoopbackData ja copia).
                    DataAvailable?.Invoke(this, new WaveInEventArgs(buf, bytes));
                }
            }
        }
        catch (Exception ex) { err = ex; }
        try { RecordingStopped?.Invoke(this, new StoppedEventArgs(err)); } catch { }
    }

    // ----- ativacao (unico interop novo) -----
    private static AudioClient Activate(uint pid, Mode mode)
    {
        var pars = new ActivationParams
        {
            ActivationType = ActivationTypeProcessLoopback,
            TargetProcessId = pid,
            ProcessLoopbackMode = (int)mode,
        };
        int size = Marshal.SizeOf<ActivationParams>();
        IntPtr pPars = Marshal.AllocHGlobal(size);
        Marshal.StructureToPtr(pars, pPars, false);
        var pv = new PropVariantBlob { vt = VtBlob, blobSize = (uint)size, blobData = pPars };
        var handler = new Handler(); // agil (IAgileObject): callback chega em MTA worker
        Guid iid = typeof(IAudioClient).GUID; // IID_IAudioClient via tipo publico do NAudio
        int hr = ActivateAudioInterfaceAsync(VirtualDevice, ref iid, ref pv, handler, out var op);
        if (hr < 0)
        {
            Marshal.FreeHGlobal(pPars);
            Marshal.ThrowExceptionForHR(hr);
        }
        bool done = handler.Completion.Wait(ActivateTimeout);
        GC.KeepAlive(op); // op+handler vivos ate o callback (senao GC mata o CCW)
        if (!done)
        {
            // NAO liberar o blob agora (ativacao ainda em voo -> use-after-free). Quando o
            // callback finalmente completar, liberamos o blob E, se ele produziu uma
            // interface tardia, soltamos o COM object p/ nao vazar handle nativo.
            handler.Completion.ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion && t.Result is object late)
                {
                    try { Marshal.FinalReleaseComObject(late); } catch { }
                }
                Marshal.FreeHGlobal(pPars);
            }, TaskScheduler.Default);
            throw new TimeoutException("ActivateAudioInterfaceAsync nao completou");
        }
        try
        {
            object itf = handler.Completion.GetAwaiter().GetResult(); // relanca erro do callback
            return new AudioClient((IAudioClient)itf); // ctor publico do NAudio 2.3.0
        }
        finally
        {
            Marshal.FreeHGlobal(pPars); // completou (sucesso ou erro): blob ja nao esta em uso
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParams // AUDIOCLIENT_ACTIVATION_PARAMS (union achatada)
    {
        public int ActivationType;
        public uint TargetProcessId;     // AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob // braco BLOB do PROPVARIANT; layout ok em x86 e x64
    {
        public ushort vt; public ushort r1, r2, r3;
        public uint blobSize; public IntPtr blobData;
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        ref PropVariantBlob activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler, // interface PUBLICA do NAudio
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }

    private sealed class Handler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly TaskCompletionSource<object> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<object> Completion => _tcs.Task;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
        {
            try
            {
                op.GetActivateResult(out int hr, out object itf);
                if (hr < 0) _tcs.TrySetException(
                    Marshal.GetExceptionForHR(hr) ?? new COMException("GetActivateResult", hr));
                else _tcs.TrySetResult(itf);
            }
            catch (Exception ex) { _tcs.TrySetException(ex); }
        }
    }
}
