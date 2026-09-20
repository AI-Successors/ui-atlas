using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using UiAtlas.Core.Cli;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Recording.Windows;
using UiAtlas.Core.Storage;
using static UiAtlas.Core.Tests.MapperOverlayWindowTests;

namespace UiAtlas.Core.Tests;

[Collection("Mapper desktop")]
public sealed class GridSchemaReviewWindowTests
{
    [Theory]
    [InlineData(GridCaptureStatus.Complete, GridRestorationStatus.Succeeded, true)]
    [InlineData(GridCaptureStatus.Partial, GridRestorationStatus.Failed, true)]
    [InlineData(GridCaptureStatus.Partial, GridRestorationStatus.SafelySkipped, true)]
    [InlineData(GridCaptureStatus.Failed, GridRestorationStatus.Failed, false)]
    public void ExtractButtonOpensHeaderReviewAndSavesEditedSchema(GridCaptureStatus status, GridRestorationStatus restoration, bool assembled)
    {
        Sta(() =>
        {
            using var fixture = new Fixture(status, restoration, assembled);
            var control = MapperHighlightTests.Control("Orders", "DataGrid", new(0, 0, 600, 120), "WPF", ["Grid"]);
            var selection = Assert.Single(MapperHighlightModel.Build(control.Bounds, [control]));
            var workspace = RecorderWorkspace.CreateStandaloneWorkspace(System.IO.Path.Combine(fixture.Directory, "orders.mlrec"), "OrdersApp");
            var context = new DataGridReviewContext(new("OrdersApp", "OrdersWindow", "Orders", [], new("Grid")),
                "orders-tab", new("Grid", "orders", "DataGrid", "Orders"), control.Bounds);
            var resumed = 0;
            var inspector = new MapperGridInspector(selection) { Left = 100, Top = 80, Explore = (_, _, _) => Task.FromResult(fixture.Source),
                SchemaExtractor = (source, header, dividers, token) => new GridSchemaExtractor().ExtractAsync(source, header, dividers, token),
                SaveDataGrid = (name, schema) => workspace.SaveDataGrid(name, context, schema) };
            inspector.Finished += () => resumed++;
            try
            {
                inspector.Show(); Pump(20);
                var exploring = inspector.StartExplorationAsync(); Await(() => exploring.IsCompleted); exploring.GetAwaiter().GetResult();
                inspector.UpdateLayout(); Pump(20);
                var extract = Descendants<Button>(inspector).Single(b => Equals(b.Content, "Extract"));
                Assert.True(extract.IsEnabled); Click(extract); Pump(50);
                var review = Assert.IsType<GridSchemaReviewWindow>(inspector.SchemaReviewWindow);
                Assert.True(review.IsVisible); Assert.True(review.IsActive);
                Click(Descendants<Button>(review).Single(b => Equals(b.Content, "Extract schema")));
                Await(() => !review.IsBusy);
                Assert.NotNull(review.Draft);
                Assert.Equal(new[] { "Order", "Name", "Total" }, review.Draft.Schema.Columns.Select(c => c.Label));
                var grid = Descendants<DataGrid>(review).Single();
                var rows = grid.Items.Cast<GridSchemaReviewWindow.ColumnRow>().ToArray();
                // Exercise the actual editing control, including keyboard focus in the owned review window.
                grid.SelectedIndex = 0; grid.CurrentCell = new DataGridCellInfo(rows[0], grid.Columns[1]);
                grid.Focus(); Assert.True(grid.BeginEdit()); Pump(20);
                var editor = Descendants<TextBox>(grid).First(); editor.Focus(); editor.Text = "Order ID";
                Pump(20);
                var qa = Environment.GetEnvironmentVariable("UIATLAS_MAPPER_QA_DIR");
                if (!string.IsNullOrWhiteSpace(qa) && status == GridCaptureStatus.Partial && restoration == GridRestorationStatus.Failed)
                    Render((FrameworkElement)review.Content, System.IO.Path.Combine(qa, "schema-review.png"));
                Click(Descendants<Button>(review).Single(b => Equals(b.Content, "Save schema")));
                Await(() => !review.IsBusy);
                Assert.NotNull(review.SavedReview); Assert.Equal("Order ID", review.SavedReview.Schema.Columns[0].Label);
                Assert.Equal(status, review.SavedReview.CaptureStatus); Assert.Equal(restoration, review.SavedReview.Restoration.Status);
                Assert.NotNull(review.SavedPath); Assert.True(File.Exists(review.SavedPath));
                var saved = JsonSerializer.Deserialize<GridSchemaReview>(File.ReadAllText(review.SavedPath), JsonDefaults.Options)!;
                Assert.Equal("Order ID", saved.Schema.Columns[0].Label);
                Assert.False(review.IsVisible); Assert.Null(inspector.SchemaReviewWindow);
                Assert.Equal(0, resumed); Assert.True(inspector.IsVisible);
                Assert.NotNull(inspector.SavedSchema);
                Assert.Equal(status, inspector.Result!.Capture.Status);
                Click(Descendants<Button>(inspector).Single(b => Equals(b.Content, "Edit schema"))); Pump(20);
                Assert.Equal("Order ID", inspector.SchemaReviewWindow!.Draft!.Schema.Columns[0].Label);
                inspector.SchemaReviewWindow.Close(); Pump(20);
                var nameBox = Assert.Single(Descendants<TextBox>(inspector));
                Assert.Equal("Orders", nameBox.Text);
                var saveGrid = Descendants<Button>(inspector).Single(b => Equals(b.Content, "Save"));
                nameBox.Text = "  "; Assert.False(saveGrid.IsEnabled);
                nameBox.Text = "Open orders"; Assert.True(saveGrid.IsEnabled); Click(saveGrid);
                Assert.False(inspector.IsVisible); Assert.Equal(1, resumed);
                var manifest = LogicalMapSessionStore.Load(workspace.SessionManifestPath);
                var namedGrid = Assert.Single(manifest.DataGrids!);
                Assert.Equal("Open orders", namedGrid.DisplayName); Assert.Equal("orders-tab", namedGrid.Context.SurfaceLayerKey);
                Assert.Equal("orders", namedGrid.Context.Control.AutomationId);
                Assert.Equal("Order ID", namedGrid.Review.Schema.Columns[0].Label);
                var restored = RecorderWorkspace.CreateExistingWorkspace(workspace.MapPath, workspace.DefaultExportPath, workspace.SessionManifestPath, manifest);
                restored.AddCompletedSession("next", System.IO.Path.Combine(fixture.Directory, "next.mlrec"));
                Assert.Equal("Open orders", Assert.Single(LogicalMapSessionStore.Load(workspace.SessionManifestPath).DataGrids!).DisplayName);
            }
            finally { inspector.SchemaReviewWindow?.Close(); inspector.Close(); }
        });
    }

