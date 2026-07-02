using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using Velopack;
using Velopack.Sources;

namespace DuoVoz;

/// <summary>
/// Atualizacao in-app via Velopack 1.2.0 + GitHub releases.
/// API verificada: GithubSource(repoUrl, accessToken, prerelease) â€” 4o arg (downloader)
/// e opcional; CheckForUpdatesAsync LANCA se o app nao for instalado (guardar com
/// IsInstalled); DownloadUpdatesAsync(info, Action&lt;int&gt; 0-100) roda em thread de
/// fundo; ApplyUpdatesAndRestart(asset) encerra o processo na hora.
/// </summary>
public static class Updater
{
    private const string RepoUrl = "https://github.com/BanePlayss/duovoz";
    private static bool _busy;

    private static UpdateManager NewManager() => new(new GithubSource(RepoUrl, null, false));

    /// <summary>Checagem silenciosa ~15s apos abrir; se houver update marca botao + badge.</summary>
    public static async void AutoCheckLater(Form owner, Button btn, Action onAvailable)
    {
        try
        {
            await Task.Delay(15000);
            if (owner.IsDisposed) return;
            var mgr = NewManager();
            if (!mgr.IsInstalled) return; // dotnet run / portable: Check lancaria
            var info = await mgr.CheckForUpdatesAsync();
            if (info == null || owner.IsDisposed) return;
            Log.Write($"update: disponivel v{info.TargetFullRelease.Version}");
            owner.BeginInvoke(() =>
            {
                if (btn.IsDisposed) return;
                btn.Text = $"Atualizar p/ v{info.TargetFullRelease.Version}";
                try { onAvailable(); } catch { }
            });
        }
        catch (Exception ex) { Log.Write("update: auto-check falhou: " + ex.Message); }
    }

    /// <summary>Fluxo do botao Atualizar (chamar na UI thread; await preserva o contexto).</summary>
    public static async Task CheckInteractiveAsync(Form owner, Button btn)
    {
        if (_busy) return;
        _busy = true;
        string original = btn.Text;
        try
        {
            var mgr = NewManager();
            if (!mgr.IsInstalled)
            {
                MessageBox.Show(owner,
                    "A atualizacao automatica so funciona na versao instalada pelo instalador.\n" +
                    "Baixe a versao mais recente em:\n" + RepoUrl + "/releases",
                    "DuoVoz", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            btn.Enabled = false;
            btn.Text = "Verificando...";
            var info = await mgr.CheckForUpdatesAsync();
            if (info == null)
            {
                MessageBox.Show(owner, $"Voce ja esta na versao mais recente (v{mgr.CurrentVersion}).",
                    "DuoVoz", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            btn.Text = "Baixando 0%";
            await mgr.DownloadUpdatesAsync(info, p =>
            {
                // Callback vem em thread de fundo -> marshal p/ a UI.
                try { owner.BeginInvoke(() => { if (!btn.IsDisposed) btn.Text = $"Baixando {p}%"; }); } catch { }
            });

            btn.Text = "Reiniciando...";
            Log.Write($"update: aplicando v{info.TargetFullRelease.Version} e reiniciando");
            mgr.ApplyUpdatesAndRestart(info.TargetFullRelease); // encerra o processo aqui
        }
        catch (Exception ex)
        {
            Log.Write("update: falhou: " + ex.Message);
            MessageBox.Show(owner, "Falha ao atualizar: " + ex.Message, "DuoVoz",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _busy = false;
            if (!btn.IsDisposed) { btn.Text = original; btn.Enabled = true; }
        }
    }
}
