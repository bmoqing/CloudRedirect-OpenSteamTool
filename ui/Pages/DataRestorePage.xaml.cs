using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CloudRedirect.Resources;
using CloudRedirect.Services;
using Wpf.Ui.Controls;

namespace CloudRedirect.Pages;

public partial class DataRestorePage : Page
{
    private string? _steamPath;
    private SteamLoginUser? _currentUser;
    private RestoreSourceInfo? _source;

    public DataRestorePage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            try { await RefreshAsync(); }
            catch { }
        };
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        SetButtonsEnabled(false);
        ResultOutput.Text = S.Get("DataRestore_Detecting");

        var snapshot = await Task.Run(() =>
        {
            var steamPath = SteamDetector.FindSteamPath();
            SteamLoginUser? user = null;
            RestoreSourceInfo? source = null;
            if (steamPath != null)
            {
                user = SteamAccountService.GetMostRecentUser(steamPath);
                source = DataRestoreService.FindBestSource(steamPath, user?.AccountId.ToString());
            }
            return (steamPath, user, source);
        });

        _steamPath = snapshot.steamPath;
        _currentUser = snapshot.user;
        _source = snapshot.source;

        ApplySnapshot();
        ResultOutput.Text = S.Get("DataRestore_Ready");
    }

    private void ApplySnapshot()
    {
        if (_steamPath == null)
        {
            CurrentAccountText.Text = S.Get("DataRestore_SteamNotFound");
            CurrentAccountIcon.Symbol = SymbolRegular.Warning24;
            SourceText.Text = S.Get("DataRestore_NoSource");
            SourceCountsText.Text = "";
            SourceIcon.Symbol = SymbolRegular.Warning24;
            SetButtonsEnabled(false);
            return;
        }

        if (_currentUser == null)
        {
            CurrentAccountText.Text = S.Get("DataRestore_CurrentAccountMissing");
            CurrentAccountIcon.Symbol = SymbolRegular.Warning24;
        }
        else
        {
            var name = string.IsNullOrWhiteSpace(_currentUser.PersonaName)
                ? _currentUser.AccountName
                : _currentUser.PersonaName;
            CurrentAccountText.Text = S.Format("DataRestore_CurrentAccountFormat",
                name, _currentUser.AccountId, _currentUser.SteamId64);
            CurrentAccountIcon.Symbol = SymbolRegular.Person24;
        }

        if (_source == null)
        {
            SourceText.Text = S.Get("DataRestore_NoSource");
            SourceCountsText.Text = "";
            SourceIcon.Symbol = SymbolRegular.Warning24;
            SetButtonsEnabled(false);
            return;
        }

        SourceText.Text = S.Format("DataRestore_SourceFormat", _source.RootPath, _source.AccountId);
        SourceCountsText.Text = S.Format("DataRestore_SourceCountsFormat",
            _source.PlaytimeCount, _source.StatsCount, _source.LuaCount);
        SourceIcon.Symbol = SymbolRegular.Cloud24;
        RestorePlaytimeButton.IsEnabled = _source.PlaytimeCount > 0;
        RestoreStatsButton.IsEnabled = _source.StatsCount > 0;
        RestoreLuaButton.IsEnabled = _source.LuaCount > 0;
    }

    private async void RestorePlaytime_Click(object sender, RoutedEventArgs e)
    {
        await RunRestoreAsync(
            RestorePlaytimeButton,
            S.Get("DataRestore_RestorePlaytime"),
            S.Get("DataRestore_PlaytimeConfirmTitle"),
            S.Get("DataRestore_PlaytimeConfirmMessage"),
            source => DataRestoreService.RestorePlaytime(_steamPath!, source));
    }

    private async void RestoreStats_Click(object sender, RoutedEventArgs e)
    {
        await RunRestoreAsync(
            RestoreStatsButton,
            S.Get("DataRestore_RestoreStats"),
            S.Get("DataRestore_StatsConfirmTitle"),
            S.Get("DataRestore_StatsConfirmMessage"),
            source => DataRestoreService.RestoreStats(_steamPath!, source));
    }

    private async void RestoreLua_Click(object sender, RoutedEventArgs e)
    {
        await RunRestoreAsync(
            RestoreLuaButton,
            S.Get("DataRestore_RestoreLua"),
            S.Get("DataRestore_LuaConfirmTitle"),
            S.Get("DataRestore_LuaConfirmMessage"),
            source => DataRestoreService.RestoreLua(_steamPath!, source),
            requireSteamClosed: false);
    }

    private async Task RunRestoreAsync(
        Wpf.Ui.Controls.Button button,
        string idleText,
        string confirmTitle,
        string confirmMessage,
        Func<RestoreSourceInfo, RestoreSummary> restore,
        bool requireSteamClosed = true)
    {
        if (_steamPath == null || _source == null)
            return;

        if (requireSteamClosed && SteamDetector.IsSteamRunning())
        {
            await Dialog.ShowWarningAsync(S.Get("DataRestore_SteamRunningTitle"),
                S.Get("DataRestore_SteamRunningMessage"));
            return;
        }

        var confirmed = await Dialog.ConfirmAsync(confirmTitle, confirmMessage);
        if (!confirmed)
            return;

        SetButtonsEnabled(false);
        button.Content = S.Get("DataRestore_Restoring");
        ResultOutput.Text = S.Get("DataRestore_Restoring");

        try
        {
            var summary = await Task.Run(() => restore(_source));
            ResultOutput.Text = FormatSummary(summary);
            await Dialog.ShowInfoAsync(S.Get("DataRestore_Done"),
                S.Format("DataRestore_DoneMessage", summary.RestoredCount, summary.SkippedCount));
        }
        catch (Exception ex)
        {
            ResultOutput.Text = S.Format("DataRestore_FailedFormat", ex.Message);
            await Dialog.ShowErrorAsync(S.Get("Common_Error"),
                S.Format("DataRestore_FailedFormat", ex.Message));
        }
        finally
        {
            button.Content = idleText;
            ApplySnapshot();
        }
    }

    private static string FormatSummary(RestoreSummary summary)
    {
        var sb = new StringBuilder();
        sb.AppendLine(S.Format("DataRestore_ResultSummaryFormat",
            summary.RestoredCount, summary.SkippedCount, summary.AccountId));
        sb.AppendLine(S.Format("DataRestore_ResultSourceFormat", summary.SourceRoot));
        sb.AppendLine(S.Format("DataRestore_ResultTargetFormat", summary.TargetPath));
        if (!string.IsNullOrWhiteSpace(summary.BackupPath))
            sb.AppendLine(S.Format("DataRestore_ResultBackupFormat", summary.BackupPath));
        sb.AppendLine();

        foreach (var detail in summary.Details.Take(80))
            sb.AppendLine(detail);
        if (summary.Details.Count > 80)
            sb.AppendLine(S.Format("DataRestore_ResultMoreFormat", summary.Details.Count - 80));

        return sb.ToString();
    }

    private void SetButtonsEnabled(bool enabled)
    {
        RestorePlaytimeButton.IsEnabled = enabled;
        RestoreStatsButton.IsEnabled = enabled;
        RestoreLuaButton.IsEnabled = enabled;
    }
}