    [Fact]
    public void ChangingHeaderSelectionInvalidatesEarlierReadAndClosingCancelsWork()
    {
        Sta(() =>
        {
            using var fixture = new Fixture(GridCaptureStatus.Partial, GridRestorationStatus.Failed, true);
            using var started = new ManualResetEventSlim();
            CancellationToken operationToken = default;
            var review = new GridSchemaReviewWindow(fixture.Source, extractor: async (_, _, _, token) =>
            {
                operationToken = token; started.Set(); await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException();
            });
            review.Show();
            var changed = new RectI(0, 40, 600, 36); review.SelectHeader(changed); Assert.Equal(changed, review.HeaderSelection);
            Assert.False(Descendants<Button>(review).Single(b => Equals(b.Content, "Save schema")).IsEnabled);
            var task = review.ExtractAsync(); Assert.True(started.IsSet);
            review.Close(); Await(() => task.IsCompleted); task.GetAwaiter().GetResult();
            Assert.True(operationToken.IsCancellationRequested); Assert.Null(review.SavedReview);
        });
    }

    [Fact]
    public void SelectedDividersCanBeDraggedNudgedRemovedAndAddedBeforeSaving()
    {
        Sta(() =>
        {
            using var fixture = new Fixture(GridCaptureStatus.Partial, GridRestorationStatus.Failed, true);
            var review = new GridSchemaReviewWindow(fixture.Source, extractor: (_, header, dividers, _) =>
                Task.FromResult(fixture.Review(header, dividers)));
            Button Find(string name) => Descendants<Button>(review).Single(b => Equals(b.Content, name));
            void Read() { Click(Find("Extract schema")); Await(() => !review.IsBusy); Assert.NotNull(review.Draft); }
            try
            {
                review.Show(); Pump(30); Read();
                Descendants<CheckBox>(review).Single().IsChecked = true;
                Assert.False(Find("Remove divider").IsEnabled);
                // Select from the table body, well below the header, then drag without crossing a neighbour.
                review.BeginPreviewInteraction(new(200, 90));
                review.ContinuePreviewInteraction(new(-30, 90));
                Assert.False(Find("Move left").IsEnabled);
                review.ContinuePreviewInteraction(new(900, 90));
                Assert.False(Find("Move right").IsEnabled);
                review.ContinuePreviewInteraction(new(240, 90)); review.EndPreviewInteraction();
                Assert.Null(review.Draft); Assert.False(Find("Save schema").IsEnabled);
                Click(Find("Move right")); Click(Find("Move left")); Read();
                Assert.Equal(new[] { 0, 240, 400 }, review.Draft!.HeaderReadings.Select(r => r.Bounds.X));
                review.BeginPreviewInteraction(new(240, 90)); review.EndPreviewInteraction();
                Click(Find("Remove divider"));
                Assert.False(Find("Remove divider").IsEnabled);
                Click(Find("Add divider")); review.BeginPreviewInteraction(new(399, 90));
                Assert.Contains("Cancel adding", Descendants<Button>(review).Select(b => b.Content));
                review.BeginPreviewInteraction(new(300, 90)); Read();
                Assert.Equal(new[] { 0, 300, 400 }, review.Draft!.HeaderReadings.Select(r => r.Bounds.X));
                Assert.Equal(fixture.Source.HeaderBounds, review.HeaderSelection);
                var save = review.SaveAsync(); Await(() => save.IsCompleted); save.GetAwaiter().GetResult();
                var saved = JsonSerializer.Deserialize<GridSchemaReview>(File.ReadAllText(review.SavedPath!), JsonDefaults.Options)!;
                Assert.Equal(new double[] { 300, 400, 600 }, saved.Schema.Columns.Select(c => c.EndX));
                Assert.False(saved.Schema.IsComplete);
                // Removing every detected divider must remain an explicit one-column schema on the next read.
                foreach (var x in new[] { 300, 400 })
                { review.BeginPreviewInteraction(new(x, 90)); review.EndPreviewInteraction(); Click(Find("Remove divider")); }
                Read(); Assert.Single(review.Draft!.Schema.Columns);
            }
            finally { review.Close(); }
        });
    }

