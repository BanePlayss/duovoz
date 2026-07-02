using System;
using SpeexDSPSharp.Core;

namespace DuoVoz;

/// <summary>
/// Supressao de ruido do MICROFONE. Opera em frames exatos de 480 amostras
/// (10 ms @ 48 kHz mono 16-bit = 960 bytes) â€” exatamente o frame do DuoVoz.
/// Motor principal: SpeexDSP preprocessor (nativo, via SpeexDSPSharp).
/// Fallback 100% managed (high-pass ~80 Hz + gate adaptativo) se o nativo falhar.
/// NAO thread-safe: chamar somente da thread de captura do mic.
/// </summary>
public sealed class NoiseSuppressor : IDisposable
{
    public const int FrameSamples = 480;

    /// <summary>Liga/desliga em runtime (checkbox). Seguro trocar de outra thread.</summary>
    public volatile bool Enabled = true;

    private SpeexDSPPreprocessor? _pre;
    private bool _speexBroken;
    private readonly short[] _frame = new short[FrameSamples];

    // Estado do fallback managed.
    private float _hpPrevIn, _hpPrevOut;
    private float _noiseFloor = 200f;
    private float _gateGain = 1f;

    public NoiseSuppressor()
    {
        try
        {
            _pre = new SpeexDSPPreprocessor(FrameSamples, 48000);
            int on = 1, off = 0, suppressDb = -30; // -30 dB = supressao forte sem robotizar
            _pre.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_DENOISE, ref on);
            _pre.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_NOISE_SUPPRESS, ref suppressDb);
            _pre.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_AGC, ref off);     // PTT/VU ja existem
            _pre.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_VAD, ref off);
            _pre.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_DEREVERB, ref off);
            Log.Write("noise: SpeexDSP preprocessor ativo (480 amostras @ 48k, -30 dB)");
        }
        catch (Exception ex)
        {
            _pre = null;
            _speexBroken = true;
            Log.Write("noise: SpeexDSP indisponivel, usando fallback managed: " + ex.Message);
        }
    }

    /// <summary>Processa UM frame de 960 bytes PCM16 IN PLACE em buffer[offset..offset+length].</summary>
    public void Process(byte[] buffer, int offset, int length)
    {
        if (!Enabled || length != FrameSamples * 2) return;

        for (int i = 0; i < FrameSamples; i++)
            _frame[i] = (short)(buffer[offset + 2 * i] | (buffer[offset + 2 * i + 1] << 8));

        if (_pre != null && !_speexBroken)
        {
            try { _pre.Run(_frame); } // muta o frame in place
            catch (Exception ex)
            {
                _speexBroken = true;
                Log.Write("noise: Speex falhou em runtime, caindo p/ fallback: " + ex.Message);
                Fallback(_frame);
            }
        }
        else
        {
            Fallback(_frame);
        }

        for (int i = 0; i < FrameSamples; i++)
        {
            buffer[offset + 2 * i] = (byte)(_frame[i] & 0xFF);
            buffer[offset + 2 * i + 1] = (byte)((_frame[i] >> 8) & 0xFF);
        }
    }

    // High-pass 1a ordem ~80 Hz + gate com piso de ruido adaptativo e attack/release.
    private void Fallback(short[] f)
    {
        const float R = 0.98954f; // exp(-2*pi*80/48000)
        float peak = 0f;
        for (int i = 0; i < f.Length; i++)
        {
            float x = f[i];
            float y = R * (_hpPrevOut + x - _hpPrevIn);
            _hpPrevIn = x;
            _hpPrevOut = y;
            f[i] = (short)Math.Clamp(y, short.MinValue, short.MaxValue);
            float a = Math.Abs(y);
            if (a > peak) peak = a;
        }
        // Piso sobe muito devagar (fala nao contamina), desce rapido (silencio real).
        _noiseFloor = peak < _noiseFloor
            ? _noiseFloor * 0.95f + peak * 0.05f
            : _noiseFloor * 0.999f + peak * 0.001f;
        float thresh = Math.Max(_noiseFloor * 2.5f, 120f);
        float target = peak > thresh ? 1f : 0.08f; // gate nao fecha 100% (menos artefato)
        for (int i = 0; i < f.Length; i++)
        {
            _gateGain += (target - _gateGain) * (target > _gateGain ? 0.15f : 0.004f); // attack rapido / release lento
            f[i] = (short)(f[i] * _gateGain);
        }
    }

    public void Dispose()
    {
        try { _pre?.Dispose(); } catch { }
        _pre = null;
    }
}
