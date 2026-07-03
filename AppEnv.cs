using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NAudio.Wave;

namespace DuoVoz;

/// <summary>Helpers compartilhados: pastas, icone, som de atencao, utilidades de rede.</summary>
public static class AppEnv
{
    /// <summary>%APPDATA%\DuoVoz (sobrevive a updates do Velopack).</summary>
    public static string DataDir
    {
        get
        {
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DuoVoz");
            try { Directory.CreateDirectory(d); } catch { }
            return d;
        }
    }

    /// <summary>%USERPROFILE%\Downloads\DuoVoz (destino de arquivos recebidos).</summary>
    public static string DownloadsDir
    {
        get
        {
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "DuoVoz");
            try { Directory.CreateDirectory(d); } catch { }
            return d;
        }
    }

    /// <summary>Carrega CherrySpy.ico ao lado do exe; null se nao existir (dev run).</summary>
    public static Icon? LoadAppIcon()
    {
        try
        {
            string p = Path.Combine(AppContext.BaseDirectory, "CherrySpy.ico");
            if (File.Exists(p)) return new Icon(p);
        }
        catch (Exception ex) { Log.Write("icone nao carregou: " + ex.Message); }
        return null;
    }

    /// <summary>true se o IP e loopback ou pertence a uma NIC desta maquina.</summary>
    public static bool IsOwnAddress(IPAddress ip)
    {
        try
        {
            if (IPAddress.IsLoopback(ip)) return true;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                IPInterfaceProperties props;
                try { props = nic.GetIPProperties(); } catch { continue; }
                foreach (var u in props.UnicastAddresses)
                    if (u.Address.AddressFamily == AddressFamily.InterNetwork && u.Address.Equals(ip))
                        return true;
            }
        }
        catch { }
        return false;
    }

    public static string FormatBytes(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 || v >= 100 ? $"{v:0} {u[i]}" : $"{v:0.0} {u[i]}";
    }

    /// <summary>Toca um sinal de atencao de 2 tons, gerado em memoria (sem arquivo externo).</summary>
    public static void PlayChime()
    {
        try
        {
            const int rate = 44100;
            short[] pcm = new short[(int)(rate * 0.34)];
            FillTone(pcm, 0, (int)(rate * 0.15), 880.0, rate);            // A5
            FillTone(pcm, (int)(rate * 0.16), pcm.Length, 1174.66, rate); // D6
            byte[] bytes = new byte[pcm.Length * 2];
            Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
            var ms = new MemoryStream(bytes);
            var src = new RawSourceWaveStream(ms, new WaveFormat(rate, 16, 1));
            var dev = new WaveOutEvent();
            dev.PlaybackStopped += (_, _) =>
            {
                try { dev.Dispose(); } catch { }
                try { src.Dispose(); } catch { }
            };
            dev.Init(src);
            dev.Play();
        }
        catch (Exception ex) { Log.Write("chime falhou: " + ex.Message); }
    }

    private static void FillTone(short[] buf, int start, int end, double freq, int rate)
    {
        int len = end - start;
        for (int i = 0; i < len; i++)
        {
            // Envelope com ataque/decaimento curtos p/ nao dar clique.
            double env = Math.Min(1.0, i / (rate * 0.01)) * Math.Min(1.0, (len - i) / (rate * 0.03));
            buf[start + i] = (short)(Math.Sin(2 * Math.PI * freq * (i / (double)rate)) * env * 9000);
        }
    }
}