    [Fact]
    public void WideImageOverlaysRemainVisibleAndSelectableAtEveryZoom()
    {
        Sta(() =>
        {
            using var fixture = new Fixture(GridCaptureStatus.Partial, GridRestorationStatus.Failed, true, 4800, 600, 12);
            var review = new GridSchemaReviewWindow(fixture.Source, extractor: (_, header, dividers, _) =>
                Task.FromResult(fixture.Review(header, dividers)));
            try
            {
                review.Show(); Pump(30);
                var task = review.ExtractAsync(); Await(() => task.IsCompleted); task.GetAwaiter().GetResult();
                var canvas = Descendants<Canvas>(review).Single();
                var zoom = Descendants<ComboBox>(review).Single();
                var scale = Assert.IsType<ScaleTransform>(canvas.LayoutTransform);
                Assert.InRange(scale.ScaleX, .01, .3);
                foreach (var index in new[] { 0, 1, 2, 0 })
                {
                    zoom.SelectedIndex = index; review.UpdateLayout(); Pump(20);
                    var lines = canvas.Children.OfType<Line>().Where(l => !Equals(l.Stroke, Brushes.White)).ToArray();
                    Assert.Equal(11, lines.Length);
                    Assert.All(lines, line =>
                    {
                        Assert.Equal(600, line.Y2); Assert.Equal(0, line.Y1);
                        Assert.Equal(1.5, line.StrokeThickness * scale.ScaleX, 6);
                    });
                    var header = Assert.Single(canvas.Children.OfType<Rectangle>(), r => !Equals(r.Stroke, Brushes.White));
                    Assert.Equal(2, header.StrokeThickness * scale.ScaleX, 6);
                    Assert.True(header.Height > 0);
                    Assert.Equal(0, Canvas.GetTop(header));
                    Assert.Equal(review.HeaderSelection.Height, header.RenderedGeometry.Bounds.Height + header.StrokeThickness, 6);
                }
                var qa = Environment.GetEnvironmentVariable("UIATLAS_MAPPER_QA_DIR");
                if (!string.IsNullOrWhiteSpace(qa)) Render((FrameworkElement)review.Content, System.IO.Path.Combine(qa, "schema-dividers-fit.png"));
                Descendants<CheckBox>(review).Single().IsChecked = true; review.UpdateLayout(); Pump(20);
                // Hit tolerance is measured on screen even when the captured image is much wider than the window.
                review.BeginPreviewInteraction(new(800 + 6 / scale.ScaleX, 500)); review.EndPreviewInteraction();
                review.UpdateLayout(); Pump(30);
                Assert.True(Descendants<Button>(review).Single(b => Equals(b.Content, "Remove divider")).IsEnabled);
                Assert.Equal(2, canvas.Children.OfType<Line>().Count(l => l.X1 == 800));
                Assert.Contains(canvas.Children.OfType<Line>(), l => l.X1 == 800 && Math.Abs(l.StrokeThickness * scale.ScaleX - 3) < .001);
                if (!string.IsNullOrWhiteSpace(qa)) Render((FrameworkElement)review.Content, System.IO.Path.Combine(qa, "schema-dividers-edit.png"));
            }
            finally { review.Close(); }
        });
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ColumnMenuRemovesItsOwnRowAndPersistsRemainingPhysicalColumns(int removedIndex)
    {
        Sta(() =>
        {
            using var fixture = new Fixture(GridCaptureStatus.Complete, GridRestorationStatus.Succeeded, true);
            var original = GridSchemaExtractor.Confirm(fixture.Review(fixture.Source.HeaderBounds, null) with { Reasons = [] }, ["Code", "Code", ""]);
            Assert.True(original.Schema.IsComplete);
            var review = new GridSchemaReviewWindow(fixture.Source, original);
            GridSchemaReviewWindow? reopened = null;
            try
            {
                review.Show(); Pump(30);
                var grid = Descendants<DataGrid>(review).Single();
                var editedIndex = (removedIndex + 1) % 3;
                grid.SelectedIndex = editedIndex;
                grid.CurrentCell = new DataGridCellInfo(grid.Items[editedIndex], grid.Columns[1]);
                grid.Focus(); Assert.True(grid.BeginEdit()); Pump(20);
                Descendants<TextBox>(grid).First().Text = "Edited header"; Pump(20);
                var menu = OpenColumnMenu(grid, removedIndex);
                Assert.Equal(removedIndex, grid.SelectedIndex);
                var remove = Assert.IsType<MenuItem>(Assert.Single(menu.Items.Cast<object>()));
                Assert.Equal("Remove column", remove.Header); Assert.True(remove.IsEnabled);
                var qa = Environment.GetEnvironmentVariable("UIATLAS_MAPPER_QA_DIR");
                if (!string.IsNullOrWhiteSpace(qa) && removedIndex == 1)
                    Render(menu, System.IO.Path.Combine(qa, "schema-remove-menu.png"));
                remove.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); menu.IsOpen = false; Pump(20);

                var expected = original.Schema.Columns.Select((column, i) => i == editedIndex ? column with { Label = "Edited header" } : column)
                    .Where((_, i) => i != removedIndex).Select((column, i) => column with { Ordinal = i }).ToArray();
                Assert.Equal(expected, review.Draft!.Schema.Columns);
                Assert.Null(review.Draft.ReviewedUtc);
                Assert.Equal(new[] { 1, 2 }, grid.Items.Cast<GridSchemaReviewWindow.ColumnRow>().Select(row => row.Number));
                Assert.Contains(Descendants<TextBlock>(review), text => text.Text.Contains("2 columns remaining", StringComparison.Ordinal));
                if (!string.IsNullOrWhiteSpace(qa) && removedIndex == 1)
                    Render((FrameworkElement)review.Content, System.IO.Path.Combine(qa, "schema-column-removed.png"));

                var save = review.SaveAsync(); Await(() => save.IsCompleted); save.GetAwaiter().GetResult();
                var saved = JsonSerializer.Deserialize<GridSchemaReview>(File.ReadAllText(review.SavedPath!), JsonDefaults.Options)!;
                Assert.Equal(expected, saved.Schema.Columns);
                Assert.Equal(original.HeaderReadings.Where((_, i) => i != removedIndex), saved.HeaderReadings);
                Assert.Equal(original.ImageSha256, saved.ImageSha256); Assert.Equal(original.HeaderBounds, saved.HeaderBounds);
                Assert.Equal(original.CaptureStatus, saved.CaptureStatus); Assert.Equal(original.Restoration, saved.Restoration);
                Assert.Equal(original.Coverage, review.SavedReview!.Coverage);
                Assert.False(saved.Schema.IsComplete); Assert.Null(saved.Schema.KnownColumnCount);
                Assert.NotNull(saved.ReviewedUtc);
                review.Close();
                reopened = new GridSchemaReviewWindow(fixture.Source, saved); reopened.Show(); Pump(20);
                Assert.Equal(expected, reopened.Draft!.Schema.Columns);
                var boundaries = Descendants<Canvas>(reopened).Single().Children.OfType<Line>()
                    .Select(line => line.X1).Distinct().Order().ToArray();
                Assert.Equal(new double[] { 200, 400 }, boundaries);
            }
            finally { reopened?.Close(); review.Close(); }
        });
    }

