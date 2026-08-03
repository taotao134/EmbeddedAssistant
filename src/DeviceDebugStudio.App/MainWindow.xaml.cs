using System.ComponentModel;
using System.Collections.Specialized;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DeviceDebugStudio.App.ViewModels;
using DeviceDebugStudio.Core.Transports;
using DeviceDebugStudio.Infrastructure.Persistence;
using DeviceDebugStudio.Infrastructure.Transports;
using DeviceDebugStudio.Infrastructure.Updates;
using Microsoft.Win32;
using ScottPlot.Plottables;
using Serilog;
using Wpf.Ui.Appearance;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;

namespace DeviceDebugStudio.App;

public partial class MainWindow : FluentWindow
{
    private const double MinimumLayoutBreakpoint = 1120;
    private const double CompactLayoutBreakpoint = 1280;
    private const double NormalWorkspaceMinWidth = 480;
    private const double CompactWorkspaceMinWidth = 520;
    private const double MinimumWorkspaceMinWidth = 560;
    private const double NormalCommandPanelMinWidth = 480;
    private const double CompactCommandPanelMinWidth = 440;
    private const double MinimumCommandPanelMinWidth = 420;
    private const double CommandPanelMaxWidth = 620;
    private const double NormalDevicePanelMinWidth = 150;
    private const double MinimumDevicePanelMinWidth = 160;
    private const double NormalDevicePanelWidth = 232;
    private const double CompactDevicePanelWidth = 190;
    private const double MinimumDevicePanelWidth = 170;
    private const double SplitterWidth = 5;
    private const double TerminalTimeColumnMinWidth = 60;
    private const double TerminalDirectionColumnMinWidth = 84;
    private const double TerminalEndpointColumnMinWidth = 70;
    private const double TerminalSizeColumnMinWidth = 38;
    private const double TerminalContentColumnMinWidth = 140;
    private const double FrameTimeColumnMinWidth = 60;
    private const double FrameLengthColumnMinWidth = 42;
    private const double FrameHexColumnMinWidth = 100;
    private const double FrameSummaryColumnMinWidth = 110;
    private const double QuickCommandWheelPixelsPerDetent = 48;
    private static readonly TimeSpan ViewModelShutdownTimeout = TimeSpan.FromSeconds(3);
    private static readonly Duration QuickCommandEditorTransitionDuration = new(TimeSpan.FromMilliseconds(150));
    private static readonly Duration ThemeWipeDuration = new(TimeSpan.FromMilliseconds(260));
    private static readonly Duration ThemeRevealDuration = new(TimeSpan.FromMilliseconds(100));
    private const double QuickCommandEditorMinHeight = 210;
    private const double QuickCommandEditorMaxHeight = 360;

    private readonly MainWindowViewModel _viewModel;
    private readonly DataLogger _chartLogger;
    private readonly DispatcherTimer _chartRefreshTimer;
    private readonly DispatcherTimer _quickCommandScrollTimer;
    private readonly List<double> _chartValues = [];
    private bool _chartDirty;
    private bool _closing;
    private bool _closeApproved;
    private bool _deviceDesiredOpen;
    private bool _commandDesiredOpen = true;
    private SidebarFocus _sidebarFocus;
    private GridLength _devicePanelExpandedWidth = new(232);
    private GridLength _commandPanelExpandedWidth = new(500);
    private Point _quickCommandDragStartPoint;
    private QuickCommandItemViewModel? _quickCommandDragSource;
    private int? _quickCommandDropInsertionIndex;
    private ScrollViewer? _quickCommandScrollViewer;
    private double _quickCommandScrollTarget;
    private bool _quickCommandScrollDirty;
    private bool _quickCommandEditorExpanded;
    private int _quickCommandEditorAnimationGeneration;
    private bool _terminalColumnDragActive;
    private double _terminalViewportWidth;
    private int _terminalAutoScrollGeneration;
    private bool _terminalAutoScrollSuspended;
    private int _terminalPlainTextCharacterCount;
    private bool _frameColumnDragActive;
    private double _frameViewportWidth;
    private bool _updatePromptVisible;
    private ComboBox? _comboBoxPendingOpen;
    private ComboBox? _openComboBox;
    private bool _themeTransitionActive;
    private bool _isCompactLayout;
    private bool _isMinimumLayout;

    private double CurrentCommandPanelMinWidth =>
        _isMinimumLayout
            ? MinimumCommandPanelMinWidth
            : _isCompactLayout ? CompactCommandPanelMinWidth : NormalCommandPanelMinWidth;

    private double CurrentWorkspaceMinWidth =>
        _isMinimumLayout
            ? MinimumWorkspaceMinWidth
            : _isCompactLayout ? CompactWorkspaceMinWidth : NormalWorkspaceMinWidth;

    private double CurrentDevicePanelMaxWidth =>
        _isMinimumLayout
            ? MinimumDevicePanelWidth
            : _isCompactLayout ? CompactDevicePanelWidth : double.PositiveInfinity;

    private double CurrentDevicePanelMinWidth =>
        _isMinimumLayout ? MinimumDevicePanelMinWidth : NormalDevicePanelMinWidth;

    private enum SidebarFocus
    {
        None,
        Device,
        Command
    }

    public MainWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Title = App.MainWindowTitle;
        AddHandler(
            Mouse.PreviewMouseDownEvent,
            new MouseButtonEventHandler(OnComboBoxPreviewMouseDown),
            true);
        AddHandler(
            Mouse.PreviewMouseUpEvent,
            new MouseButtonEventHandler(OnComboBoxPreviewMouseUp),
            true);
        Loaded += OnMainWindowLoaded;
        TerminalList.AddHandler(
            Thumb.DragDeltaEvent,
            new DragDeltaEventHandler(OnTerminalColumnHeaderDragDelta),
            true);
        TerminalList.AddHandler(
            Thumb.DragCompletedEvent,
            new DragCompletedEventHandler(OnTerminalColumnHeaderDragCompleted),
            true);
        TerminalList.AddHandler(
            ScrollBar.PreviewMouseDownEvent,
            new MouseButtonEventHandler(OnTerminalScrollBarPreviewMouseDown),
            true);
        FrameList.AddHandler(
            Thumb.DragDeltaEvent,
            new DragDeltaEventHandler(OnFrameColumnHeaderDragDelta),
            true);
        FrameList.AddHandler(
            Thumb.DragCompletedEvent,
            new DragCompletedEventHandler(OnFrameColumnHeaderDragCompleted),
            true);

        _quickCommandScrollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _quickCommandScrollTimer.Tick += OnQuickCommandScrollTimerTick;

        _chartLogger = RealtimePlot.Plot.Add.DataLogger();
        _chartLogger.ViewSlide(240);
        RealtimePlot.Plot.Axes.AutoScale();
        _chartRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _chartRefreshTimer.Tick += (_, _) =>
        {
            if (!_chartDirty)
            {
                return;
            }
            _chartLogger.ViewSlide(240);
            RealtimePlot.Refresh();
            _chartDirty = false;
        };
        _chartRefreshTimer.Start();

