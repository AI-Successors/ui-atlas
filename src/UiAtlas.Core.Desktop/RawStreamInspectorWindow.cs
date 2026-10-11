using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Reader;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Desktop;

/// <summary>Frame/provider navigation independent of native-window grouping.</summary>
internal sealed class RawStreamInspectorWindow : Window
{
    public RawStreamInspectorWindow(UiMappingReadModel model, UiEvidenceReader? evidence, UiMapVariantView? selected)
    {
        Title = "Raw frame streams — provider evidence";
        Width = 1100; Height = 760;
        var frames = model.Graph.Nodes.SelectMany(node => node.Evidence)
            .DistinctBy(item => (item.BundleId, item.FrameSequence))
            .OrderBy(item => item.BundleId).ThenBy(item => item.FrameSequence).ToArray();
        var picker = new ComboBox { Margin = new Thickness(8), ItemsSource = frames.Select(item =>
            $"{item.BundleId} • frame {item.FrameSequence}").ToArray() };
        var providers = new ComboBox { Margin = new Thickness(8) };
        var status = new TextBlock { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
        var payload = new TextBox { IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Consolas"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        var image = new Image { Stretch = Stretch.Uniform };
        var tabs = new TabControl { Items = {
            new TabItem { Header = "Provider records and provenance", Content = payload },
            new TabItem { Header = "Original frame image (includes covering windows)", Content = image } } };
        var panel = new DockPanel();
        foreach (var item in new UIElement[] { picker, status, providers })
        { DockPanel.SetDock(item, Dock.Top); panel.Children.Add(item); }
        panel.Children.Add(tabs); Content = panel;
        FrameObservation? frame = null;
        providers.SelectionChanged += (_, _) => payload.Text = JsonSerializer.Serialize(
            providers.SelectedIndex <= 0 ? (object?)frame : frame?.RawStreams?[providers.SelectedIndex - 1], JsonDefaults.Options);
        picker.SelectionChanged += (_, _) =>
        {
            frame = null; image.Source = null; payload.Clear(); providers.ItemsSource = null;
            if (picker.SelectedIndex < 0) return;
            var chosen = frames[picker.SelectedIndex];
            if (evidence is null || !evidence.SessionIds.Contains(chosen.BundleId))
            { status.Text = $"Evidence bundle is not attached: {chosen.BundleId}. This does not mean capture was missing."; return; }
            try
            {
                frame = evidence.ReadObservation(chosen.BundleId, chosen.FrameSequence);
                status.Text = $"{frame.Acquisition?.Status ?? "legacy unknown"} • stability: {frame.Acquisition?.StabilityStatus ?? "not checked"} • " +
                    $"{frame.Acquisition?.StartedUtc:HH:mm:ss.fff}–{frame.Acquisition?.SealedUtc:HH:mm:ss.fff}. " +
                    "Source entries are provider records, not unique physical controls. WindowImages lists independent HWND images.";
                if (frame.RawStreams?.Where(stream => stream.Source.StartsWith("uia.", StringComparison.Ordinal))
                    .SelectMany(stream => stream.Controls).Any(control => control.ScopeStatus is null) == true)
                    status.Text += " Legacy UIA window ownership was not verified; the stored query target is not proof of ownership.";
                providers.ItemsSource = new[] { "All frame evidence" }.Concat((frame.RawStreams ?? []).Select(stream =>
                    $"{stream.Source} [{stream.StreamId}] • {stream.Status} • {stream.Controls.Count} source entries")).ToArray();
                providers.SelectedIndex = 0;
                if (evidence.Read(chosen) is { } original) image.Source = WindowSnapshotCapture.DecodeOpaquePng(original.Png);
            }
            catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or
                System.Text.Json.JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
            { status.Text = $"Evidence could not be read: {ex.Message}"; }
        };
        picker.SelectedIndex = Math.Max(0, Array.FindIndex(frames, item => item.BundleId == selected?.BundleId && item.FrameSequence == selected.FrameSequence));
    }
}