    [Fact]
    public void ColumnRemovalKeepsOneColumnAndCannotApplyWhileExtractionIsRunning()
    {
        Sta(() =>
        {
            using var fixture = new Fixture(GridCaptureStatus.Partial, GridRestorationStatus.Failed, true);
            var pending = new TaskCompletionSource<GridSchemaReview>();
            var review = new GridSchemaReviewWindow(fixture.Source, fixture.Review(fixture.Source.HeaderBounds, null),
                extractor: (_, _, _, _) => pending.Task);
            try
            {
                review.Show(); Pump(30);
                var grid = Descendants<DataGrid>(review).Single();
                for (var count = 3; count > 1; count--)
                {
                    var menu = OpenColumnMenu(grid, 0);
                    var remove = Assert.IsType<MenuItem>(menu.Items[0]);
                    Assert.True(remove.IsEnabled); remove.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    menu.IsOpen = false; Pump(20);
                    Assert.Equal(count - 1, review.Draft!.Schema.Columns.Count);
                }
                var lastMenu = OpenColumnMenu(grid, 0);
                var lastRemove = Assert.IsType<MenuItem>(lastMenu.Items[0]);
                Assert.False(lastRemove.IsEnabled);
                lastRemove.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); lastMenu.IsOpen = false;
                Assert.Single(review.Draft!.Schema.Columns);
                var save = review.SaveAsync(); Await(() => save.IsCompleted); save.GetAwaiter().GetResult();
                Assert.Single(review.SavedReview!.Schema.Columns);

                pending.SetResult(fixture.Review(fixture.Source.HeaderBounds, null));
                var extraction = review.ExtractAsync(); Await(() => extraction.IsCompleted); extraction.GetAwaiter().GetResult();
                pending = new TaskCompletionSource<GridSchemaReview>();
                var busyMenu = OpenColumnMenu(grid, 1);
                var busyRemove = Assert.IsType<MenuItem>(busyMenu.Items[0]);
                Assert.True(busyRemove.IsEnabled);
                extraction = review.ExtractAsync(); Assert.True(review.IsBusy);
                busyRemove.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); busyMenu.IsOpen = false;
                Assert.Equal(3, review.Draft!.Schema.Columns.Count);
                pending.SetResult(fixture.Review(fixture.Source.HeaderBounds, null));
                Await(() => extraction.IsCompleted); extraction.GetAwaiter().GetResult();
            }
            finally { pending.TrySetCanceled(); review.Close(); }
        });
    }

    private static ContextMenu OpenColumnMenu(DataGrid grid, int index)
    {
        grid.ScrollIntoView(grid.Items[index]); grid.UpdateLayout(); Pump(20);
        var row = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromIndex(index));
        var menu = Assert.IsType<ContextMenu>(row.ContextMenu);
        menu.PlacementTarget = row; menu.IsOpen = true; Pump(20);
        return menu;
    }

    [Fact]
    public void FailedGridSaveKeepsNamingOpenAndClosingResumesOnce()
    {
        Sta(() =>
        {
            using var fixture = new Fixture(GridCaptureStatus.Partial, GridRestorationStatus.Failed, true);
            var control = MapperHighlightTests.Control("Orders", "DataGrid", new(0, 0, 600, 120), "WPF", ["Grid"]);
            var inspector = new MapperGridInspector(Assert.Single(MapperHighlightModel.Build(control.Bounds, [control])))
            {
                Explore = (_, _, _) => Task.FromResult(fixture.Source),
                SchemaExtractor = (_, header, dividers, _) => Task.FromResult(fixture.Review(header, dividers)),
                SaveDataGrid = (_, _) => throw new IOException("fixture-save-failure")
            };
            var resumed = 0; inspector.Finished += () => resumed++;
            try
            {
                inspector.Show();
                var explore = inspector.StartExplorationAsync(); Await(() => explore.IsCompleted);
                inspector.OpenSchemaReview();
                var review = inspector.SchemaReviewWindow!;
                var extract = review.ExtractAsync(); Await(() => extract.IsCompleted);
                var save = review.SaveAsync(closeAfterSave: true); Await(() => save.IsCompleted);
                Assert.False(review.IsVisible);
                Click(Descendants<Button>(inspector).Single(b => Equals(b.Content, "Save")));
                Assert.True(inspector.IsVisible); Assert.Equal(0, resumed);
                Assert.Contains(Descendants<TextBlock>(inspector), text => text.Text.Contains("could not be saved", StringComparison.Ordinal));
                Click(Descendants<Button>(inspector).Single(b => Equals(b.Content, "×")));
                Assert.Equal(1, resumed);
                Assert.Equal("Column 1 / Column 2 table", MapperGridInspector.SuggestGridName("DataGrid", inspector.SavedSchema!));
            }
            finally { inspector.Close(); }
        });
    }

    [Fact]
    public void ClosingDuringExplorationQueuesResumeOnlyAfterExplorationUnwinds()
    {
        Sta(() =>
        {
            using var panel = new RecordingControlPanel("orders", "OrdersApp");
            var pending = new TaskCompletionSource<GridImageExplorationResult>();
            var control = MapperHighlightTests.Control("Orders", "DataGrid", new(0, 0, 600, 120), "WPF", ["Grid"]);
            var inspector = new MapperGridInspector(Assert.Single(MapperHighlightModel.Build(control.Bounds, [control])))
                { Explore = (_, _, _) => pending.Task };
            inspector.Finished += panel.RequestResumeAfterGrid;
            try
            {
                inspector.Show(); var operation = inspector.StartExplorationAsync();
                Assert.True(inspector.IsExploring); inspector.Close();
                Assert.False(panel.TryDequeueCommand(out _));
                pending.SetCanceled(); Await(() => operation.IsCompleted); operation.GetAwaiter().GetResult();
                Assert.True(panel.TryDequeueCommand(out var command)); Assert.Equal("R", command);
                Assert.False(panel.TryDequeueCommand(out _));
            }
            finally { pending.TrySetCanceled(); inspector.Close(); }
        });
    }

    private sealed class Fixture : IDisposable
    {
        internal string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "schema-ui-test-" + Guid.NewGuid().ToString("N"));
        internal GridImageExplorationResult Source { get; }
        private readonly int _columnCount;
        internal Fixture(GridCaptureStatus status, GridRestorationStatus restoration, bool assembled,
            int width = 600, int height = 120, int columnCount = 3)
        {
            _columnCount = columnCount;
            System.IO.Directory.CreateDirectory(Directory);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                dc.DrawRectangle(Brushes.WhiteSmoke, null, new Rect(0, 0, width, 36));
                var names = new[] { "Order", "Name", "Total" };
                for (var i = 0; i < columnCount; i++)
                {
                    dc.DrawText(new FormattedText(i < names.Length ? names[i] : $"Column {i + 1}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface("Segoe UI"), 20, Brushes.Black, 1), new Point(i * width / columnCount + 12, 4));
                    dc.DrawText(new FormattedText(i == 0 ? "101" : i == 1 ? "Guest" : "40.29", CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, new Typeface("Segoe UI"), 18, Brushes.Black, 1), new Point(i * width / columnCount + 12, 45));
                }
                for (var i = 1; i < columnCount; i++) dc.DrawRectangle(Brushes.Gray, null, new Rect(i * width / columnCount, 0, 1, height));
                for (var y = 36; y < height; y += 40) dc.DrawRectangle(Brushes.Gray, null, new Rect(0, y, width, 1));
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream(); encoder.Save(stream); var png = stream.ToArray();
            var path = System.IO.Path.Combine(Directory, "table.png"); File.WriteAllBytes(path, png);
            var image = new RectI(0, 0, width, height); var header = new RectI(0, 0, width, 36); var body = new RectI(0, 36, width, height - 36);
            var now = DateTimeOffset.UtcNow;
            var tile = new GridCapturedTile("tile", path, Convert.ToHexString(SHA256.HashData(png)), now, image, header, body, 0, 0, assembled, []);
            var capture = new CapturedGrid("acquisition", "grid", new("image", false, null, [], 0, 36), new(1, 1, 1, now, 1, image, image),
                now, now, status, GridAcquisitionStage.Capture, status == GridCaptureStatus.Complete ? [] : ["tile-limit"], [tile], [], [],
                new(true, true, status == GridCaptureStatus.Complete, status == GridCaptureStatus.Complete, true, false, false, [body], []),
                new(restoration, "fixture", false, false), System.IO.Path.Combine(Directory, "manifest.json"));
            Source = new(capture, assembled ? path : null, image, header, capture.Reasons);
        }
        internal GridSchemaReview Review(RectI header, IReadOnlyList<int>? dividers)
        {
            var edges = new[] { header.X }.Concat(dividers ?? Enumerable.Range(1, _columnCount - 1)
                .Select(i => header.X + i * header.Width / _columnCount).ToArray()).Append(header.X + header.Width).ToArray();
            var columns = edges.Zip(edges.Skip(1)).Select((pair, i) => new GridColumn($"column-{i}", i, $"Column {i + 1}", pair.First, pair.Second)).ToArray();
            return new(Source.Capture.AcquisitionId, Source.Capture.GridId, new("fixture", false, null, columns, 0, header.Height),
                header, Source.ImagePath!, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Source.ImagePath!))).ToLowerInvariant(),
                Source.Capture.ManifestPath, Source.Capture.Status, Source.Capture.Coverage, Source.Capture.Restoration,
                columns.Select(c => new GridSchemaHeaderReading(c.ColumnKey, new((int)c.StartX, header.Y, (int)(c.EndX - c.StartX), header.Height),
                    GridCellReadStatus.Text, c.Label, null)).ToArray(), ["schema-width-unverified"]);
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
