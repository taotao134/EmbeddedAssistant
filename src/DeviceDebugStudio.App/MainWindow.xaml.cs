using System.ComponentModel;
using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media;
using DeviceDebugStudio.App.ViewModels;
using DeviceDebugStudio.Core.Transports;
using DeviceDebugStudio.Infrastructure.Persistence;
using DeviceDebugStudio.Infrastructure.Transports;
using Microsoft.Win32;
using ScottPlot.Plottables;
using Wpf.Ui.Appearance;

namespace DeviceDebugStudio.App;

public partial class MainWindow : Window
{
    private const double CommandPanelMinWidth = 480;
    private const double CommandPanelMaxWidth = 620;
    private const double DevicePanelMinWidth = 150;
    private const double WorkspaceMinWidth = 480;
    private const double SplitterWidth = 5;
    private const double TerminalTimeColumnMinWidth = 60;
    private const double TerminalDirectionColumnMinWidth = 84;
    private const double TerminalEndpointColumnMinWidth = 70;
    private const double TerminalSizeColumnMinWidth = 38;
    private const double TerminalContentColumnMinWidth = 140;
    private const int WmNonClientLeftButtonDoubleClick = 0x00A3;
    private const int WmNonClientHitTest = 0x0084;
    private const int WmSystemCommand = 0x0112;
    private const int HitTestSystemMenu = 3;
    private const int HitTestCloseButton = 20;
    private const int SystemCommandClose = 0xF060;

