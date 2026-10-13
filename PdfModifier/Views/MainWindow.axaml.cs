using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PdfModifier.Controls;
using PdfModifier.Services;
using PdfModifier.ViewModels;

namespace PdfModifier.Views;

public static class Converters
{
    /// <summary>Shows just the file name of a full path.</summary>
    public static readonly IValueConverter FileName = new FuncValueConverter<string?, string>(path => Path.GetFileName(path) ?? "");
}

/// <summary>Lets a view model ask for the keyboard focus to go into the text box that shows one of its fields.</summary>
public static class FieldFocus
{
    public static readonly AttachedProperty<TextField?> SourceProperty =
        AvaloniaProperty.RegisterAttached<TextBox, TextField?>("Source", typeof(FieldFocus));

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBox, Listener> Listeners = new();

    static FieldFocus() => SourceProperty.Changed.AddClassHandler<TextBox>((box, change) =>
    {
        if (Listeners.TryGetValue(box, out var previous)) previous.Stop();
        Listeners.Remove(box);
        if (change.NewValue is TextField field) Listeners.Add(box, new Listener(box, field));
    });

    public static TextField? GetSource(TextBox box) => box.GetValue(SourceProperty);

    public static void SetSource(TextBox box, TextField? value) => box.SetValue(SourceProperty, value);

    /// <summary>
    /// Listens to one field for one text box, and only while the box is on screen: the option panel is built anew
    /// every time another tool is chosen, and the boxes of the old one must not be kept alive by the fields.
    /// </summary>
    private sealed class Listener
    {
        private readonly TextBox _box;
        private readonly TextField _field;

        public Listener(TextBox box, TextField field)
        {
            (_box, _field) = (box, field);
            field.FocusRequested += Take;
            box.DetachedFromVisualTree += Detached;
        }

        public void Stop()
        {
            _field.FocusRequested -= Take;
            _box.DetachedFromVisualTree -= Detached;
        }

        private void Detached(object? sender, VisualTreeAttachmentEventArgs e) => Stop();

        private void Take()
        {
            _box.Focus();
            _box.CaretIndex = _box.Text?.Length ?? 0;
        }
    }
}

public partial class MainWindow : Window
{
    private bool _syncingSelection;
    private bool _closeConfirmed;

    // Turning pages with the wheel: only after scrolling has come to rest at the edge, so that scrolling quickly
    // through a page does not skip on to the next one by accident.
    private long _lastScrolled, _lastTurned;
    private double _wheelAtEdge;

    private Point? _middleDrag;

    // Dragging pages: where a press on a page began while it may still become a drag, and the pages being dragged
    // while they are.
    private static readonly DataFormat<string> PagesFormat = DataFormat.CreateStringApplicationFormat("pdfmodifier.pages");
    private Point? _pagePress;
    private PageItem? _pressedPage;
    private bool _narrowSelectionOnRelease, _pageButtonDown;
    private IReadOnlyList<int>? _draggedPages;

    // Zooming: the spot of the page that stays where it is (the pointer, or else the middle of the view), and the
    // scale the page was last laid out at.
    private Point? _zoomAnchor;
    private double _shownScale = 1;

