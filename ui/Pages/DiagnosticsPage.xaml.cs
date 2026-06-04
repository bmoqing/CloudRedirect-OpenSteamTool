using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CloudRedirect.Resources;
using CloudRedirect.Services;
using Wpf.Ui.Controls;

namespace CloudRedirect.Pages;

public partial class DiagnosticsPage : Page
{
    public DiagnosticsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private sealed record UiStatusItem(string Name, string Detail, SymbolRegular Icon);

    private async Task LoadAsync()
    {
        var snapshot = await Task.Run(DiagnosticsService.Load);

        StatusItems.ItemsSource = snapshot.Items
            .Select(i => new UiStatusItem(
                i.Name,
                i.Detail,
                i.Ok ? SymbolRegular.CheckmarkCircle24 : SymbolRegular.Warning24))
            .ToList();

        OpenSteamToolItems.ItemsSource = snapshot.OpenSteamTool?.Items
            .Select(i => new UiStatusItem(
                i.Name,
                i.Detail,
                i.Ok ? SymbolRegular.CheckmarkCircle24 : SymbolRegular.Warning24))
            .ToList()
            ?? new List<UiStatusItem>
            {
                new("OpenSteamTool", "Steam path not found", SymbolRegular.Warning24)
            };

        LogOutput.Text = snapshot.RecentLogLines.Count > 0
            ? string.Join(Environment.NewLine, snapshot.RecentLogLines)
            : S.Get("Diagnostics_LogMissing");
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await LoadAsync();
    }
}