    private readonly MainWindowViewModel _viewModel;
    private readonly DataLogger _chartLogger;
    private readonly DispatcherTimer _chartRefreshTimer;
    private readonly List<double> _chartValues = [];
    private HwndSource? _windowSource;
    private bool _chartDirty;
    private bool _closing;
    private bool _deviceDesiredOpen;
    private bool _commandDesiredOpen = true;
    private SidebarFocus _sidebarFocus;
    private GridLength _devicePanelExpandedWidth = new(232);
    private GridLength _commandPanelExpandedWidth = new(500);
    private Point _quickCommandDragStartPoint;
    private QuickCommandItemViewModel? _quickCommandDragSource;
    private int? _quickCommandDropInsertionIndex;
    private bool _terminalColumnDragActive;
    private double _terminalViewportWidth;

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
        TerminalList.AddHandler(
            Thumb.DragDeltaEvent,
            new DragDeltaEventHandler(OnTerminalColumnHeaderDragDelta),
            true);
        TerminalList.AddHandler(
            Thumb.DragCompletedEvent,
            new DragCompletedEventHandler(OnTerminalColumnHeaderDragCompleted),
            true);

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
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.QuickCommandAdded += OnQuickCommandAdded;
        UpdateTerminalColumnWidths();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource?.AddHook(OnWindowMessage);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.QuickCommandAdded -= OnQuickCommandAdded;
        _windowSource?.RemoveHook(OnWindowMessage);
        _windowSource = null;
        base.OnClosed(e);
    }

    private static nint OnWindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmNonClientLeftButtonDoubleClick && wParam.ToInt32() == HitTestSystemMenu)
        {
            handled = true;
        }
        else if (message == WmSystemCommand
            && (wParam.ToInt64() & 0xFFF0) == SystemCommandClose
            && GetPointerHitTest(hwnd) != HitTestCloseButton)
        {
            handled = true;
        }

        return 0;
    }

    private static int GetPointerHitTest(nint hwnd)
    {
        if (!GetCursorPos(out NativePoint point))
        {
            return 0;
        }

        nint coordinates = (point.X & 0xFFFF) | (point.Y << 16);
        return SendMessage(hwnd, WmNonClientHitTest, 0, coordinates).ToInt32();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hwnd, int message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
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
        if (_viewModel.AutoScroll
            && TerminalDisplayTabs.SelectedItem == TerminalTableTab
            && TerminalList.Items.Count > 0)
        {
            TerminalList.ScrollIntoView(TerminalList.Items[^1]);
        }

        UpdateTerminalColumnWidths();
    }

    private void OnTerminalDisplaySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, TerminalDisplayTabs) || !_viewModel.AutoScroll)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (TerminalDisplayTabs.SelectedItem == TerminalTableTab && TerminalList.Items.Count > 0)
            {
                object lastItem = TerminalList.Items[^1];
                TerminalList.UpdateLayout();
                TerminalList.ScrollIntoView(lastItem);
                TerminalList.UpdateLayout();
                TerminalList.ScrollIntoView(lastItem);
            }
            else if (TerminalDisplayTabs.SelectedItem == TerminalPlainTextTab)
            {
                TerminalPlainTextBox.CaretIndex = TerminalPlainTextBox.Text.Length;
                TerminalPlainTextBox.ScrollToEnd();
            }
        });
    }

    private void OnTerminalListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTerminalColumnWidths();
    }

    private void OnTerminalListScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        double viewportWidth = GetTerminalViewportWidth();
        if (viewportWidth > 0 && Math.Abs(viewportWidth - _terminalViewportWidth) > 0.1)
        {
            UpdateTerminalColumnWidths(viewportWidth);
        }
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

        int start = _viewModel.TerminalRecords.Count - actualCount;
        StringBuilder builder = new(actualCount * 64);
        for (int index = start; index < _viewModel.TerminalRecords.Count; index++)
        {
            TerminalRecordItem item = _viewModel.TerminalRecords[index];
            if (IsConnectionStatusRecord(item) || !_viewModel.IsTerminalRecordVisible(item))
            {
                continue;
            }

            string direction = item.Direction switch
            {
                PacketDirection.Send => "发→◇",
                PacketDirection.Receive => "收←◆",
                _ => item.DirectionText
            };
            string prefix = $"[{item.TimeText}]{direction}";
            builder.Append(prefix);
            string content = item.GetContinuousTextContent(_viewModel.ReceiveAsHex);
            AppendAlignedContinuousContent(builder, content, prefix);
            if (!content.EndsWith('\r') && !content.EndsWith('\n'))
            {
                builder.AppendLine();
            }
        }

        int selectionStart = TerminalPlainTextBox.SelectionStart;
        int selectionLength = TerminalPlainTextBox.SelectionLength;
        TerminalPlainTextBox.AppendText(builder.ToString());
        if (TerminalPlainTextBox.Text.Length > 4_000_000)
        {
            string retained = TerminalPlainTextBox.Text[^3_000_000..];
            int firstLineEnd = retained.IndexOf('\n');
            TerminalPlainTextBox.Text = firstLineEnd >= 0 ? retained[(firstLineEnd + 1)..] : retained;
            selectionStart = 0;
            selectionLength = 0;
        }

        if (selectionLength > 0)
        {
            TerminalPlainTextBox.Select(selectionStart, selectionLength);
        }
        else if (_viewModel.AutoScroll)
        {
            TerminalPlainTextBox.ScrollToEnd();
        }
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
            TerminalPlainTextBox.Clear();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.ReceiveAsHex)
            or nameof(MainWindowViewModel.SearchText))
        {
            TerminalPlainTextBox.Clear();
            AppendTerminalPlainText(_viewModel.TerminalRecords.Count);
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.TerminalFontSize))
        {
            UpdateTerminalColumnWidths();
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

        QuickCommandsList.SelectedItem = clickedCommand;
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
            QuickCommandsList.UpdateLayout();
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

    private void OnQuickCommandsListPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ScrollViewer? outer = FindVisualChild<ScrollViewer>(QuickCommandsList);
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

        double wheelSteps = Math.Clamp(Math.Abs(e.Delta) / 120.0, 0.25, 1.0);
        double offset = outer.VerticalOffset - Math.Sign(e.Delta) * 24.0 * wheelSteps;
        outer.ScrollToVerticalOffset(Math.Clamp(offset, 0, outer.ScrollableHeight));
        e.Handled = true;
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
        if (e.ClickCount == 3 && sender is TextBox textBox)
        {
            textBox.SelectAll();
            e.Handled = true;
        }
    }

    private void OnQuickVariablePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 3 && sender is TextBox textBox)
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
        ShowQuickCommandDropIndicator(insertionIndex.Value);
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
        ListBoxItem? container = ItemsControl.ContainerFromElement(
            QuickCommandsList,
            e.OriginalSource as DependencyObject) as ListBoxItem;
        if (container?.DataContext is QuickCommandItemViewModel target)
        {
            int targetIndex = _viewModel.QuickCommands.IndexOf(target);
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
                return _viewModel.QuickCommands.IndexOf(firstCommand);
            }
        }

        if (lastContainer?.DataContext is QuickCommandItemViewModel lastCommand)
        {
            double lastBottom = lastContainer.TranslatePoint(
                new Point(0, lastContainer.ActualHeight),
                QuickCommandsList).Y;
            if (listPosition.Y > lastBottom)
            {
                return _viewModel.QuickCommands.IndexOf(lastCommand) + 1;
            }
        }

        return _quickCommandDropInsertionIndex ?? _viewModel.QuickCommands.Count;
    }

    private void ShowQuickCommandDropIndicator(int insertionIndex)
    {
        if (_quickCommandDropInsertionIndex == insertionIndex)
        {
            return;
        }

        ClearQuickCommandDropIndicators();
        _quickCommandDropInsertionIndex = insertionIndex;
        if (_viewModel.QuickCommands.Count == 0)
        {
            return;
        }

        if (insertionIndex >= _viewModel.QuickCommands.Count)
        {
            _viewModel.QuickCommands[^1].IsDropTargetAfter = true;
        }
        else
        {
            _viewModel.QuickCommands[Math.Max(0, insertionIndex)].IsDropTarget = true;
        }
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
            $"{item.TimeText}\t{item.DirectionText}\t{item.Endpoint}\t{item.Size}\t{item.GetDisplayContent(_viewModel.ReceiveAsHex)}"));
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
        ProfileActionsMenu.PlacementTarget = ProfileActionsButton;
        ProfileActionsMenu.Placement = PlacementMode.Bottom;
        ProfileActionsMenu.IsOpen = true;
    }

    private void OnOpenTerminalMoreMenuClick(object sender, RoutedEventArgs e)
    {
        TerminalMoreMenu.PlacementTarget = TerminalMoreButton;
        TerminalMoreMenu.Placement = PlacementMode.Bottom;
        TerminalMoreMenu.IsOpen = true;
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
            await File.WriteAllTextAsync(dialog.FileName, TerminalPlainTextBox.Text, new UTF8Encoding(false));
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
        ApplicationTheme next = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark
            ? ApplicationTheme.Light
            : ApplicationTheme.Dark;
        App.ApplyTheme(next);
        _viewModel.ApplyTerminalThemeColors(next);
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
            _commandDesiredOpen = !_commandDesiredOpen;
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
            _deviceDesiredOpen = !_deviceDesiredOpen;
        }

        EnforcePanelLayout();
    }

    private bool CanDockCommandPanel(double totalWidth) =>
        totalWidth >= WorkspaceMinWidth + CommandPanelMinWidth + SplitterWidth;

    private bool CanDockDevicePanel(double totalWidth) =>
        totalWidth >= WorkspaceMinWidth + DevicePanelMinWidth + SplitterWidth;

    private bool CanDockDesiredPanels(double totalWidth)
    {
        double required = WorkspaceMinWidth;
        if (_deviceDesiredOpen)
        {
            required += DevicePanelMinWidth + SplitterWidth;
        }
        if (_commandDesiredOpen)
        {
            required += CommandPanelMinWidth + SplitterWidth;
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

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DevicePanelColumn is null || CommandPanelColumn is null)
        {
            return;
        }

        EnforcePanelLayout();
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
                Math.Clamp(CommandPanelColumn.Width.Value, CommandPanelMinWidth, CommandPanelMaxWidth));
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
                CommandPanelMinWidth,
                CommandPanelMaxWidth);
        }
        else if (_sidebarFocus == SidebarFocus.Device)
        {
            deviceWidth = Math.Max(_devicePanelExpandedWidth.Value, DevicePanelMinWidth);
        }
        else
        {
            commandWidth = _commandDesiredOpen && CanDockCommandPanel(totalWidth)
                ? Math.Clamp(_commandPanelExpandedWidth.Value, CommandPanelMinWidth, CommandPanelMaxWidth)
                : 0;
            double occupied = WorkspaceMinWidth + (commandWidth > 0 ? commandWidth + SplitterWidth : 0);
            deviceWidth = _deviceDesiredOpen && totalWidth >= occupied + DevicePanelMinWidth + SplitterWidth
                ? Math.Max(_devicePanelExpandedWidth.Value, DevicePanelMinWidth)
                : 0;
        }

        WorkspaceColumn.MinWidth = showWorkspace && _sidebarFocus == SidebarFocus.None
            ? WorkspaceMinWidth
            : 0;
        WorkspaceColumn.Width = showWorkspace ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ApplyDevicePanel(deviceWidth);
        ApplyCommandPanel(commandWidth);
    }

    private void ApplyDevicePanel(double width)
    {
        if (width >= DevicePanelMinWidth)
        {
            DevicePanelColumn.MinWidth = DevicePanelMinWidth;
            DevicePanelColumn.Width = new GridLength(width);
            DeviceSplitterColumn.Width = new GridLength(SplitterWidth);
        }
        else
        {
            DevicePanelColumn.MinWidth = 0;
            DevicePanelColumn.Width = new GridLength(0);
            DeviceSplitterColumn.Width = new GridLength(0);
        }
    }

    private void ApplyCommandPanel(double width)
    {
        if (width >= CommandPanelMinWidth)
        {
            CommandPanelColumn.MinWidth = CommandPanelMinWidth;
            CommandPanelColumn.Width = new GridLength(width);
            CommandSplitterColumn.Width = new GridLength(SplitterWidth);
        }
        else
        {
            CommandPanelColumn.MinWidth = 0;
            CommandPanelColumn.Width = new GridLength(0);
            CommandSplitterColumn.Width = new GridLength(0);
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
        if (_closing)
        {
            return;
        }

        e.Cancel = true;
        _closing = true;
        _chartRefreshTimer.Stop();
        _viewModel.ChartValueAdded -= OnChartValueAdded;
        _viewModel.RecordsAppended -= OnRecordsAppended;
        _viewModel.TerminalRecords.CollectionChanged -= OnTerminalRecordsCollectionChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        await _viewModel.DisposeAsync();
        Close();
    }
}