    public MainWindow()
    {
        InitializeComponent();

        PageList.SelectionChanged += OnPageSelectionChanged;
        PageList.ContainerPrepared += (_, e) =>
        {
            if (ViewModel is { } vm && e.Index >= 0 && e.Index < vm.Pages.Count) vm.RequestThumbnail(vm.Pages[e.Index]);
        };

        PageView.OverlayClicked += overlay => _ = ViewModel?.OverlayClicked(overlay);
        PageView.PageClicked += (x, y) => _ = ViewModel?.PageClicked(x, y);
        PageView.RegionSelected += region => _ = ViewModel?.RegionSelected(region);
        PageView.ObjectTransformed += (obj, bounds, rotation, scale, done) => ViewModel?.ObjectTransformed(obj, bounds, rotation, scale, done);
        PageView.ObjectActivated += obj => ViewModel?.ObjectActivated(obj);
        PageView.ObjectClicked += obj => ViewModel?.ObjectClicked(obj);
        PageView.Panned += delta => Viewer.Offset -= delta;

        FitButton.Click += (_, _) => ViewModel?.ZoomToFit(Viewer.Bounds.Width, Viewer.Bounds.Height);
        FitWidthButton.Click += (_, _) => ViewModel?.ZoomToWidth(Viewer.Bounds.Width);
        Viewer.AddHandler(PointerWheelChangedEvent, OnViewerWheel, RoutingStrategies.Tunnel);
        Viewer.AddHandler(PointerPressedEvent, OnViewerPressed, RoutingStrategies.Tunnel);
        Viewer.AddHandler(PointerMovedEvent, OnViewerMoved, RoutingStrategies.Tunnel);
        Viewer.AddHandler(PointerReleasedEvent, OnViewerReleased, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnKeyPressed, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnKeyReleased, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);

        PageList.AddHandler(PointerPressedEvent, OnPageListPressed, RoutingStrategies.Tunnel);
        PageList.AddHandler(PointerMovedEvent, OnPageListMoved, RoutingStrategies.Tunnel);
        PageList.AddHandler(PointerReleasedEvent, OnPageListReleased, RoutingStrategies.Tunnel);
        PagePanel.AddHandler(DragDrop.DragOverEvent, OnPagePanelDragOver);
        PagePanel.AddHandler(DragDrop.DragLeaveEvent, (_, _) => HideDropMark());
        PagePanel.AddHandler(DragDrop.DropEvent, OnPagePanelDrop);
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (ViewModel is not { } vm) return;
        vm.Dialogs = new Dialogs(this);
        vm.PagesRebuilt += SyncSelectionFromViewModel;
        // Wait for the layout pass that makes the page view visible, then show the whole first page.
        vm.DocumentOpened += () => Dispatcher.UIThread.Post(
            () => vm.ZoomToFit(Viewer.Bounds.Width, Viewer.Bounds.Height), DispatcherPriority.Background);
        vm.PageTurned += showEnd => ScrollPage(showEnd);
        _shownScale = vm.PointScale;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.PointScale)) KeepPlaceWhileZooming(vm);
        };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (ViewModel is { } vm) vm.RenderScaling = RenderScaling;
        FitToScreen();
    }

    /// <summary>
    /// On a screen smaller than the window would like to be, the window takes what there is: the button that applies
    /// a tool sits at its bottom edge and must not end up below the task bar.
    /// </summary>
    private void FitToScreen()
    {
        if (WindowState != WindowState.Normal || Screens.ScreenFromWindow(this) is not { } screen) return;
        var area = screen.WorkingArea;
        double roomWidth = area.Width / screen.Scaling - 24, roomHeight = area.Height / screen.Scaling - 48;
        if (roomWidth < 400 || roomHeight < 300 || (Width <= roomWidth && Height <= roomHeight)) return;
        double width = Math.Min(Width, roomWidth), height = Math.Min(Height, roomHeight);
        MinWidth = Math.Min(MinWidth, width);
        MinHeight = Math.Min(MinHeight, height);
        (Width, Height) = (width, height);
        Position = new PixelPoint(
            area.X + (int)Math.Max(0, (area.Width - (width + 16) * screen.Scaling) / 2),
            area.Y + (int)Math.Max(0, (area.Height - (height + 40) * screen.Scaling) / 2));
    }

    private void OnPageSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || ViewModel is not { } vm) return;
        var selected = PageList.SelectedItems?.OfType<PageItem>().Select(p => p.Index).ToList() ?? [];
        // The page shown is the one clicked last; when it was deselected, fall back to any that is still selected.
        var current = e.AddedItems.OfType<PageItem>().LastOrDefault()?.Index
                      ?? (selected.Contains(vm.CurrentPageIndex) ? null : selected.Cast<int?>().FirstOrDefault());
        var turned = current is { } index && index != vm.CurrentPageIndex;
        vm.SetSelection(selected, current);
        if (turned) ScrollPage(false);
    }

    private void SyncSelectionFromViewModel()
    {
        if (ViewModel is not { } vm) return;
        _syncingSelection = true;
        try
        {
            PageList.SelectedItems?.Clear();
            foreach (var index in vm.SelectedPageIndexes)
                if (index >= 0 && index < vm.Pages.Count)
                    PageList.SelectedItems?.Add(vm.Pages[index]);
            if (vm.CurrentPageIndex >= 0 && vm.CurrentPageIndex < vm.Pages.Count) PageList.ScrollIntoView(vm.CurrentPageIndex);
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    // ---- moving around the page and the document ----------------------------------------------------------------

    /// <summary>Shows the start of the page that was just put on view, or its end when it was reached going backwards.</summary>
    private void ScrollPage(bool showEnd)
    {
        void Place() => Viewer.Offset = new Vector(Viewer.Offset.X, showEnd ? Math.Max(0, Viewer.Extent.Height - Viewer.Viewport.Height) : 0);
        Place();
        // Once more after the layout has caught up, in case the new page has another size.
        Dispatcher.UIThread.Post(Place, DispatcherPriority.Loaded);
    }

    private void OnViewerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (ViewModel is not { HasDocument: true } vm) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            ZoomAt(e.Delta.Y > 0 ? 1.25 : 1 / 1.25, e.GetPosition(Viewer));
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || Math.Abs(e.Delta.Y) < 1e-6) return;
        var now = Environment.TickCount64;
        var room = Viewer.Extent.Height - Viewer.Viewport.Height;
        var atEdge = e.Delta.Y > 0 ? Viewer.Offset.Y <= 0.5 : Viewer.Offset.Y >= room - 0.5;
        if (!atEdge)
        {
            // Ordinary scrolling inside the page, left to the scroll viewer.
            _lastScrolled = now;
            _wheelAtEdge = 0;
            return;
        }

        e.Handled = true;
        // Arrived at the edge a moment ago, or just turned a page: not yet. Touchpads send many small steps, which
        // are added up until they amount to one notch of a wheel.
        if (now - _lastScrolled < 280 || now - _lastTurned < 180) return;
        _wheelAtEdge += e.Delta.Y;
        if (Math.Abs(_wheelAtEdge) < 0.95) return;
        var back = _wheelAtEdge > 0;
        _wheelAtEdge = 0;
        if (vm.TurnPage(back ? -1 : 1, showEnd: back)) _lastTurned = now;
    }

    /// <summary>Zooms by a factor while keeping the point of the page under <paramref name="anchor"/> where it is.</summary>
    private void ZoomAt(double factor, Point anchor)
    {
        if (ViewModel is not { } vm) return;
        _zoomAnchor = anchor;
        try
        {
            if (factor > 1) vm.ZoomInCommand.Execute(null);
            else vm.ZoomOutCommand.Execute(null);
        }
        finally
        {
            _zoomAnchor = null;
        }
    }

    /// <summary>
    /// Called when the scale has changed but the page is still laid out at the old one. Whatever asked for the zoom
    /// (the wheel, a button, a key), the page grows or shrinks around one spot: the one under the pointer, or the
    /// middle of the view. Without this the view would stay pinned to the top-left corner of the page.
    /// </summary>
    private void KeepPlaceWhileZooming(MainViewModel vm)
    {
        var grown = vm.PointScale / _shownScale;
        _shownScale = vm.PointScale;
        if (!vm.HasDocument || Math.Abs(grown - 1) < 1e-9 || PageView.Bounds.Width < 1) return;
        var anchor = _zoomAnchor ?? new Point(Viewer.Viewport.Width / 2, Viewer.Viewport.Height / 2);
        if (Viewer.TranslatePoint(anchor, PageView) is not { } onPage) return;

        // The page view learns of the new scale through its binding, which may come after this. So the rest waits
        // until everybody has heard, yet happens before the next frame is drawn: lay the page out at its new size
        // and see where the same spot of it ended up.
        Dispatcher.UIThread.Post(() =>
        {
            Viewer.UpdateLayout();
            if (PageView.TranslatePoint(new Point(onPage.X * grown, onPage.Y * grown), Viewer) is { } moved)
                Viewer.Offset += moved - anchor;
        }, DispatcherPriority.Send);
    }

    private void OnViewerPressed(object? sender, PointerPressedEventArgs e)
    {
        // The middle button takes hold of the page anywhere in the view, also beside the page.
        if (!e.GetCurrentPoint(Viewer).Properties.IsMiddleButtonPressed || e.Source is PageCanvas) return;
        _middleDrag = e.GetPosition(this);
        e.Pointer.Capture(Viewer);
        e.Handled = true;
    }

    private void OnViewerMoved(object? sender, PointerEventArgs e)
    {
        if (_middleDrag is not { } last) return;
        var now = e.GetPosition(this);
        Viewer.Offset -= now - last;
        _middleDrag = now;
        e.Handled = true;
    }

    private void OnViewerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_middleDrag == null) return;
        _middleDrag = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private Visual? Focused => FocusManager?.GetFocusedElement() as Visual;

    /// <summary>
    /// True while the keyboard is being used in a field or a list of the option panel, where the keys mean what they
    /// always mean there.
    /// </summary>
    private bool IsTyping() =>
        Focused?.GetSelfAndVisualAncestors().Any(v =>
            v is TextBox or ComboBox or Slider or NumericUpDown || (v is ListBox list && !ReferenceEquals(list, PageList))) == true;

    private void OnKeyPressed(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm || IsTyping()) return;
        var focused = Focused;
        if (e.Key == Key.Space && focused is null or PageCanvas or Window)
        {
            // Held down over the page, the space bar turns the mouse into a hand for moving it, whatever the tool.
            // On a button it keeps its usual meaning of pressing that button.
            PageView.GrabToPan = true;
            e.Handled = true;
            return;
        }

        if (!vm.HasDocument) return;
        var inPageList = focused?.GetSelfAndVisualAncestors().Contains(PageList) == true;
        if (inPageList && e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None)
        {
            // In the page list Delete means the pages that are selected there, whatever the tool is working on.
            vm.DeleteSelectedPagesCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (vm.KeyPressed(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers != KeyModifiers.None) return;
        // In the page list the arrows and Home/End move through its entries, which turns the pages just the same.
        switch (e.Key)
        {
            case Key.Left:
                vm.TurnPage(-1);
                break;
            case Key.Right:
                vm.TurnPage(1);
                break;
            case Key.Up when !inPageList:
                ScrollOrTurn(-1);
                break;
            case Key.Down when !inPageList:
                ScrollOrTurn(1);
                break;
            case Key.Home when !inPageList:
                vm.FirstPageCommand.Execute(null);
                break;
            case Key.End when !inPageList:
                vm.LastPageCommand.Execute(null);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnKeyReleased(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space) PageView.GrabToPan = false;
    }

    /// <summary>Scrolls the page a step up or down; at its edge, goes on to the neighbouring page.</summary>
    private void ScrollOrTurn(int direction)
    {
        if (ViewModel is not { } vm) return;
        var room = Viewer.Extent.Height - Viewer.Viewport.Height;
        var target = Math.Clamp(Viewer.Offset.Y + direction * 80, 0, Math.Max(0, room));
        if (Math.Abs(target - Viewer.Offset.Y) > 0.5) Viewer.Offset = new Vector(Viewer.Offset.X, target);
        else vm.TurnPage(direction, showEnd: direction < 0);
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        PageView.GrabToPan = false;
    }

    // ---- files --------------------------------------------------------------------------------------------------

    private static List<string> DroppedPaths(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()).OfType<string>().ToList() ?? [];

    private void OnDragOver(object? sender, DragEventArgs e) =>
        // Pages of this window are only taken back by its page list; anywhere else they are on their way out.
        e.DragEffects = _draggedPages == null && e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (_draggedPages != null || ViewModel is not { } vm) return;
        var paths = DroppedPaths(e);
        if (paths.Count > 0) await vm.DropFiles(paths);
    }

    // ---- the page list: dragging pages and dropping files -------------------------------------------------------------

    private void OnPageListPressed(object? sender, PointerPressedEventArgs e)
    {
        _pagePress = null;
        _narrowSelectionOnRelease = false;
        if (!e.GetCurrentPoint(PageList).Properties.IsLeftButtonPressed) return;
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(true)?.DataContext is not PageItem page) return;
        _pagePress = e.GetPosition(PageList);
        _pressedPage = page;
        _pageButtonDown = true;
        // Pressing on one of several selected pages keeps them all selected, so they can be dragged together. Only
        // when the press turns out to be a plain click does the selection narrow down to that page.
        if (e.KeyModifiers != KeyModifiers.None || PageList.SelectedItems is not { Count: > 1 } selected || !selected.Contains(page)) return;
        _narrowSelectionOnRelease = true;
        PageList.Focus();
        e.Handled = true;
    }

    private void OnPageListReleased(object? sender, PointerReleasedEventArgs e)
    {
        _pageButtonDown = false;
        if (_pagePress != null && _narrowSelectionOnRelease && _pressedPage is { } page) PageList.SelectedItem = page;
        _pagePress = null;
        _narrowSelectionOnRelease = false;
    }

    private async void OnPageListMoved(object? sender, PointerEventArgs e)
    {
        if (_pagePress is not { } start || _pressedPage is not { } page || ViewModel is not { IsBusy: false } vm) return;
        if (!e.GetCurrentPoint(PageList).Properties.IsLeftButtonPressed)
        {
            _pagePress = null;
            return;
        }

        var now = e.GetPosition(PageList);
        if (Math.Abs(now.X - start.X) < 6 && Math.Abs(now.Y - start.Y) < 6) return;
        _pagePress = null;
        _narrowSelectionOnRelease = false;

        // The page under the pointer, together with the others when it is one of those selected.
        var pages = PageList.SelectedItems?.OfType<PageItem>().Select(p => p.Index).Order().ToList() ?? [];
        if (!pages.Contains(page.Index)) pages = [page.Index];
        e.Pointer.Capture(null);
        _draggedPages = pages;
        try
        {
            // What leaves the window are pictures of the pages: dropped on a folder they become PNG files there.
            // Dropped back into the page list the same drag just moves the pages.
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(PagesFormat, string.Join(',', pages)));
            foreach (var path in await vm.PagePictures(pages))
                if (await StorageProvider.TryGetFileFromPathAsync(path) is { } file)
                    data.Add(DataTransferItem.CreateFile(file));
            // Let go of while the pictures were being made: there is nothing left to drag.
            if (!_pageButtonDown) return;
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        catch (Exception problem) when (problem is not OutOfMemoryException)
        {
            vm.StatusText = MainViewModel.Describe(problem);
        }
        finally
        {
            _draggedPages = null;
            _pageButtonDown = false;
            HideDropMark();
        }
    }

    /// <summary>
    /// Where something dropped at a point of the page panel goes: in front of which page, and at what height the mark
    /// that shows this is drawn.
    /// </summary>
    internal (int Index, double Y) DropPlace(Point position)
    {
        var count = PageList.ItemCount;
        (int Index, double Y)? after = null;
        for (var i = 0; i < count; i++)
        {
            // Only the pages on view have a place on screen; those are also the only ones one can drop between.
            if (PageList.ContainerFromIndex(i) is not { } item || item.TranslatePoint(default, PagePanel) is not { } corner) continue;
            if (position.Y < corner.Y + item.Bounds.Height / 2) return (i, corner.Y - 2);
            after = (i + 1, corner.Y + item.Bounds.Height + 2);
        }

        return after ?? (count, 8);
    }

    private void ShowDropMark(double y)
    {
        Canvas.SetLeft(DropMark, 14);
        Canvas.SetTop(DropMark, Math.Clamp(y - 1.5, 2, Math.Max(2, PagePanel.Bounds.Height - 5)));
        DropMark.Width = Math.Max(0, PagePanel.Bounds.Width - 28);
        DropMark.IsVisible = true;
    }

    private void HideDropMark() => DropMark.IsVisible = false;

    private void OnPagePanelDragOver(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        var own = _draggedPages != null;
        if (ViewModel is not { IsBusy: false } || (!own && !e.DataTransfer.Contains(DataFormat.File)))
        {
            e.DragEffects = DragDropEffects.None;
            HideDropMark();
            return;
        }

        e.DragEffects = own ? DragDropEffects.Move : DragDropEffects.Copy;
        var position = e.GetPosition(PagePanel);
        // Near the upper or lower edge the list moves on, so a page can be taken further than what is on view.
        if (PageList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } scroller)
        {
            var step = position.Y < 36 ? -18 : position.Y > PagePanel.Bounds.Height - 36 ? 18 : 0;
            if (step != 0)
                scroller.Offset = new Vector(scroller.Offset.X,
                    Math.Clamp(scroller.Offset.Y + step, 0, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height)));
        }

        ShowDropMark(DropPlace(position).Y);
    }

    private async void OnPagePanelDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        var index = DropPlace(e.GetPosition(PagePanel)).Index;
        HideDropMark();
        if (ViewModel is not { } vm) return;
        if (_draggedPages is { } pages)
        {
            await vm.MovePages(pages, index);
            return;
        }

        var paths = DroppedPaths(e);
        if (paths.Count > 0) await vm.AddFiles(paths, index);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        // Releases the document and deletes the undo history kept in the temp folder.
        ViewModel?.Session.Dispose();
        ViewModel?.DeleteScratchFiles();
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || ViewModel is not { Session.HasDocument: true } vm) return;
        if (!vm.Session.IsDirty && !vm.SelectedTool.HasPendingChanges) return;
        // Hold the window open while asking; closing again afterwards skips the question.
        e.Cancel = true;
        // An edit that is still only on screen has to be in the document before it can be saved or knowingly discarded.
        await vm.FlushTool();
        if (!await vm.ConfirmDiscard()) return;
        _closeConfirmed = true;
        Close();
    }
}
