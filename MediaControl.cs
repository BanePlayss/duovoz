using System;
using System.Runtime.InteropServices;

namespace DuoVoz;

/// <summary>Acao de midia trocada entre os pares (controle remoto de musica).</summary>
public enum MediaAction { Next, Prev, PlayPause }

/// <summary>
/// Injeta as teclas de midia GLOBAIS do Windows. Spotify e a maioria dos players
/// respondem a elas mesmo sem estar em foco (precisa das "teclas de midia globais"
/// ligadas no player — no Spotify e o padrao).
/// </summary>
public static class MediaKeys
{
    private const byte VK_MEDIA_NEXT = 0xB0;
    private const byte VK_MEDIA_PREV = 0xB1;
    private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    public static void Send(MediaAction a)
    {
        byte vk = a switch
        {
            MediaAction.Next => VK_MEDIA_NEXT,
            MediaAction.Prev => VK_MEDIA_PREV,
            _ => VK_MEDIA_PLAY_PAUSE,
        };
        try
        {
            keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
            keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
        catch (Exception ex) { Log.Write("media inject falhou: " + ex.Message); }
    }
}

/// <summary>
/// Hook global de teclado (WH_KEYBOARD_LL) que so olha as teclas de midia
/// (next / prev / play-pause). Ignora eventos INJETADOS (evita eco com
/// MediaKeys.Send no lado que recebe). Instalar/descartar na UI thread (precisa
/// de message loop). Nao capta nenhuma outra tecla — passa tudo adiante.
/// </summary>
public sealed class MediaKeyHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const uint LLKHF_INJECTED = 0x10;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    private readonly LowLevelKeyboardProc _proc; // mantem a delegate viva (anti-GC)
    private IntPtr _hook;

    /// <summary>Handler da tecla de midia local. Retorna true p/ SUPRIMIR a tecla
    /// aqui (quando ela foi encaminhada ao par em vez de tocar no player local).</summary>
    public Func<MediaAction, bool>? OnMediaKey;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    public MediaKeyHook()
    {
        _proc = Proc;
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandleW(null), 0);
        Log.Write(_hook == IntPtr.Zero ? "media hook: SetWindowsHookEx FALHOU" : "media hook: instalado");
    }

    private IntPtr Proc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && ((int)wParam == WM_KEYDOWN || (int)wParam == WM_SYSKEYDOWN))
        {
            int vk = Marshal.ReadInt32(lParam, 0);         // KBDLLHOOKSTRUCT.vkCode
            uint flags = (uint)Marshal.ReadInt32(lParam, 8); // KBDLLHOOKSTRUCT.flags
            if ((flags & LLKHF_INJECTED) == 0)             // ignora a nossa propria injecao
            {
                MediaAction? a = vk switch
                {
                    0xB0 => MediaAction.Next,
                    0xB1 => MediaAction.Prev,
                    0xB3 => MediaAction.PlayPause,
                    _ => (MediaAction?)null,
                };
                var cb = OnMediaKey;
                if (a != null && cb != null)
                {
                    bool suppress = false;
                    try { suppress = cb(a.Value); }
                    catch (Exception ex) { Log.Write("media hook cb: " + ex.Message); }
                    if (suppress) return (IntPtr)1; // consome a tecla localmente
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            try { UnhookWindowsHookEx(_hook); } catch { }
            _hook = IntPtr.Zero;
        }
    }
}