        _viewModel.ChartValueAdded += OnChartValueAdded;
        _viewModel.RecordsAppended += OnRecordsAppended;
        _viewModel.TerminalRecords.CollectionChanged += OnTerminalRecordsCollectionChanged;
        _viewModel.FrameRecords.CollectionChanged += OnFrameRecordsCollectionChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.QuickCommandAdded += OnQuickCommandAdded;
        _viewModel.UpdateAvailable += OnUpdateAvailable;
        UpdateTerminalColumnWidths();
        UpdateFrameColumnWidths();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        App.ApplyWindowTitleBarTheme(this, ApplicationThemeManager.GetAppTheme());
    }

    private void OnComboBoxPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        ComboBox? comboBox = FindComboBoxAncestor(source);
        if (comboBox is null
            || !comboBox.IsEnabled
            || comboBox.IsDropDownOpen
            || comboBox.IsEditable && IsInsideEditableTextBox(source, comboBox))
        {
            return;
        }

        _comboBoxPendingOpen = comboBox;
        comboBox.Focus();
        e.Handled = true;
    }

    private void OnComboBoxPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        ComboBox? comboBox = _comboBoxPendingOpen;
        _comboBoxPendingOpen = null;
        if (e.ChangedButton != MouseButton.Left || comboBox is null)
        {
            return;
        }

        e.Handled = true;
        if (!comboBox.IsEnabled || !comboBox.IsMouseOver)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (comboBox.IsEnabled && comboBox.IsVisible)
            {
                PositionComboBoxPopup(comboBox);
                TrackOpenComboBox(comboBox);
                comboBox.IsDropDownOpen = true;
            }
        }));
    }

    private void TrackOpenComboBox(ComboBox comboBox)
    {
        if (ReferenceEquals(_openComboBox, comboBox))
        {
            return;
        }

        if (_openComboBox is not null)
        {
            _openComboBox.DropDownClosed -= OnTrackedComboBoxDropDownClosed;
        }

        _openComboBox = comboBox;
        comboBox.DropDownClosed += OnTrackedComboBoxDropDownClosed;
    }

    private void OnTrackedComboBoxDropDownClosed(object? sender, EventArgs e)
    {
        if (sender is not ComboBox comboBox || !ReferenceEquals(_openComboBox, comboBox))
        {
            return;
        }

        comboBox.DropDownClosed -= OnTrackedComboBoxDropDownClosed;
        _openComboBox = null;
    }

    private void CloseOpenComboBox()
    {
        _comboBoxPendingOpen = null;
        ComboBox? comboBox = _openComboBox;
        _openComboBox = null;
        if (comboBox is null)
        {
            return;
        }

        comboBox.DropDownClosed -= OnTrackedComboBoxDropDownClosed;
        comboBox.IsDropDownOpen = false;
    }

    private static void PositionComboBoxPopup(ComboBox comboBox)
    {
        if (!comboBox.IsVisible)
        {
            return;
        }

        comboBox.ApplyTemplate();
        Popup? popup = comboBox.Template.FindName("Popup", comboBox) as Popup
            ?? comboBox.Template.FindName("PART_Popup", comboBox) as Popup
            ?? FindVisualChild<Popup>(comboBox);
        Point controlTopLeft = comboBox.PointToScreen(new Point(0, 0));
        Point controlBottomRight = comboBox.PointToScreen(new Point(comboBox.ActualWidth, comboBox.ActualHeight));
        System.Windows.Forms.Screen screen = System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point((int)Math.Round(controlTopLeft.X), (int)Math.Round(controlTopLeft.Y)));
        System.Drawing.Rectangle workArea = screen.WorkingArea;
        double scaleY = comboBox.ActualHeight > 0
            ? Math.Max(1, (controlBottomRight.Y - controlTopLeft.Y) / comboBox.ActualHeight)
            : 1;
        double estimatedHeight = Math.Min(
            comboBox.MaxDropDownHeight,
            comboBox.Items.Count * Math.Max(comboBox.ActualHeight, 32) + 8);
        double contentHeight = popup?.Child is FrameworkElement popupContent
            ? Math.Max(popupContent.ActualHeight, popupContent.DesiredSize.Height)
            : 0;
        double desiredHeight = Math.Max(contentHeight, estimatedHeight) * scaleY;
        double availableBelow = workArea.Bottom - controlBottomRight.Y;
        double availableAbove = controlTopLeft.Y - workArea.Top;
        PlacementMode placement = availableBelow < desiredHeight && availableAbove > availableBelow
            ? PlacementMode.Top
            : PlacementMode.Bottom;

        comboBox.SetCurrentValue(Popup.PlacementProperty, placement);
        if (popup is not null)
        {
            // 在打开前使用淡入淡出，避免默认滑动与自定义上下定位同时发生。
            popup.SetCurrentValue(Popup.AllowsTransparencyProperty, true);
            popup.SetCurrentValue(Popup.PopupAnimationProperty, PopupAnimation.Fade);
            popup.HorizontalOffset = 0;
            popup.VerticalOffset = 0;
            popup.Placement = placement;
        }
    }

    private static ComboBox? FindComboBoxAncestor(DependencyObject source)
    {
        for (DependencyObject? current = source; current is not null; current = GetVisualOrLogicalParent(current))
        {
            if (current is ComboBox comboBox)
            {
                return comboBox;
            }
        }

        return null;
    }

    private static bool IsInsideEditableTextBox(DependencyObject source, ComboBox comboBox)
    {
        for (DependencyObject? current = source;
             current is not null && !ReferenceEquals(current, comboBox);
             current = GetVisualOrLogicalParent(current))
        {
            if (current is TextBox)
            {
                return true;
            }
        }

        return false;
    }

    private static DependencyObject? GetVisualOrLogicalParent(DependencyObject source) =>
        source is Visual
            ? VisualTreeHelper.GetParent(source)
            : LogicalTreeHelper.GetParent(source);

    protected override void OnClosed(EventArgs e)
    {
        Loaded -= OnMainWindowLoaded;
        _quickCommandScrollTimer.Stop();
        _quickCommandScrollTimer.Tick -= OnQuickCommandScrollTimerTick;
        _viewModel.QuickCommandAdded -= OnQuickCommandAdded;
        _viewModel.UpdateAvailable -= OnUpdateAvailable;
        _viewModel.FrameRecords.CollectionChanged -= OnFrameRecordsCollectionChanged;
        base.OnClosed(e);
        bool hasVisibleWindow = Application.Current.Windows
            .OfType<MainWindow>()
            .Any(window => window.IsVisible);
        if (!Application.Current.Dispatcher.HasShutdownStarted && !hasVisibleWindow)
        {
            Application.Current.Shutdown();
        }
    }

    private void OnChartValueAdded(double value)
    {
        _chartValues.Add(value);
        _chartLogger.Add(value);
        _chartDirty = true;
    }

    private void OnRecordsAppended(int count)
    {
        AppendTerminalPlainText(count);
        QueueTerminalAutoScroll();
    }

    private void OnTerminalDisplaySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, TerminalDisplayTabs) || !_viewModel.AutoScroll)
        {
            return;
        }

        QueueTerminalAutoScroll();
    }

    private void QueueTerminalAutoScroll()
    {
        if (_terminalAutoScrollSuspended)
        {
            return;
        }

        int generation = ++_terminalAutoScrollGeneration;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (generation != _terminalAutoScrollGeneration || !_viewModel.AutoScroll || _terminalAutoScrollSuspended)
            {
                return;
            }

            if (TerminalDisplayTabs.SelectedItem == TerminalTableTab && TerminalList.Items.Count > 0)
            {
                ScrollViewer? viewer = FindVisualChild<ScrollViewer>(TerminalList);
                if (viewer is not null)
                {
                    viewer.ScrollToVerticalOffset(viewer.ScrollableHeight);
                }
            }
            else if (TerminalDisplayTabs.SelectedItem == TerminalPlainTextTab)
            {
                if (!TerminalPlainTextBox.Selection.IsEmpty)
                {
                    return;
                }
                TerminalPlainTextBox.CaretPosition = TerminalPlainTextBox.Document.ContentEnd;
                TerminalPlainTextBox.ScrollToEnd();
            }
        }));
    }

    private void OnTerminalListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTerminalColumnWidths();
    }

    private void OnTerminalScrollBarPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _terminalAutoScrollSuspended = true;
        _terminalAutoScrollGeneration++;
    }

    private void OnTerminalListScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        ScrollViewer? viewer = e.OriginalSource as ScrollViewer ?? FindVisualChild<ScrollViewer>(TerminalList);
        if (viewer is not null && Math.Abs(e.ExtentHeightChange) < 0.01)
        {
            bool atBottom = viewer.VerticalOffset >= viewer.ScrollableHeight - 2;
            _terminalAutoScrollSuspended = !atBottom;
            if (_terminalAutoScrollSuspended)
            {
                _terminalAutoScrollGeneration++;
            }
        }

        double viewportWidth = GetTerminalViewportWidth();
        if (viewportWidth > 0 && Math.Abs(viewportWidth - _terminalViewportWidth) > 0.1)
        {
            UpdateTerminalColumnWidths(viewportWidth);
        }
    }

    private void OnFrameListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateFrameColumnWidths();
    }

    private void OnFrameListScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        double viewportWidth = GetFrameViewportWidth();
        if (viewportWidth > 0 && Math.Abs(viewportWidth - _frameViewportWidth) > 0.1)
        {
            UpdateFrameColumnWidths(viewportWidth);
        }
    }

    private void OnFrameColumnHeaderDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (e.OriginalSource is not Thumb thumb
            || FindVisualParent<GridViewColumnHeader>(thumb)?.Column is not GridViewColumn column
            || !TryGetFrameColumnMinimumWidth(column, out double minimumWidth))
        {
            return;
        }

        _frameColumnDragActive = true;
        double timeMinimumWidth = GetFrameColumnMinimumWidth(FrameTimeColumn);
        double lengthMinimumWidth = GetFrameColumnMinimumWidth(FrameLengthColumn);
        double hexMinimumWidth = GetFrameColumnMinimumWidth(FrameHexColumn);
        double summaryMinimumWidth = FrameSummaryColumnMinWidth;
        FrameTimeColumn.Width = Math.Max(timeMinimumWidth, FrameTimeColumn.ActualWidth);
        FrameLengthColumn.Width = Math.Max(lengthMinimumWidth, FrameLengthColumn.ActualWidth);
        FrameHexColumn.Width = Math.Max(hexMinimumWidth, FrameHexColumn.ActualWidth);
        FrameSummaryColumn.Width = Math.Max(summaryMinimumWidth, FrameSummaryColumn.ActualWidth);

        double viewportWidth = GetFrameViewportWidth();
        double otherWidth = FrameTimeColumn.Width
            + FrameLengthColumn.Width
            + FrameHexColumn.Width
            + FrameSummaryColumn.Width
            - column.Width;
        double reservedMinimum = ReferenceEquals(column, FrameSummaryColumn)
            ? 0
            : summaryMinimumWidth;
        double currentWidth = column.Width > 0 ? column.Width : column.ActualWidth;
        double maximumWidth = Math.Max(minimumWidth, viewportWidth - otherWidth - reservedMinimum);
        column.Width = Math.Clamp(currentWidth + e.HorizontalChange, minimumWidth, maximumWidth);
        FitFrameSummaryColumn(viewportWidth);
    }

    private void OnFrameColumnHeaderDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!_frameColumnDragActive)
        {
            return;
        }

        _frameColumnDragActive = false;
        FitFrameSummaryColumn(GetFrameViewportWidth());
        _viewModel.SaveFrameColumnWidths(
            FrameTimeColumn.Width,
            FrameLengthColumn.Width,
            FrameHexColumn.Width,
            FrameSummaryColumn.Width);
    }

    private bool TryGetFrameColumnMinimumWidth(GridViewColumn column, out double minimumWidth)
    {
        minimumWidth = GetFrameColumnMinimumWidth(column);
        return minimumWidth > 0;
    }

    private double GetFrameColumnMinimumWidth(GridViewColumn column) =>
        ReferenceEquals(column, FrameTimeColumn)
            ? GetFrameTextMinimumWidth("时间", item => item.TimeText, FrameTimeColumnMinWidth)
                : ReferenceEquals(column, FrameLengthColumn)
                    ? GetFrameTextMinimumWidth("字节", item => item.Length.ToString(), FrameLengthColumnMinWidth)
                : ReferenceEquals(column, FrameHexColumn)
                    ? FrameHexColumnMinWidth
                    : ReferenceEquals(column, FrameSummaryColumn)
                        ? GetFrameTextMinimumWidth("字段 / 解析", item => item.Summary, FrameSummaryColumnMinWidth)
                        : 0;

    private double GetFrameTextMinimumWidth(
        string header,
        Func<FrameRecordItem, string> valueSelector,
        double hardMinimumWidth)
    {
        int maximumLength = header.Length;
        int firstIndex = Math.Max(0, _viewModel.FrameRecords.Count - 64);
        for (int index = firstIndex; index < _viewModel.FrameRecords.Count; index++)
        {
            maximumLength = Math.Max(maximumLength, valueSelector(_viewModel.FrameRecords[index]).Length);
        }

        double characterWidth = Math.Max(6, _viewModel.TerminalFontSize * 0.62);
        return Math.Max(hardMinimumWidth, Math.Min(520, maximumLength * characterWidth + 16));
    }

    private void OnTerminalColumnHeaderDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (e.OriginalSource is not Thumb thumb
            || FindVisualParent<GridViewColumnHeader>(thumb)?.Column is not GridViewColumn column
            || !TryGetTerminalColumnMinimumWidth(column, out double minimumWidth))
        {
            return;
        }

        _terminalColumnDragActive = true;
        double timeMinimumWidth = GetTerminalColumnMinimumWidth(TerminalTimeColumn);
        double directionMinimumWidth = GetTerminalColumnMinimumWidth(TerminalDirectionColumn);
        double endpointMinimumWidth = GetTerminalColumnMinimumWidth(TerminalEndpointColumn);
        double sizeMinimumWidth = GetTerminalColumnMinimumWidth(TerminalSizeColumn);
        TerminalTimeColumn.Width = Math.Max(timeMinimumWidth, TerminalTimeColumn.ActualWidth);
        TerminalDirectionColumn.Width = Math.Max(directionMinimumWidth, TerminalDirectionColumn.ActualWidth);
        TerminalEndpointColumn.Width = Math.Max(endpointMinimumWidth, TerminalEndpointColumn.ActualWidth);
        TerminalSizeColumn.Width = Math.Max(sizeMinimumWidth, TerminalSizeColumn.ActualWidth);

        double viewportWidth = GetTerminalViewportWidth();
        double otherMetadataWidth = TerminalTimeColumn.Width
            + TerminalDirectionColumn.Width
            + TerminalEndpointColumn.Width
            + TerminalSizeColumn.Width
            - column.Width;
        double maximumWidth = Math.Max(
            minimumWidth,
            viewportWidth - TerminalContentColumnMinWidth - otherMetadataWidth);
        column.Width = Math.Clamp(column.ActualWidth, minimumWidth, maximumWidth);
        FitTerminalContentColumn(viewportWidth);
    }

    private void OnTerminalColumnHeaderDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!_terminalColumnDragActive)
        {
            return;
        }

        _terminalColumnDragActive = false;
        FitTerminalContentColumn(GetTerminalViewportWidth());
        _viewModel.SaveTerminalColumnWidths(
            TerminalTimeColumn.Width,
            TerminalDirectionColumn.Width,
            TerminalEndpointColumn.Width,
            TerminalSizeColumn.Width,
            TerminalContentColumn.Width);
    }

    private bool TryGetTerminalColumnMinimumWidth(GridViewColumn column, out double minimumWidth)
    {
        minimumWidth = GetTerminalColumnMinimumWidth(column);
        return minimumWidth > 0;
    }

    private double GetTerminalColumnMinimumWidth(GridViewColumn column) =>
        ReferenceEquals(column, TerminalTimeColumn)
            ? GetTerminalTextMinimumWidth("时间", item => item.TimeText, TerminalTimeColumnMinWidth)
            : ReferenceEquals(column, TerminalDirectionColumn)
                ? GetTerminalTextMinimumWidth("方向", item => item.DirectionText, TerminalDirectionColumnMinWidth)
                : ReferenceEquals(column, TerminalEndpointColumn)
                    ? GetTerminalTextMinimumWidth("端点", item => item.Endpoint, TerminalEndpointColumnMinWidth)
                    : ReferenceEquals(column, TerminalSizeColumn)
                        ? GetTerminalTextMinimumWidth("字节", item => item.Size.ToString(), TerminalSizeColumnMinWidth)
                        : 0;

    private double GetTerminalTextMinimumWidth(
        string header,
        Func<TerminalRecordItem, string> valueSelector,
        double hardMinimumWidth)
    {
        int maximumLength = header.Length;
        int firstIndex = Math.Max(0, _viewModel.TerminalRecords.Count - 64);
        for (int index = firstIndex; index < _viewModel.TerminalRecords.Count; index++)
        {
            maximumLength = Math.Max(maximumLength, valueSelector(_viewModel.TerminalRecords[index]).Length);
        }

        double characterWidth = Math.Max(6, _viewModel.TerminalFontSize * 0.62);
        return Math.Max(hardMinimumWidth, Math.Min(260, maximumLength * characterWidth + 16));
    }

    private void AppendTerminalPlainText(int count)
    {
        int actualCount = Math.Min(Math.Max(0, count), _viewModel.TerminalRecords.Count);
        if (actualCount == 0)
        {
            return;
        }

        bool hasSelection = !TerminalPlainTextBox.Selection.IsEmpty;
        int start = _viewModel.TerminalRecords.Count - actualCount;
        AppendTerminalPlainTextEntries(BuildTerminalPlainTextEntries(start, actualCount));
        if (_terminalPlainTextCharacterCount > 4_000_000)
        {
            RebuildTerminalPlainText(3_000_000);
            hasSelection = false;
        }

        if (!hasSelection && _viewModel.AutoScroll)
        {
            TerminalPlainTextBox.ScrollToEnd();
        }
    }

    private List<(string Text, bool IsSeparator)> BuildTerminalPlainTextEntries(int start, int count)
    {
        List<(string Text, bool IsSeparator)> entries = new(count);
        int end = Math.Min(_viewModel.TerminalRecords.Count, start + count);
        for (int index = Math.Max(0, start); index < end; index++)
        {
            TerminalRecordItem item = _viewModel.TerminalRecords[index];
            string text = FormatTerminalPlainTextRecord(item);
            if (text.Length > 0)
            {
                entries.Add((text, item.IsSeparator));
            }
        }
        return entries;
    }

    private string FormatTerminalPlainTextRecord(TerminalRecordItem item)
    {
        if (IsConnectionStatusRecord(item) || !_viewModel.IsTerminalRecordVisible(item))
        {
            return string.Empty;
        }
        if (item.IsSeparator)
        {
            return _viewModel.BuildTerminalSeparatorLine(item.SeparatorGapMilliseconds) + Environment.NewLine;
        }

        string direction = item.Direction switch
        {
            PacketDirection.Send => "发→◇",
            PacketDirection.Receive => "收←◆",
            _ => item.DirectionText
        };
        string prefix = $"[{item.TimeText}]{direction}";
        StringBuilder builder = new(prefix.Length + item.Content.Length + 16);
        builder.Append(prefix);
        string content = item.GetContinuousTextContent(_viewModel.ReceiveAsHex);
        AppendAlignedContinuousContent(builder, content, prefix);
        if (!content.EndsWith('\r') && !content.EndsWith('\n'))
        {
            builder.AppendLine();
        }
        return builder.ToString();
    }

    private void AppendTerminalPlainTextEntries(IEnumerable<(string Text, bool IsSeparator)> entries)
    {
        Paragraph paragraph = GetTerminalPlainTextParagraph();
        Brush terminalBrush = (Brush)FindResource("TerminalTextBrush");
        Brush separatorBrush = (Brush)FindResource("TerminalSeparatorBrush");
        StringBuilder terminalText = new();
        foreach ((string text, bool isSeparator) in entries)
        {
            if (isSeparator)
            {
                AppendTerminalRun(paragraph, terminalText, terminalBrush);
                paragraph.Inlines.Add(new Run(text) { Foreground = separatorBrush });
                _terminalPlainTextCharacterCount += text.Length;
            }
            else
            {
                terminalText.Append(text);
            }
        }
        AppendTerminalRun(paragraph, terminalText, terminalBrush);
    }

    private void AppendTerminalRun(Paragraph paragraph, StringBuilder builder, Brush foreground)
    {
        if (builder.Length == 0)
        {
            return;
        }

        string text = builder.ToString();
        paragraph.Inlines.Add(new Run(text) { Foreground = foreground });
        _terminalPlainTextCharacterCount += text.Length;
        builder.Clear();
    }

    private Paragraph GetTerminalPlainTextParagraph()
    {
        if (TerminalPlainTextBox.Document.Blocks.LastBlock is Paragraph paragraph)
        {
            return paragraph;
        }

        paragraph = new Paragraph { Margin = new Thickness(0) };
        TerminalPlainTextBox.Document.Blocks.Add(paragraph);
        return paragraph;
    }

    private void ResetTerminalPlainTextDocument()
    {
        TerminalPlainTextBox.Document.Blocks.Clear();
        TerminalPlainTextBox.Document.Blocks.Add(new Paragraph { Margin = new Thickness(0) });
        _terminalPlainTextCharacterCount = 0;
    }

    private void RebuildTerminalPlainText(int maximumCharacters = int.MaxValue)
    {
        List<(string Text, bool IsSeparator)> entries = [];
        int retainedCharacters = 0;
        for (int index = _viewModel.TerminalRecords.Count - 1; index >= 0; index--)
        {
            TerminalRecordItem item = _viewModel.TerminalRecords[index];
            string text = FormatTerminalPlainTextRecord(item);
            if (text.Length == 0)
            {
                continue;
            }
            if (retainedCharacters > 0 && retainedCharacters + text.Length > maximumCharacters)
            {
                break;
            }
            entries.Add((text, item.IsSeparator));
            retainedCharacters += text.Length;
        }
        entries.Reverse();
        ResetTerminalPlainTextDocument();
        AppendTerminalPlainTextEntries(entries);
    }

    private static bool IsConnectionStatusRecord(TerminalRecordItem item) =>
        item.Direction == PacketDirection.Information
        && item.Content.Contains("连接状态：", StringComparison.Ordinal);

    private static void AppendAlignedContinuousContent(StringBuilder builder, string content, string continuationPrefix)
    {
        for (int index = 0; index < content.Length; index++)
        {
            char current = content[index];
            builder.Append(current);
            if (current == '\r' && index + 1 < content.Length && content[index + 1] == '\n')
            {
                builder.Append('\n');
                index++;
            }

            if ((current == '\r' || current == '\n') && index + 1 < content.Length)
            {
                builder.Append(continuationPrefix);
            }
        }
    }

    private void OnTerminalRecordsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset && _viewModel.TerminalRecords.Count == 0)
        {
            ResetTerminalPlainTextDocument();
        }
    }

    private void OnFrameRecordsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 帧记录批量到达时只更新集合，列宽由窗口尺寸/字体变化处理，避免每批数据触发整表布局。
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.AutoScroll))
        {
            if (_viewModel.AutoScroll)
            {
                _terminalAutoScrollSuspended = false;
                QueueTerminalAutoScroll();
            }
            else
            {
                _terminalAutoScrollGeneration++;
            }
        }
        else if (e.PropertyName is nameof(MainWindowViewModel.ReceiveAsHex)
            or nameof(MainWindowViewModel.SearchText)
            or nameof(MainWindowViewModel.TerminalSeparatorEnabled)
            or nameof(MainWindowViewModel.SelectedTerminalSeparatorStyle))
        {
            RebuildTerminalPlainText();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.TerminalFontSize))
        {
            UpdateTerminalColumnWidths();
            UpdateFrameColumnWidths();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.SelectedQuickCommand))
        {
            if (_viewModel.SelectedQuickCommand is null)
            {
                CollapseQuickCommandEditor();
            }
            else if (_quickCommandEditorExpanded)
            {
                _viewModel.SelectedQuickCommand.IsExpanded = true;
            }
            else if (_viewModel.SelectedQuickCommand.IsExpanded)
            {
                _viewModel.SelectedQuickCommand.IsExpanded = false;
            }
        }
    }

    private double GetTerminalViewportWidth()
    {
        ScrollContentPresenter? presenter = FindVisualChild<ScrollContentPresenter>(TerminalList);
        double viewportWidth = presenter is { ActualWidth: > 0 }
            ? presenter.ActualWidth
            : TerminalList.ActualWidth;
        return viewportWidth > 0 && double.IsFinite(viewportWidth) ? viewportWidth : 0;
    }

    private double GetFrameViewportWidth()
    {
        ScrollContentPresenter? presenter = FindVisualChild<ScrollContentPresenter>(FrameList);
        double viewportWidth = presenter is { ActualWidth: > 0 }
            ? presenter.ActualWidth
            : FrameList.ActualWidth;
        return viewportWidth > 0 && double.IsFinite(viewportWidth) ? viewportWidth : 0;
    }

    private void FitTerminalContentColumn(double viewportWidth)
    {
        if (viewportWidth <= 0)
        {
            return;
        }

        _terminalViewportWidth = viewportWidth;
        double metadataWidth = TerminalTimeColumn.Width
            + TerminalDirectionColumn.Width
            + TerminalEndpointColumn.Width
            + TerminalSizeColumn.Width;
        TerminalContentColumn.Width = Math.Max(TerminalContentColumnMinWidth, viewportWidth - metadataWidth);
    }

    private void UpdateTerminalColumnWidths(double? measuredViewportWidth = null)
    {
        double timeMinimumWidth = GetTerminalColumnMinimumWidth(TerminalTimeColumn);
        double directionMinimumWidth = GetTerminalColumnMinimumWidth(TerminalDirectionColumn);
        double endpointMinimumWidth = GetTerminalColumnMinimumWidth(TerminalEndpointColumn);
        double sizeMinimumWidth = GetTerminalColumnMinimumWidth(TerminalSizeColumn);
        double baseTimeWidth = _viewModel.TerminalTimeColumnWidth;
        double baseDirectionWidth = _viewModel.TerminalDirectionColumnWidth;
        double baseEndpointWidth = _viewModel.TerminalEndpointColumnWidth;
        double baseSizeWidth = _viewModel.TerminalSizeColumnWidth;
        double baseContentWidth = _viewModel.TerminalContentColumnWidth;
        double baseTotalWidth = baseTimeWidth
            + baseDirectionWidth
            + baseEndpointWidth
            + baseSizeWidth
            + baseContentWidth;

        double viewportWidth = measuredViewportWidth ?? GetTerminalViewportWidth();
        if (viewportWidth <= 0)
        {
            viewportWidth = baseTotalWidth;
        }
        _terminalViewportWidth = viewportWidth;

        if (_terminalColumnDragActive)
        {
            FitTerminalContentColumn(viewportWidth);
            return;
        }

        double scale = viewportWidth / baseTotalWidth;
        double timeWidth = Math.Max(timeMinimumWidth, baseTimeWidth * scale);
        double directionWidth = Math.Max(directionMinimumWidth, baseDirectionWidth * scale);
        double endpointWidth = Math.Max(endpointMinimumWidth, baseEndpointWidth * scale);
        double sizeWidth = Math.Max(sizeMinimumWidth, baseSizeWidth * scale);
        double contentWidth = Math.Max(TerminalContentColumnMinWidth, baseContentWidth * scale);

        double totalWidth = timeWidth + directionWidth + endpointWidth + sizeWidth + contentWidth;
        double overflow = Math.Max(0, totalWidth - viewportWidth);
        double contentReduction = Math.Min(overflow, contentWidth - TerminalContentColumnMinWidth);
        contentWidth -= contentReduction;
        overflow -= contentReduction;

        if (overflow > 0)
        {
            double flexibleMetadataWidth = timeWidth - timeMinimumWidth
                + directionWidth - directionMinimumWidth
                + endpointWidth - endpointMinimumWidth
                + sizeWidth - sizeMinimumWidth;
            if (flexibleMetadataWidth > 0)
            {
                double reductionRatio = Math.Min(1, overflow / flexibleMetadataWidth);
                timeWidth -= (timeWidth - timeMinimumWidth) * reductionRatio;
                directionWidth -= (directionWidth - directionMinimumWidth) * reductionRatio;
                endpointWidth -= (endpointWidth - endpointMinimumWidth) * reductionRatio;
                sizeWidth -= (sizeWidth - sizeMinimumWidth) * reductionRatio;
            }
        }

        double fittedWidth = timeWidth + directionWidth + endpointWidth + sizeWidth + contentWidth;
        contentWidth += Math.Max(0, viewportWidth - fittedWidth);

        TerminalTimeColumn.Width = timeWidth;
        TerminalDirectionColumn.Width = directionWidth;
        TerminalEndpointColumn.Width = endpointWidth;
        TerminalSizeColumn.Width = sizeWidth;
        TerminalContentColumn.Width = contentWidth;
    }

    private void FitFrameSummaryColumn(double viewportWidth)
    {
        if (viewportWidth <= 0)
        {
            return;
        }

        _frameViewportWidth = viewportWidth;
        double metadataWidth = FrameTimeColumn.Width
            + FrameLengthColumn.Width
            + FrameHexColumn.Width;
        FrameSummaryColumn.Width = Math.Max(
            FrameSummaryColumnMinWidth,
            viewportWidth - metadataWidth);
    }

    private void UpdateFrameColumnWidths(double? measuredViewportWidth = null)
    {
        double viewportWidth = measuredViewportWidth ?? GetFrameViewportWidth();
        double baseTimeWidth = _viewModel.FrameTimeColumnWidth;
        double baseLengthWidth = _viewModel.FrameLengthColumnWidth;
        double baseHexWidth = _viewModel.FrameHexColumnWidth;
        double baseSummaryWidth = _viewModel.FrameSummaryColumnWidth;
        double baseTotalWidth = baseTimeWidth + baseLengthWidth + baseHexWidth + baseSummaryWidth;
        if (viewportWidth <= 0)
        {
            viewportWidth = baseTotalWidth;
        }

        _frameViewportWidth = viewportWidth;
        if (_frameColumnDragActive)
        {
            FitFrameSummaryColumn(viewportWidth);
            return;
        }

        double timeMinimumWidth = GetFrameColumnMinimumWidth(FrameTimeColumn);
        double lengthMinimumWidth = GetFrameColumnMinimumWidth(FrameLengthColumn);
        double hexMinimumWidth = GetFrameColumnMinimumWidth(FrameHexColumn);
        double summaryMinimumWidth = FrameSummaryColumnMinWidth;
        double scale = viewportWidth / baseTotalWidth;
        double timeWidth = Math.Max(timeMinimumWidth, baseTimeWidth * scale);
        double lengthWidth = Math.Max(lengthMinimumWidth, baseLengthWidth * scale);
        double hexWidth = Math.Max(hexMinimumWidth, baseHexWidth * scale);
        double summaryWidth = Math.Max(summaryMinimumWidth, baseSummaryWidth * scale);

        double totalWidth = timeWidth + lengthWidth + hexWidth + summaryWidth;
        double overflow = Math.Max(0, totalWidth - viewportWidth);
        double summaryReduction = Math.Min(overflow, summaryWidth - summaryMinimumWidth);
        summaryWidth -= summaryReduction;
        overflow -= summaryReduction;

        if (overflow > 0)
        {
            double flexibleWidth = timeWidth - timeMinimumWidth
                + lengthWidth - lengthMinimumWidth
                + hexWidth - hexMinimumWidth;
            if (flexibleWidth > 0)
            {
                double reductionRatio = Math.Min(1, overflow / flexibleWidth);
                timeWidth -= (timeWidth - timeMinimumWidth) * reductionRatio;
                lengthWidth -= (lengthWidth - lengthMinimumWidth) * reductionRatio;
                hexWidth -= (hexWidth - hexMinimumWidth) * reductionRatio;
            }
        }

        double fittedWidth = timeWidth + lengthWidth + hexWidth + summaryWidth;
        summaryWidth += Math.Max(0, viewportWidth - fittedWidth);

        FrameTimeColumn.Width = timeWidth;
        FrameLengthColumn.Width = lengthWidth;
        FrameHexColumn.Width = hexWidth;
        FrameSummaryColumn.Width = summaryWidth;
    }

    private void OnTerminalPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        if (Keyboard.FocusedElement is TextBox textBox)
        {
            if (e.Key == Key.C && textBox.SelectionLength > 0)
            {
                return;
            }
            if (e.Key == Key.A)
            {
                return;
            }
        }

        if (e.Key == Key.A)
        {
            TerminalList.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.C)
        {
            CopySelectedTerminalRows();
            e.Handled = true;
        }
    }

    private void OnCopySelectedTerminalRowsClick(object sender, RoutedEventArgs e) => CopySelectedTerminalRows();

    private void OnSelectAllTerminalRowsClick(object sender, RoutedEventArgs e) => TerminalList.SelectAll();

    private void OnSelectAllTerminalTextClick(object sender, RoutedEventArgs e)
    {
        TerminalPlainTextBox.Focus();
        TerminalPlainTextBox.SelectAll();
    }

    private void OnQuickCommandPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? originalSource = e.OriginalSource as DependencyObject;
        ListBoxItem? container = ItemsControl.ContainerFromElement(QuickCommandsList, originalSource) as ListBoxItem;
        if (container?.DataContext is not QuickCommandItemViewModel clickedCommand)
        {
            _quickCommandDragSource = null;
            return;
        }

        Button? clickedButton = FindVisualParent<Button>(originalSource);
        if (clickedButton is { Name: "QuickCommandEditorToggleButton" })
        {
            _quickCommandDragSource = null;
            return;
        }

        QuickCommandsList.SelectedItem = clickedCommand;
        if (clickedButton is not null)
        {
            CommitQuickCommandEditorBindings(container);
            TextBox? payloadTextBox = FindNamedTextBox(container, "QuickCommandPayloadTextBox");
            if (payloadTextBox is not null
                && !string.Equals(clickedCommand.Payload, payloadTextBox.Text, StringComparison.Ordinal))
            {
                clickedCommand.Payload = payloadTextBox.Text;
            }
        }

        Button? dragHandle = GetQuickCommandDragHandle(originalSource);
        if (dragHandle?.DataContext is not QuickCommandItemViewModel source)
        {
            _quickCommandDragSource = null;
            return;
        }

        _quickCommandDragStartPoint = e.GetPosition(QuickCommandsList);
        _quickCommandDragSource = source;
        if (_viewModel.SelectedQuickCommandSort != "手动顺序")
        {
            _viewModel.SelectedQuickCommandSort = "手动顺序";
        }

        QuickCommandsList.SelectedItem = source;
        e.Handled = true;
    }

    private void OnQuickCommandAdded(QuickCommandItemViewModel command)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            SelectQuickCommand(command);
            if (QuickCommandsList.ItemContainerGenerator.ContainerFromItem(command) is not ListBoxItem container)
            {
                return;
            }

            TextBox? nameTextBox = FindNamedTextBox(container, "QuickCommandNameTextBox");
            if (nameTextBox is null)
            {
                return;
            }

            nameTextBox.Focus();
            nameTextBox.CaretIndex = nameTextBox.Text.Length;
            nameTextBox.SelectionLength = 0;
        }, DispatcherPriority.Loaded);
    }

    private void OnToggleQuickCommandEditorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: QuickCommandItemViewModel command })
        {
            return;
        }

        bool shouldCollapse = ReferenceEquals(_viewModel.SelectedQuickCommand, command)
            && _quickCommandEditorExpanded;
        QuickCommandsList.SelectedItem = command;
        if (shouldCollapse)
        {
            CollapseQuickCommandEditor();
        }
        else
        {
            ExpandQuickCommandEditor();
        }

        e.Handled = true;
    }

    private void OnCollapseQuickCommandEditorClick(object sender, RoutedEventArgs e) =>
        CollapseQuickCommandEditor();

    private void ExpandQuickCommandEditor(bool animate = true)
    {
        if (_viewModel.SelectedQuickCommand is not { } selectedCommand)
        {
            return;
        }

        selectedCommand.IsExpanded = true;
        _quickCommandEditorExpanded = true;
        int generation = ++_quickCommandEditorAnimationGeneration;
        double targetHeight = GetQuickCommandEditorTargetHeight();
        double startHeight = QuickCommandEditorHost.Visibility == Visibility.Visible
            ? Math.Max(0, QuickCommandEditorHost.ActualHeight)
            : 0;
        double startOpacity = QuickCommandEditorHost.Visibility == Visibility.Visible
            ? QuickCommandEditorHost.Opacity
            : 0;

        QuickCommandEditorHost.BeginAnimation(FrameworkElement.HeightProperty, null);
        QuickCommandEditorHost.BeginAnimation(UIElement.OpacityProperty, null);
        QuickCommandEditorHost.Height = startHeight;
        QuickCommandEditorHost.Opacity = startOpacity;
        QuickCommandEditorHost.Visibility = Visibility.Visible;
        if (!animate)
        {
            QuickCommandEditorHost.Height = targetHeight;
            QuickCommandEditorHost.Opacity = 1;
            return;
        }

        DoubleAnimation heightAnimation = new(startHeight, targetHeight, QuickCommandEditorTransitionDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        heightAnimation.Completed += (_, _) =>
        {
            if (generation != _quickCommandEditorAnimationGeneration || !_quickCommandEditorExpanded)
            {
                return;
            }

            QuickCommandEditorHost.BeginAnimation(FrameworkElement.HeightProperty, null);
            QuickCommandEditorHost.Height = targetHeight;
        };
        QuickCommandEditorHost.BeginAnimation(FrameworkElement.HeightProperty, heightAnimation);
        QuickCommandEditorHost.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(startOpacity, 1, QuickCommandEditorTransitionDuration));
    }

    private void CollapseQuickCommandEditor()
    {
        if (QuickCommandEditorHost.Visibility != Visibility.Visible)
        {
            _quickCommandEditorExpanded = false;
            if (_viewModel.SelectedQuickCommand is { } selectedCommand)
            {
                selectedCommand.IsExpanded = false;
            }
            return;
        }

        _quickCommandEditorExpanded = false;
        if (_viewModel.SelectedQuickCommand is { } expandedCommand)
        {
            expandedCommand.IsExpanded = false;
        }
        int generation = ++_quickCommandEditorAnimationGeneration;
        double startHeight = Math.Max(0, QuickCommandEditorHost.ActualHeight);
        QuickCommandEditorHost.BeginAnimation(FrameworkElement.HeightProperty, null);
        QuickCommandEditorHost.BeginAnimation(UIElement.OpacityProperty, null);
        QuickCommandEditorHost.Height = startHeight;
        DoubleAnimation heightAnimation = new(startHeight, 0, QuickCommandEditorTransitionDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        heightAnimation.Completed += (_, _) =>
        {
            if (generation != _quickCommandEditorAnimationGeneration || _quickCommandEditorExpanded)
            {
                return;
            }

            QuickCommandEditorHost.BeginAnimation(FrameworkElement.HeightProperty, null);
            QuickCommandEditorHost.BeginAnimation(UIElement.OpacityProperty, null);
            QuickCommandEditorHost.Height = 0;
            QuickCommandEditorHost.Opacity = 0;
            QuickCommandEditorHost.Visibility = Visibility.Collapsed;
        };
        QuickCommandEditorHost.BeginAnimation(FrameworkElement.HeightProperty, heightAnimation);
        QuickCommandEditorHost.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(QuickCommandEditorHost.Opacity, 0, QuickCommandEditorTransitionDuration));
    }

    private double GetQuickCommandEditorTargetHeight()
    {
        double availableWidth = QuickCommandEditorHost.ActualWidth;
        if (!(availableWidth > 0) || !double.IsFinite(availableWidth))
        {
            availableWidth = CommandPanelBorder.ActualWidth - 16;
        }

        availableWidth = Math.Max(0, availableWidth);
        Visibility previousVisibility = QuickCommandEditorHost.Visibility;
        double previousHeight = QuickCommandEditorHost.Height;

        // 临时解除折叠和固定高度，让 WPF 按编辑内容测量自然高度。
        QuickCommandEditorHost.Visibility = Visibility.Visible;
        QuickCommandEditorHost.Height = double.NaN;
        QuickCommandEditorHost.Measure(new Size(availableWidth, double.PositiveInfinity));
        double measuredHeight = QuickCommandEditorHost.DesiredSize.Height;

        QuickCommandEditorHost.Height = previousHeight;
        QuickCommandEditorHost.Visibility = previousVisibility;

        double maximumHeight = QuickCommandEditorMaxHeight;
        double panelHeight = CommandPanelBorder.ActualHeight;
        if (panelHeight > 0 && double.IsFinite(panelHeight))
        {
            maximumHeight = Math.Min(
                maximumHeight,
                Math.Max(QuickCommandEditorMinHeight, panelHeight - 96));
        }

        if (!(measuredHeight > 0) || !double.IsFinite(measuredHeight))
        {
            measuredHeight = 280;
        }

        return Math.Clamp(
            Math.Ceiling(measuredHeight),
            QuickCommandEditorMinHeight,
            maximumHeight);
    }

    private static TextBox? FindNamedTextBox(DependencyObject parent, string name)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is TextBox textBox && textBox.Name == name)
            {
                return textBox;
            }

            TextBox? nested = FindNamedTextBox(child, name);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static void CommitQuickCommandEditorBindings(DependencyObject parent)
    {
        if (parent is TextBox { IsReadOnly: false } textBox)
        {
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }

        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            CommitQuickCommandEditorBindings(VisualTreeHelper.GetChild(parent, index));
        }
    }

    private void OnQuickCommandsListPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ScrollViewer? outer = _quickCommandScrollViewer ??= FindVisualChild<ScrollViewer>(QuickCommandsList);
        if (outer is null)
        {
            return;
        }

        ScrollViewer? inner = FindVisualParent<ScrollViewer>(e.OriginalSource as DependencyObject);
        if (inner is not null
            && !ReferenceEquals(inner, outer)
            && CanScroll(inner, e.Delta))
        {
            return;
        }

        if (!_quickCommandScrollTimer.IsEnabled)
        {
            _quickCommandScrollTarget = outer.VerticalOffset;
        }

        double wheelDetents = e.Delta / 120.0;
        _quickCommandScrollTarget = Math.Clamp(
            _quickCommandScrollTarget - wheelDetents * QuickCommandWheelPixelsPerDetent,
            0,
            outer.ScrollableHeight);
        _quickCommandScrollDirty = true;
        _quickCommandScrollTimer.Start();
        e.Handled = true;
    }

    private void OnQuickCommandScrollTimerTick(object? sender, EventArgs e)
    {
        if (!_quickCommandScrollDirty)
        {
            _quickCommandScrollTimer.Stop();
            return;
        }

        _quickCommandScrollDirty = false;
        ScrollViewer? viewer = _quickCommandScrollViewer;
        if (viewer is null)
        {
            _quickCommandScrollTimer.Stop();
            return;
        }

        double target = Math.Clamp(_quickCommandScrollTarget, 0, viewer.ScrollableHeight);
        if (Math.Abs(viewer.VerticalOffset - target) >= 0.5)
        {
            viewer.ScrollToVerticalOffset(target);
        }
    }

    private void OnMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnMainWindowLoaded;
        UpdateResponsiveLayout(ActualWidth > 0 ? ActualWidth : Width);
        EnforcePanelLayout();
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(PrewarmBaudRateComboBox));
    }

    private void PrewarmBaudRateComboBox()
    {
        if (_closing || !BaudRateComboBox.IsLoaded)
        {
            return;
        }

        try
        {
            BaudRateComboBox.ApplyTemplate();
            if (BaudRateComboBox.IsDropDownOpen)
            {
                return;
            }

            // 在空闲时创建一次 Popup 和项目容器，避免用户首次展开时承担模板初始化成本。
            BaudRateComboBox.IsDropDownOpen = true;
            BaudRateComboBox.UpdateLayout();
            BaudRateComboBox.IsDropDownOpen = false;
        }
        catch (Exception exception)
        {
            Log.Debug(exception, "预热波特率下拉框失败");
        }
    }

    private static bool CanScroll(ScrollViewer viewer, int delta)
    {
        const double tolerance = 0.5;
        return delta > 0
            ? viewer.VerticalOffset > tolerance
            : viewer.VerticalOffset < viewer.ScrollableHeight - tolerance;
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            T? nested = FindVisualChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static T? FindVisualParent<T>(DependencyObject? element)
        where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match)
            {
                return match;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return null;
    }

    private void OnQuickCommandPayloadPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        if (e.ClickCount == 3)
        {
            textBox.Focus();
            textBox.SelectAll();
            e.Handled = true;
        }
    }

    private void OnQuickVariablePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        if (e.ClickCount == 3)
        {
            textBox.Focus();
            textBox.SelectAll();
            e.Handled = true;
        }
    }

    private void OnQuickCommandNamePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox textBox || textBox.IsKeyboardFocusWithin)
        {
            return;
        }

        textBox.Focus();
        textBox.CaretIndex = textBox.Text.Length;
        textBox.SelectionLength = 0;
        e.Handled = true;
    }

    private void OnQuickVariableSetPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox)
        {
            return;
        }

        ListBoxItem? container = ItemsControl.ContainerFromElement(
            listBox,
            e.OriginalSource as DependencyObject) as ListBoxItem;
        if (container?.DataContext is not QuickCommandVariableSetItemViewModel variableSet)
        {
            return;
        }

        listBox.SelectedItem = variableSet;
        if (e.ClickCount < 2)
        {
            if (!variableSet.IsRenaming)
            {
                foreach (QuickCommandVariableSetItemViewModel item in listBox.Items)
                {
                    item.IsRenaming = false;
                }
            }
            return;
        }

        foreach (QuickCommandVariableSetItemViewModel item in listBox.Items)
        {
            item.IsRenaming = false;
        }
        variableSet.IsRenaming = true;
        e.Handled = true;
        _ = Dispatcher.InvokeAsync(() =>
        {
            listBox.UpdateLayout();
            if (listBox.ItemContainerGenerator.ContainerFromItem(variableSet) is not ListBoxItem itemContainer)
            {
                return;
            }

            TextBox? textBox = FindNamedTextBox(itemContainer, "QuickVariableSetNameTextBox");
            if (textBox is null)
            {
                return;
            }
            textBox.Focus();
            textBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void OnQuickVariableSetNameLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: QuickCommandVariableSetItemViewModel variableSet })
        {
            variableSet.IsRenaming = false;
        }
    }

    private void OnQuickVariableSetNamePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape)
            || sender is not TextBox { DataContext: QuickCommandVariableSetItemViewModel variableSet })
        {
            return;
        }

        variableSet.IsRenaming = false;
        e.Handled = true;
    }

    private void OnQuickCommandPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed
            || _quickCommandDragSource is null)
        {
            return;
        }

        Point current = e.GetPosition(QuickCommandsList);
        if (Math.Abs(current.X - _quickCommandDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _quickCommandDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        QuickCommandItemViewModel source = _quickCommandDragSource;
        _quickCommandDragSource = null;
        QuickCommandsList.Tag = source;
        try
        {
            Mouse.OverrideCursor = Cursors.SizeAll;
            DragDrop.DoDragDrop(QuickCommandsList, source, DragDropEffects.Move);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            QuickCommandsList.Tag = null;
            ClearQuickCommandDropIndicators();
            _ = Dispatcher.InvokeAsync(
                () => SelectQuickCommand(source, forceRefresh: true),
                DispatcherPriority.Loaded);
        }
    }

    private void OnQuickCommandGiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        if (QuickCommandsList.Tag is not QuickCommandItemViewModel)
        {
            return;
        }

        e.UseDefaultCursors = false;
        Mouse.SetCursor(Cursors.SizeAll);
        e.Handled = true;
    }

    private void OnQuickCommandDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(QuickCommandItemViewModel)))
        {
            ClearQuickCommandDropIndicators();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        QuickCommandItemViewModel? source = e.Data.GetData(typeof(QuickCommandItemViewModel)) as QuickCommandItemViewModel;
        int? insertionIndex = GetQuickCommandInsertionIndex(e);
        if (source is null || insertionIndex is null)
        {
            ClearQuickCommandDropIndicators();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (!ReferenceEquals(QuickCommandsList.SelectedItem, source))
        {
            QuickCommandsList.SelectedItem = source;
        }
        ShowQuickCommandDropIndicator(insertionIndex.Value, source.IsPinned);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnQuickCommandDragLeave(object sender, DragEventArgs e)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!QuickCommandsList.IsMouseOver)
            {
                ClearQuickCommandDropIndicators();
            }
        }, DispatcherPriority.Input);
    }

    private void OnQuickCommandDrop(object sender, DragEventArgs e)
    {
        QuickCommandItemViewModel? source = e.Data.GetData(typeof(QuickCommandItemViewModel)) as QuickCommandItemViewModel;
        int? insertionIndex = GetQuickCommandInsertionIndex(e);
        if (source is null || insertionIndex is null)
        {
            ClearQuickCommandDropIndicators();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (source.IsPinned)
        {
            ReorderPinnedQuickCommand(source, insertionIndex.Value);
            SelectQuickCommand(source, forceRefresh: true);
            ClearQuickCommandDropIndicators();
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        int sourceIndex = _viewModel.QuickCommands.IndexOf(source);
        int insertIndex = insertionIndex.Value;
        if (sourceIndex < insertIndex)
        {
            insertIndex--;
        }

        insertIndex = Math.Clamp(insertIndex, 0, Math.Max(0, _viewModel.QuickCommands.Count - 1));
        if (sourceIndex >= 0 && sourceIndex != insertIndex)
        {
            _viewModel.QuickCommands.Move(sourceIndex, insertIndex);
        }

        SelectQuickCommand(source, forceRefresh: true);
        ClearQuickCommandDropIndicators();
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private int? GetQuickCommandInsertionIndex(DragEventArgs e)
    {
        QuickCommandItemViewModel? source = e.Data.GetData(typeof(QuickCommandItemViewModel)) as QuickCommandItemViewModel;
        List<QuickCommandItemViewModel> orderedCommands = source?.IsPinned == true
            ? GetQuickCommandViewItems()
            : _viewModel.QuickCommands.ToList();
        ListBoxItem? container = ItemsControl.ContainerFromElement(
            QuickCommandsList,
            e.OriginalSource as DependencyObject) as ListBoxItem;
        if (container?.DataContext is QuickCommandItemViewModel target)
        {
            int targetIndex = orderedCommands.IndexOf(target);
            if (targetIndex < 0)
            {
                return null;
            }

            double pointerY = e.GetPosition(container).Y;
            double midpoint = container.ActualHeight / 2;
            double transitionHalfHeight = Math.Clamp(container.ActualHeight * 0.20, 8, 20);
            if (pointerY < midpoint - transitionHalfHeight)
            {
                return targetIndex;
            }
            if (pointerY > midpoint + transitionHalfHeight)
            {
                return targetIndex + 1;
            }

            if (_quickCommandDropInsertionIndex is int current
                && current >= targetIndex
                && current <= targetIndex + 1)
            {
                return current;
            }

            return targetIndex + (pointerY >= midpoint ? 1 : 0);
        }

        Point listPosition = e.GetPosition(QuickCommandsList);
        if (listPosition.Y < 0 || listPosition.Y > QuickCommandsList.ActualHeight)
        {
            return null;
        }

        ListBoxItem? firstContainer = null;
        ListBoxItem? lastContainer = null;
        foreach (object item in QuickCommandsList.Items)
        {
            if (QuickCommandsList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem itemContainer)
            {
                continue;
            }

            firstContainer ??= itemContainer;
            lastContainer = itemContainer;
        }

        if (firstContainer?.DataContext is QuickCommandItemViewModel firstCommand)
        {
            double firstTop = firstContainer.TranslatePoint(new Point(0, 0), QuickCommandsList).Y;
            if (listPosition.Y < firstTop)
            {
                return orderedCommands.IndexOf(firstCommand);
            }
        }

        if (lastContainer?.DataContext is QuickCommandItemViewModel lastCommand)
        {
            double lastBottom = lastContainer.TranslatePoint(
                new Point(0, lastContainer.ActualHeight),
                QuickCommandsList).Y;
            if (listPosition.Y > lastBottom)
            {
                return orderedCommands.IndexOf(lastCommand) + 1;
            }
        }

        return _quickCommandDropInsertionIndex ?? orderedCommands.Count;
    }

    private void ShowQuickCommandDropIndicator(int insertionIndex, bool useViewOrder)
    {
        if (_quickCommandDropInsertionIndex == insertionIndex)
        {
            return;
        }

        ClearQuickCommandDropIndicators();
        _quickCommandDropInsertionIndex = insertionIndex;
        List<QuickCommandItemViewModel> orderedCommands = useViewOrder
            ? GetQuickCommandViewItems()
            : _viewModel.QuickCommands.ToList();
        if (orderedCommands.Count == 0)
        {
            return;
        }

        if (insertionIndex >= orderedCommands.Count)
        {
            orderedCommands[^1].IsDropTargetAfter = true;
        }
        else
        {
            orderedCommands[Math.Max(0, insertionIndex)].IsDropTarget = true;
        }
    }

    private List<QuickCommandItemViewModel> GetQuickCommandViewItems() =>
        _viewModel.QuickCommandsView.Cast<QuickCommandItemViewModel>().ToList();

    private void ReorderPinnedQuickCommand(QuickCommandItemViewModel source, int insertionIndex)
    {
        List<QuickCommandItemViewModel> pinnedCommands = GetQuickCommandViewItems()
            .Where(command => command.IsPinned)
            .ToList();
        int sourceIndex = pinnedCommands.IndexOf(source);
        if (sourceIndex < 0)
        {
            return;
        }

        int targetIndex = Math.Clamp(insertionIndex, 0, pinnedCommands.Count);
        if (sourceIndex < targetIndex)
        {
            targetIndex--;
        }

        if (sourceIndex == targetIndex)
        {
            return;
        }

        pinnedCommands.RemoveAt(sourceIndex);
        pinnedCommands.Insert(targetIndex, source);
        _viewModel.ReorderPinnedQuickCommands(pinnedCommands);
    }

    private static Button? GetQuickCommandDragHandle(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is Button { Name: "QuickCommandDragHandle" } button)
            {
                return button;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return null;
    }

    private void SelectQuickCommand(QuickCommandItemViewModel command, bool forceRefresh = false)
    {
        if (forceRefresh)
        {
            QuickCommandsList.SelectedItem = null;
        }

        QuickCommandsList.SelectedItem = command;
        QuickCommandsList.ScrollIntoView(command);
    }

    private void ClearQuickCommandDropIndicators()
    {
        _quickCommandDropInsertionIndex = null;
        foreach (QuickCommandItemViewModel command in _viewModel.QuickCommands)
        {
            command.IsDropTarget = false;
            command.IsDropTargetAfter = false;
        }
    }

    private void CopySelectedTerminalRows()
    {
        IEnumerable<TerminalRecordItem> rows = TerminalList.SelectedItems.Cast<TerminalRecordItem>();
        string text = string.Join(Environment.NewLine, rows.Select(item =>
            item.IsSeparator
                ? _viewModel.BuildTerminalSeparatorLine(item.SeparatorGapMilliseconds)
                : $"{item.TimeText}\t{item.DirectionText}\t{item.Endpoint}\t{item.Size}\t{item.GetDisplayContent(_viewModel.ReceiveAsHex)}"));
        if (!string.IsNullOrEmpty(text))
        {
            Clipboard.SetText(text);
        }
    }

    private async void OnImportLegacyClick(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Title = "选择 SSCOM 或 NetAssist 配置文件",
            Filter = "配置文件 (*.ini;*.cfg)|*.ini;*.cfg|SSCOM 配置 (sscom51.ini)|sscom51.ini|NetAssist 配置 (netassist.cfg)|netassist.cfg|所有文件 (*.*)|*.*",
            InitialDirectory = @"C:\Users\12087\Desktop\串口",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.ImportLegacyFilesAsync(dialog.FileNames);
        }
    }

    private void OnOpenProfileActionsClick(object sender, RoutedEventArgs e)
    {
        OpenContextMenuBelow(ProfileActionsMenu, ProfileActionsButton);
    }

    private void OnOpenTerminalMoreMenuClick(object sender, RoutedEventArgs e)
    {
        OpenContextMenuBelow(TerminalMoreMenu, TerminalMoreButton);
    }

    private static void OpenContextMenuBelow(ContextMenu menu, FrameworkElement target)
    {
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Bottom;
        menu.HorizontalOffset = 0;
        menu.VerticalOffset = 2;
        menu.IsOpen = true;
    }

    private async void OnImportDeviceProfileClick(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Title = "导入设备配置",
            Filter = "设备配置 JSON (*.json)|*.json|所有文件 (*.*)|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.ImportDeviceProfilesAsync(dialog.FileNames);
        }
    }

    private async void OnExportDeviceProfileClick(object sender, RoutedEventArgs e)
    {
        string profileName = string.IsNullOrWhiteSpace(_viewModel.ProfileName) ? "设备配置" : _viewModel.ProfileName;
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            profileName = profileName.Replace(invalid, '_');
        }

        SaveFileDialog dialog = new()
        {
            Title = "导出当前设备配置",
            Filter = "设备配置 JSON (*.json)|*.json",
            FileName = $"{profileName}.json"
        };
        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.ExportSelectedProfileAsync(dialog.FileName);
        }
    }

    private async void OnOpenCaptureClick(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Title = "打开捕获数据库",
            Filter = "捕获数据库 (*.db)|*.db|所有文件 (*.*)|*.*",
            InitialDirectory = AppPaths.CaptureDirectory
        };
        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.OpenCaptureAsync(dialog.FileName);
        }
    }

    private async void OnCheckForUpdatesClick(object sender, RoutedEventArgs e)
    {
        UpdateCheckResult? result = await _viewModel.CheckForUpdatesAsync();
        if (result is null)
        {
            MessageBox.Show(this, _viewModel.UpdateStatusText, "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!result.IsUpdateAvailable)
        {
            MessageBox.Show(
                this,
                $"当前版本 {result.CurrentVersion} 已是最新版本。",
                "检查更新",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        await PromptForUpdateAsync(result);
    }

    private void OnUpdateAvailable(UpdateCheckResult result)
    {
        if (_closing)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => _ = PromptForUpdateAsync(result)));
    }

    private async Task PromptForUpdateAsync(UpdateCheckResult result)
    {
        if (_updatePromptVisible || !result.IsUpdateAvailable)
        {
            return;
        }

        _updatePromptVisible = true;
        try
        {
            string notes = string.IsNullOrWhiteSpace(result.Manifest.ReleaseNotes)
                ? "发布说明未提供。"
                : result.Manifest.ReleaseNotes.Trim();
            if (notes.Length > 1200)
            {
                notes = notes[..1200] + "…";
            }

            MessageBoxResult choice = MessageBox.Show(
                this,
                $"发现 GitHub 新版本 {result.LatestVersion}。\n\n{notes}\n\n现在下载并重启更新吗？",
                "发现更新",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information,
                MessageBoxResult.Yes);
            if (choice != MessageBoxResult.Yes)
            {
                return;
            }

            using CancellationTokenSource updateCancellation = new();
            UpdateProgressWindow progressWindow = new(result)
            {
                Owner = this
            };
            progressWindow.CancellationRequested += (_, _) => updateCancellation.Cancel();

            bool wasEnabled = IsEnabled;
            bool started = false;
            progressWindow.Show();
            IsEnabled = false;
            try
            {
                Progress<UpdateProgressInfo> progress = new(progressWindow.ReportProgress);
                started = await _viewModel
                    .DownloadAndApplyUpdateAsync(result, progress, updateCancellation.Token);
                if (started)
                {
                    progressWindow.MarkCompleted();
                    await Task.Delay(350);
                }
            }
            finally
            {
                progressWindow.AllowClose();
                if (progressWindow.IsVisible)
                {
                    progressWindow.Close();
                }

                IsEnabled = wasEnabled;
            }

            if (started)
            {
                Close();
            }
            else if (!progressWindow.IsCancellationRequested)
            {
                MessageBox.Show(this, _viewModel.UpdateStatusText, "更新失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            _updatePromptVisible = false;
        }
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        SettingsWindow window = new(_viewModel)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void OnTerminalPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        _viewModel.TerminalFontSize += e.Delta > 0 ? 1 : -1;
        e.Handled = true;
    }

    private async void OnConfigureProfileDirectoryClick(object sender, RoutedEventArgs e)
    {
        OpenFolderDialog dialog = new()
        {
            Title = "选择设备配置保存目录",
            InitialDirectory = _viewModel.ProfileDirectory,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.ConfigureProfileDirectoryAsync(dialog.FolderName);
        }
    }

    private async void OnDeleteProfileClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedProfile is null)
        {
            return;
        }
        MessageBoxResult result = MessageBox.Show(
            $"删除设备配置“{_viewModel.SelectedProfile.Name}”？原 SSCOM 文件不会受影响。",
            "删除设备配置",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
        {
            await _viewModel.DeleteSelectedProfileAsync();
        }
    }

    private async void OnSaveTerminalPlainTextClick(object sender, RoutedEventArgs e)
    {
        SaveFileDialog dialog = new()
        {
            Title = "保存连续文本",
            Filter = "文本日志 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            FileName = $"DeviceDebugStudio_Text_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            InitialDirectory = AppPaths.CaptureDirectory
        };
        if (dialog.ShowDialog(this) == true)
        {
            string text = new TextRange(
                TerminalPlainTextBox.Document.ContentStart,
                TerminalPlainTextBox.Document.ContentEnd).Text;
            await File.WriteAllTextAsync(dialog.FileName, text, new UTF8Encoding(false));
            _viewModel.StatusText = $"连续文本已保存：{dialog.FileName}";
        }
    }

    private async void OnExportTerminalTableCsvClick(object sender, RoutedEventArgs e)
    {
        SaveFileDialog dialog = new()
        {
            Title = "导出终端表格",
            Filter = "CSV 表格 (*.csv)|*.csv|所有文件 (*.*)|*.*",
            FileName = $"DeviceDebugStudio_Table_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            InitialDirectory = AppPaths.CaptureDirectory
        };
        if (dialog.ShowDialog(this) == true)
        {
            await File.WriteAllTextAsync(dialog.FileName, _viewModel.ExportTerminalTableCsv(), new UTF8Encoding(true));
            _viewModel.StatusText = $"终端表格已导出：{dialog.FileName}";
        }
    }

    private async void OnSendFileClick(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Title = "选择要原始发送的文件",
            Filter = "所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.SendFileAsync(dialog.FileName);
        }
    }

    private async void OnImportFrameTemplateClick(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Title = "导入帧模板 JSON",
            Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.FrameTemplateJson = await File.ReadAllTextAsync(dialog.FileName, Encoding.UTF8);
            _viewModel.ApplyFrameTemplateCommand.Execute(null);
        }
    }

    private async void OnExportFrameTemplateClick(object sender, RoutedEventArgs e)
    {
        SaveFileDialog dialog = new()
        {
            Title = "导出帧模板 JSON",
            Filter = "JSON 文件 (*.json)|*.json",
            FileName = "frame-template.json"
        };
        if (dialog.ShowDialog(this) == true)
        {
            await File.WriteAllTextAsync(dialog.FileName, _viewModel.FrameTemplateJson, new UTF8Encoding(false));
        }
    }

    private void OnClearChartClick(object sender, RoutedEventArgs e)
    {
        _chartLogger.Clear();
        _chartValues.Clear();
        RealtimePlot.Plot.Axes.AutoScale();
        RealtimePlot.Refresh();
    }

    private async void OnExportChartCsvClick(object sender, RoutedEventArgs e)
    {
        SaveFileDialog dialog = new()
        {
            Title = "导出曲线 CSV",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"Chart_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        StringBuilder builder = new("Index,Value\r\n");
        for (int index = 0; index < _chartValues.Count; index++)
        {
            builder.Append(index).Append(',').Append(_chartValues[index].ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append("\r\n");
        }
        await File.WriteAllTextAsync(dialog.FileName, builder.ToString(), new UTF8Encoding(false));
    }

    private void OnExportChartClick(object sender, RoutedEventArgs e)
    {
        SaveFileDialog dialog = new()
        {
            Title = "导出曲线 PNG",
            Filter = "PNG 图片 (*.png)|*.png",
            FileName = $"Chart_{DateTime.Now:yyyyMMdd_HHmmss}.png"
        };
        if (dialog.ShowDialog(this) == true)
        {
            int width = Math.Max(800, (int)RealtimePlot.ActualWidth);
            int height = Math.Max(480, (int)RealtimePlot.ActualHeight);
            RealtimePlot.Plot.SavePng(dialog.FileName, width, height);
        }
    }

    private void OnToggleThemeClick(object sender, RoutedEventArgs e)
    {
        if (_themeTransitionActive)
        {
            return;
        }

        ApplicationTheme next = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark
            ? ApplicationTheme.Light
            : ApplicationTheme.Dark;
        StartThemeTransition(next);
    }

    private async void OnAddWindowClick(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
        {
            await app.OpenAdditionalWindowAsync(_viewModel.SelectedProfile?.Id);
        }
    }

    private void OnToggleCommandPanelClick(object sender, RoutedEventArgs e)
    {
        if (_sidebarFocus == SidebarFocus.Command)
        {
            _sidebarFocus = SidebarFocus.None;
            _commandDesiredOpen = false;
        }
        else if (_sidebarFocus == SidebarFocus.Device)
        {
            _sidebarFocus = SidebarFocus.Command;
            _commandDesiredOpen = true;
        }
        else if (ShouldFocusCommandPanel())
        {
            _sidebarFocus = SidebarFocus.Command;
            _commandDesiredOpen = true;
        }
        else
        {
            bool opening = !_commandDesiredOpen;
            _commandDesiredOpen = opening;
            if (opening && ShouldFocusCommandPanel())
            {
                _sidebarFocus = SidebarFocus.Command;
            }
        }

        EnforcePanelLayout();
    }

    private void OnToggleDevicePanelClick(object sender, RoutedEventArgs e)
    {
        if (_sidebarFocus == SidebarFocus.Device)
        {
            _sidebarFocus = SidebarFocus.None;
            _deviceDesiredOpen = false;
        }
        else if (_sidebarFocus == SidebarFocus.Command)
        {
            _sidebarFocus = SidebarFocus.Device;
            _deviceDesiredOpen = true;
        }
        else if (ShouldFocusDevicePanel())
        {
            _sidebarFocus = SidebarFocus.Device;
            _deviceDesiredOpen = true;
        }
        else
        {
            bool opening = !_deviceDesiredOpen;
            _deviceDesiredOpen = opening;
            if (opening && ShouldFocusDevicePanel())
            {
                _sidebarFocus = SidebarFocus.Device;
            }
        }

        EnforcePanelLayout();
    }

    private bool CanDockCommandPanel(double totalWidth) =>
        totalWidth >= CurrentWorkspaceMinWidth + CurrentCommandPanelMinWidth + SplitterWidth;

    private bool CanDockDevicePanel(double totalWidth) =>
        totalWidth >= CurrentWorkspaceMinWidth + CurrentDevicePanelMinWidth + SplitterWidth;

    private bool CanDockDesiredPanels(double totalWidth)
    {
        double required = CurrentWorkspaceMinWidth;
        if (_deviceDesiredOpen)
        {
            required += CurrentDevicePanelMinWidth + SplitterWidth;
        }
        if (_commandDesiredOpen)
        {
            required += CurrentCommandPanelMinWidth + SplitterWidth;
        }
        return totalWidth >= required;
    }

    private bool ShouldFocusCommandPanel()
    {
        double totalWidth = ActualWidth;
        return !CanDockCommandPanel(totalWidth)
            || _deviceDesiredOpen && !CanDockDesiredPanels(totalWidth);
    }

    private bool ShouldFocusDevicePanel()
    {
        double totalWidth = ActualWidth;
        return !CanDockDevicePanel(totalWidth)
            || _commandDesiredOpen && !CanDockDesiredPanels(totalWidth);
    }

    private void UpdateResponsiveLayout(double totalWidth)
    {
        bool minimum = totalWidth > 0 && totalWidth < MinimumLayoutBreakpoint;
        bool compact = totalWidth > 0 && totalWidth < CompactLayoutBreakpoint;
        _isMinimumLayout = minimum;
        _isCompactLayout = compact;
        Tag = compact ? "Compact" : "Normal";

        if (TerminalSearchColumn is null
            || TerminalSearchBorder is null
            || TerminalSearchTextBox is null
            || TerminalSearchIcon is null
            || TerminalToolsPanel is null)
        {
            return;
        }

        bool stackedToolbar = compact;
        double searchWidth = stackedToolbar ? double.NaN : 260;
        TerminalSearchColumn.Width = stackedToolbar
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(searchWidth);
        TerminalSearchBorder.Width = searchWidth;
        TerminalSearchBorder.HorizontalAlignment = stackedToolbar
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Left;
        TerminalSearchBorder.Height = compact ? 28 : 30;
        TerminalSearchTextBox.Height = compact ? 26 : 28;
        TerminalSearchTextBox.FontSize = compact ? 11 : 13;
        TerminalSearchIcon.Width = compact ? 14 : 16;
        TerminalSearchIcon.Height = compact ? 14 : 16;
        TerminalToolsPanel.Margin = compact
            ? new Thickness(6, 0, 0, 0)
            : new Thickness(10, 0, 0, 0);

        if (ConnectionHeaderGrid is not null
            && ConnectionParametersScrollViewer is not null
            && ConnectionActionsPanel is not null)
        {
            ConnectionHeaderGrid.RowDefinitions.Clear();
            ConnectionHeaderGrid.ColumnDefinitions.Clear();
            if (compact)
            {
                ConnectionHeaderGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                ConnectionHeaderGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                ConnectionHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetRow(ConnectionParametersScrollViewer, 0);
                Grid.SetColumn(ConnectionParametersScrollViewer, 0);
                Grid.SetRow(ConnectionActionsPanel, 1);
                Grid.SetColumn(ConnectionActionsPanel, 0);
                ConnectionActionsPanel.HorizontalAlignment = HorizontalAlignment.Right;
                ConnectionActionsPanel.Margin = new Thickness(0, 2, 0, 0);
                ConnectionHeaderGrid.MinHeight = 78;
                ConnectionParametersScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                ConnectionParametersScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            }
            else
            {
                ConnectionHeaderGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                ConnectionHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                ConnectionHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetRow(ConnectionParametersScrollViewer, 0);
                Grid.SetColumn(ConnectionParametersScrollViewer, 0);
                Grid.SetRow(ConnectionActionsPanel, 0);
                Grid.SetColumn(ConnectionActionsPanel, 1);
                ConnectionActionsPanel.HorizontalAlignment = HorizontalAlignment.Right;
                ConnectionActionsPanel.Margin = new Thickness(10, 0, 0, 0);
                ConnectionHeaderGrid.MinHeight = 52;
                ConnectionParametersScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                ConnectionParametersScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            }
        }

        if (TerminalToolbarGrid is null)
        {
            return;
        }

        TerminalToolbarGrid.RowDefinitions.Clear();
        TerminalToolbarGrid.ColumnDefinitions.Clear();
        if (stackedToolbar)
        {
            TerminalToolbarGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            TerminalToolbarGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            TerminalToolbarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(TerminalSearchBorder, 0);
            Grid.SetColumn(TerminalSearchBorder, 0);
            Grid.SetRow(TerminalToolsPanel, 1);
            Grid.SetColumn(TerminalToolsPanel, 0);
        }
        else
        {
            TerminalToolbarGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            TerminalToolbarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
            TerminalToolbarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(TerminalSearchBorder, 0);
            Grid.SetColumn(TerminalSearchBorder, 0);
            Grid.SetRow(TerminalToolsPanel, 0);
            Grid.SetColumn(TerminalToolsPanel, 1);
        }
    }

    private double GetCommandPanelMaxWidth(double totalWidth)
    {
        double modeMax = _isMinimumLayout
            ? MinimumCommandPanelMinWidth
            : _isCompactLayout ? 460 : CommandPanelMaxWidth;
        double availableMax = totalWidth - CurrentWorkspaceMinWidth - SplitterWidth;
        return Math.Max(
            CurrentCommandPanelMinWidth,
            Math.Min(modeMax, availableMax));
    }

    private double ClampCommandPanelWidth(double requestedWidth, double totalWidth) =>
        Math.Clamp(
            requestedWidth,
            CurrentCommandPanelMinWidth,
            GetCommandPanelMaxWidth(totalWidth));

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DevicePanelColumn is null || CommandPanelColumn is null)
        {
            return;
        }

        UpdateResponsiveLayout(e.NewSize.Width);
        CloseOpenComboBox();
        EnforcePanelLayout();
        if (_quickCommandEditorExpanded && QuickCommandEditorHost is not null)
        {
            ExpandQuickCommandEditor(animate: false);
        }
        UpdateFrameColumnWidths();
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        CloseOpenComboBox();
        base.OnLocationChanged(e);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        CloseOpenComboBox();
        base.OnStateChanged(e);
    }

    private void OnDeviceSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (DevicePanelColumn.Width.Value >= 10)
        {
            _devicePanelExpandedWidth = DevicePanelColumn.Width;
        }

        EnforcePanelLayout();
    }

    private void OnCommandSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (CommandPanelColumn.Width.Value >= 10)
        {
            _commandPanelExpandedWidth = new GridLength(
                ClampCommandPanelWidth(CommandPanelColumn.Width.Value, ActualWidth));
        }

        EnforcePanelLayout();
    }

    private void OnQuickCommandColumnSplitterDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb { DataContext: QuickCommandItemViewModel command } thumb
            || VisualTreeHelper.GetParent(thumb) is not Grid rowGrid)
        {
            return;
        }

        double currentNameWidth = rowGrid.ColumnDefinitions[1].ActualWidth;
        double currentPayloadWidth = rowGrid.ColumnDefinitions[3].ActualWidth;
        double change = Math.Clamp(e.HorizontalChange, 64 - currentNameWidth, currentPayloadWidth - 56);
        if (Math.Abs(change) < 0.01)
        {
            return;
        }

        command.NameColumnWidth = new GridLength(currentNameWidth + change, GridUnitType.Star);
        command.PayloadColumnWidth = new GridLength(currentPayloadWidth - change, GridUnitType.Star);
        e.Handled = true;
    }

    private void EnforcePanelLayout()
    {
        double totalWidth = ActualWidth;
        if (totalWidth <= 0)
        {
            return;
        }

        if (_sidebarFocus != SidebarFocus.None && CanDockDesiredPanels(totalWidth))
        {
            _sidebarFocus = SidebarFocus.None;
        }

        double commandWidth = 0;
        double deviceWidth = 0;

        // Keep the workspace in the page while a sidebar is focused. At narrow
        // widths the focused panel behaves like a docked drawer instead of
        // replacing the workspace or leaving an empty trailing area.
        bool showWorkspace = true;

        if (_sidebarFocus == SidebarFocus.Command)
        {
            commandWidth = Math.Clamp(
                _commandPanelExpandedWidth.Value,
                CurrentCommandPanelMinWidth,
                GetCommandPanelMaxWidth(totalWidth));
        }
        else if (_sidebarFocus == SidebarFocus.Device)
        {
            deviceWidth = Math.Min(
                Math.Max(_devicePanelExpandedWidth.Value, CurrentDevicePanelMinWidth),
                CurrentDevicePanelMaxWidth);
        }
        else
        {
            commandWidth = _commandDesiredOpen && CanDockCommandPanel(totalWidth)
                ? ClampCommandPanelWidth(_commandPanelExpandedWidth.Value, totalWidth)
                : 0;
            double occupied = CurrentWorkspaceMinWidth + (commandWidth > 0 ? commandWidth + SplitterWidth : 0);
            deviceWidth = _deviceDesiredOpen && totalWidth >= occupied + CurrentDevicePanelMinWidth + SplitterWidth
                ? Math.Min(
                    Math.Max(_devicePanelExpandedWidth.Value, CurrentDevicePanelMinWidth),
                    Math.Min(CurrentDevicePanelMaxWidth, totalWidth - occupied - SplitterWidth))
                : 0;
        }

        WorkspaceColumn.MinWidth = showWorkspace && _sidebarFocus == SidebarFocus.None
            ? CurrentWorkspaceMinWidth
            : 0;
        WorkspaceColumn.Width = showWorkspace ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ApplyDevicePanel(deviceWidth);
        ApplyCommandPanel(commandWidth);
    }

    private void ApplyDevicePanel(double width)
    {
        if (width >= CurrentDevicePanelMinWidth)
        {
            DevicePanelColumn.MinWidth = CurrentDevicePanelMinWidth;
            DevicePanelColumn.Width = new GridLength(width);
            DeviceSplitterColumn.Width = new GridLength(SplitterWidth);
            return;
        }

        CollapseDevicePanel();
    }

    private void ApplyCommandPanel(double width)
    {
        if (width >= CurrentCommandPanelMinWidth)
        {
            CommandPanelColumn.MinWidth = CurrentCommandPanelMinWidth;
            CommandPanelColumn.Width = new GridLength(width);
            CommandSplitterColumn.Width = new GridLength(SplitterWidth);
            return;
        }

        CollapseCommandPanel();
    }

    private void CollapseDevicePanel()
    {
        DevicePanelColumn.MinWidth = 0;
        DevicePanelColumn.Width = new GridLength(0);
        DeviceSplitterColumn.Width = new GridLength(0);
    }

    private void CollapseCommandPanel()
    {
        CommandPanelColumn.MinWidth = 0;
        CommandPanelColumn.Width = new GridLength(0);
        CommandSplitterColumn.Width = new GridLength(0);
    }

    private void StartThemeTransition(ApplicationTheme next)
    {
        double width = WindowLayoutRoot.ActualWidth;
        if (!IsLoaded || width <= 1)
        {
            App.ApplyTheme(next);
            _viewModel.ApplyTerminalThemeColors(next);
            return;
        }

        _themeTransitionActive = true;
        ThemeTransitionOverlay.Visibility = Visibility.Visible;
        ThemeTransitionOverlay.IsHitTestVisible = true;
        ThemeTransitionOverlay.BeginAnimation(UIElement.OpacityProperty, null);
        ThemeTransitionOverlay.Opacity = 1;
        ResetThemeWipes();

        if (next == ApplicationTheme.Dark)
        {
            BeginThemeWipe(DarkThemeWipe, width, () => CompleteThemeTransition(next));
            return;
        }

        BeginThemeWipe(LightThemeLeftWipe, width / 2, null);
        BeginThemeWipe(LightThemeRightWipe, width / 2, () => CompleteThemeTransition(next));
    }

    private static void BeginThemeWipe(FrameworkElement wipe, double width, Action? onCompleted)
    {
        DoubleAnimation animation = new(0, width, ThemeWipeDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        if (onCompleted is not null)
        {
            animation.Completed += (_, _) => onCompleted();
        }
        wipe.BeginAnimation(FrameworkElement.WidthProperty, animation);
    }

    private void CompleteThemeTransition(ApplicationTheme next)
    {
        App.ApplyTheme(next);
        _viewModel.ApplyTerminalThemeColors(next);
        DoubleAnimation reveal = new(1, 0, ThemeRevealDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        reveal.Completed += (_, _) =>
        {
            ThemeTransitionOverlay.BeginAnimation(UIElement.OpacityProperty, null);
            ThemeTransitionOverlay.Opacity = 1;
            ThemeTransitionOverlay.Visibility = Visibility.Collapsed;
            ThemeTransitionOverlay.IsHitTestVisible = false;
            ResetThemeWipes();
            _themeTransitionActive = false;
        };
        ThemeTransitionOverlay.BeginAnimation(UIElement.OpacityProperty, reveal);
    }

    private void ResetThemeWipes()
    {
        foreach (FrameworkElement wipe in new FrameworkElement[]
        {
            DarkThemeWipe,
            LightThemeLeftWipe,
            LightThemeRightWipe
        })
        {
            wipe.BeginAnimation(FrameworkElement.WidthProperty, null);
            wipe.Width = 0;
        }
    }

    private void OnGattTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        switch (e.NewValue)
        {
            case BleGattServiceInfo service:
                _viewModel.BleServiceUuid = service.Uuid.ToString();
                break;
            case BleGattCharacteristicInfo characteristic:
                if (characteristic.Properties.Contains("读", StringComparison.Ordinal))
                {
                    _viewModel.BleReadUuid = characteristic.Uuid.ToString();
                }
                if (characteristic.Properties.Contains("写", StringComparison.Ordinal))
                {
                    _viewModel.BleWriteUuid = characteristic.Uuid.ToString();
                }
                if (characteristic.Properties.Contains("通知", StringComparison.Ordinal) || characteristic.Properties.Contains("指示", StringComparison.Ordinal))
                {
                    _viewModel.BleNotifyUuid = characteristic.Uuid.ToString();
                }
                break;
        }
    }

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_closeApproved)
        {
            return;
        }

        e.Cancel = true;
        if (_closing)
        {
            return;
        }

        _closing = true;
        _chartRefreshTimer.Stop();
        _viewModel.ChartValueAdded -= OnChartValueAdded;
        _viewModel.RecordsAppended -= OnRecordsAppended;
        _viewModel.TerminalRecords.CollectionChanged -= OnTerminalRecordsCollectionChanged;
        _viewModel.FrameRecords.CollectionChanged -= OnFrameRecordsCollectionChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        try
        {
            await _viewModel.DisposeAsync().AsTask().WaitAsync(ViewModelShutdownTimeout);
        }
        catch (TimeoutException)
        {
            Log.Warning("主窗口关闭清理超时，继续结束应用");
        }
        catch (Exception exception)
        {
            Log.Error(exception, "主窗口关闭清理失败，继续结束应用");
        }
        finally
        {
            _closeApproved = true;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Close));
        }
    }
}
