using System;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace DuoVoz;

/// <summary>Musica "tocando agora" (titulo/artista) via SMTC do Windows — funciona com
/// Spotify, navegadores, Groove etc. Read-only; nao controla nada.</summary>
public readonly record struct TrackInfo(string Title, string Artist, bool Playing)
{
    public bool HasTrack => !string.IsNullOrWhiteSpace(Title);

    public string Display =>
        !HasTrack ? "" :
        string.IsNullOrWhiteSpace(Artist) ? Title : $"{Artist} - {Title}";
}

public static class NowPlaying
{
    private static GlobalSystemMediaTransportControlsSessionManager? _mgr;

    /// <summary>Faixa da sessao de midia atual, ou default (sem faixa) se nada tocando.</summary>
    public static async Task<TrackInfo> GetAsync()
    {
        try
        {
            _mgr ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var s = _mgr?.GetCurrentSession();
            if (s == null) return default;

            var props = await s.TryGetMediaPropertiesAsync();
            if (props == null) return default;

            var info = s.GetPlaybackInfo();
            bool playing = info?.PlaybackStatus ==
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            return new TrackInfo(props.Title ?? "", props.Artist ?? "", playing);
        }
        catch (Exception ex)
        {
            Log.Write("nowplaying: " + ex.Message);
            _mgr = null; // forca re-request na proxima
            return default;
        }
    }
}
