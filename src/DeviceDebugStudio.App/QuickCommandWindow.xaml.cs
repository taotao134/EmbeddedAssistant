using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DeviceDebugStudio.App.ViewModels;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;

namespace DeviceDebugStudio.App;

public partial class QuickCommandWindow : FluentWindow
{
    private const double HorizontalWheelPixelsPerDetent = 48;
    private const double QuickCommandWheelPixelsPerDetent = 48;
    private const double QuickCommandListMinWidth = 300;
    private const double QuickCommandListMaxWidth = 520;
    private const double QuickCommandEditorMinWidth = 420;
    private const double QuickCommandSplitterWidth = 8;
    private readonly MainWindowViewModel _viewModel;
    private readonly DispatcherTimer _quickCommandScrollTimer;
    private Point _dragStartPoint;
    private QuickCommandItemViewModel? _dragSource;
    private int? _dropInsertionIndex;
    private ScrollViewer? _quickCommandScrollViewer;
    private double _quickCommandScrollTarget;
    private bool _quickCommandScrollDirty;

    public QuickCommandWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        _quickCommandScrollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _quickCommandScrollTimer.Tick += OnQuickCommandScrollTimerTick;
        Loaded += OnQuickCommandWindowLoaded;
        SizeChanged += OnQuickCommandWindowSizeChanged;
    }

    private void OnQuickCommandWindowLoaded(object sender, RoutedEventArgs e) =>
        ClampQuickCommandColumns();

    private void OnQuickCommandWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsLoaded)
        {
            ClampQuickCommandColumns();
        }
    }

    private void OnQuickCommandColumnSplitterDragDelta(object sender, DragDeltaEventArgs e)
    {
        // GridSplitter 先更新列宽；在同一输入帧末尾重新夹紧，避免拖动时参数区被推到窗口边界外。
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(ClampQuickCommandColumns));
    }

    private void OnQuickCommandColumnSplitterDragCompleted(object sender, DragCompletedEventArgs e) =>
        ClampQuickCommandColumns();

    private void ClampQuickCommandColumns()
    {
        if (QuickCommandLayoutGrid is null
            || QuickCommandListColumn is null
            || QuickCommandEditorColumn is null)
        {
            return;
        }

        double availableWidth = QuickCommandLayoutGrid.ActualWidth;
        if (!double.IsFinite(availableWidth) || availableWidth <= 0)
        {
            return;
        }

        double splitterWidth = Math.Max(
            QuickCommandSplitterWidth,
            QuickCommandColumnSplitter?.ActualWidth ?? 0);
        double editorMinWidth = Math.Max(
            QuickCommandEditorMinWidth,
            QuickCommandEditorColumn.MinWidth);
        double availableListWidth = availableWidth - splitterWidth - editorMinWidth;
        if (availableListWidth <= 0)
        {
            return;
        }

        double listMinWidth = Math.Min(QuickCommandListMinWidth, availableListWidth);
        double listMaxWidth = Math.Min(
            QuickCommandListMaxWidth,
            Math.Max(listMinWidth, availableListWidth));
        double currentListWidth = QuickCommandListColumn.ActualWidth;
        if (!double.IsFinite(currentListWidth) || currentListWidth <= 0)
        {
            currentListWidth = QuickCommandListColumn.Width.Value;
        }

        double clampedListWidth = Math.Clamp(currentListWidth, listMinWidth, listMaxWidth);
        if (Math.Abs(currentListWidth - clampedListWidth) > 0.5)
        {
            QuickCommandListColumn.Width = new GridLength(clampedListWidth, GridUnitType.Pixel);
        }

        // 重新布局后再检查一次实际编辑区，覆盖 DPI 或边框舍入造成的 1~2px 误差。
        if (QuickCommandEditorColumn.ActualWidth + 0.5 < editorMinWidth)
        {
            double correctedListWidth = Math.Max(
                listMinWidth,
                Math.Min(listMaxWidth, QuickCommandListColumn.ActualWidth - (editorMinWidth - QuickCommandEditorColumn.ActualWidth)));
            QuickCommandListColumn.Width = new GridLength(correctedListWidth, GridUnitType.Pixel);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        App.ApplyWindowTitleBarTheme(this, ApplicationThemeManager.GetAppTheme());
    }

    protected override void OnClosed(EventArgs e)
    {
        _quickCommandScrollTimer.Stop();
        _quickCommandScrollTimer.Tick -= OnQuickCommandScrollTimerTick;
        base.OnClosed(e);
    }

    private void OnQuickCommandSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.OfType<QuickCommandItemViewModel>().FirstOrDefault() is { } command)
        {
            command.IsExpanded = true;
        }
    }

    private void OnAddQuickCommandClick(object sender, RoutedEventArgs e)
    {
        _viewModel.AddQuickCommandCommand.Execute(null);
        if (_viewModel.SelectedQuickCommand is { } command)
        {
            FocusAddedQuickCommand(command);
        }
    }

    private void OnQuickCommandBulkDeleteSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: QuickCommandItemViewModel command } checkBox)
        {
            command.IsSelectedForBulkDelete = checkBox.IsChecked == true;
        }

        _viewModel.NotifyQuickCommandBulkDeleteSelectionChanged();
    }

    private void FocusAddedQuickCommand(QuickCommandItemViewModel command)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            SelectQuickCommand(command);
            QuickCommandsList.UpdateLayout();
            QuickCommandEditorNameTextBox.Focus();
            QuickCommandEditorNameTextBox.CaretIndex = QuickCommandEditorNameTextBox.Text.Length;
            QuickCommandEditorNameTextBox.SelectionLength = 0;
        }, DispatcherPriority.Loaded);
    }

    private void OnQuickCommandPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? originalSource = e.OriginalSource as DependencyObject;
        ListBoxItem? container = ItemsControl.ContainerFromElement(QuickCommandsList, originalSource) as ListBoxItem;
        if (container?.DataContext is not QuickCommandItemViewModel clickedCommand)
        {
            _dragSource = null;
            return;
        }

        QuickCommandsList.SelectedItem = clickedCommand;
        Button? dragHandle = GetQuickCommandDragHandle(originalSource);
        if (dragHandle?.DataContext is not QuickCommandItemViewModel source)
        {
            _dragSource = null;
            return;
        }

        _dragStartPoint = e.GetPosition(QuickCommandsList);
        _dragSource = source;
        if (_viewModel.SelectedQuickCommandSort != "手动顺序")
        {
            _viewModel.SelectedQuickCommandSort = "手动顺序";
        }
        QuickCommandsList.SelectedItem = source;
        e.Handled = true;
    }

    private void OnQuickCommandPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragSource is null)
        {
            return;
        }

        Point current = e.GetPosition(QuickCommandsList);
        if (Math.Abs(current.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        QuickCommandItemViewModel source = _dragSource;
        _dragSource = null;
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
        if (source is null
            || insertionIndex is null
            || IsUnpinnedDropIntoPinnedRegion(source, insertionIndex.Value, e))
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
        if (source is null
            || insertionIndex is null
            || IsUnpinnedDropIntoPinnedRegion(source, insertionIndex.Value, e))
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

            if (_dropInsertionIndex is int current
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

        return _dropInsertionIndex ?? orderedCommands.Count;
    }

    private bool IsUnpinnedDropIntoPinnedRegion(
        QuickCommandItemViewModel source,
        int insertionIndex,
        DragEventArgs e)
    {
        if (source.IsPinned)
        {
            return false;
        }

        List<QuickCommandItemViewModel> orderedCommands = GetQuickCommandViewItems();
        int pinnedCount = orderedCommands.Count(command => command.IsPinned);
        if (insertionIndex < pinnedCount)
        {
            return true;
        }

        ListBoxItem? targetItem = ItemsControl.ContainerFromElement(
            QuickCommandsList,
            e.OriginalSource as DependencyObject) as ListBoxItem;
        return targetItem?.DataContext is QuickCommandItemViewModel { IsPinned: true };
    }

    private void ShowQuickCommandDropIndicator(int insertionIndex, bool useViewOrder)
    {
        if (_dropInsertionIndex == insertionIndex)
        {
            return;
        }

        ClearQuickCommandDropIndicators();
        _dropInsertionIndex = insertionIndex;
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
        _dropInsertionIndex = null;
        foreach (QuickCommandItemViewModel command in _viewModel.QuickCommands)
        {
            command.IsDropTarget = false;
            command.IsDropTargetAfter = false;
        }
    }

    private void OnClearQuickCommandSearchClick(object sender, RoutedEventArgs e)
    {
        _viewModel.QuickCommandSearchText = string.Empty;
        QuickCommandSearchTextBox.Focus();
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

    private void OnVariableSetsPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        DependencyObject source = e.OriginalSource as DependencyObject
            ?? sender as DependencyObject
            ?? VariableSetsList;
        ScrollViewer? viewer = FindScrollViewer(source);
        if (viewer is null || viewer.ScrollableWidth <= 0)
        {
            return;
        }

        double detents = e.Delta / 120.0;
        viewer.ScrollToHorizontalOffset(Math.Clamp(
            viewer.HorizontalOffset - detents * HorizontalWheelPixelsPerDetent,
            0,
            viewer.ScrollableWidth));
        e.Handled = true;
    }

    private void OnVariableSetPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
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

    private void OnVariableSetNameLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: QuickCommandVariableSetItemViewModel variableSet })
        {
            variableSet.IsRenaming = false;
        }
    }

    private void OnVariableSetNamePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape)
            || sender is not TextBox { DataContext: QuickCommandVariableSetItemViewModel variableSet })
        {
            return;
        }

        variableSet.IsRenaming = false;
        e.Handled = true;
    }

    private void OnParameterRepeatIntervalPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        MoveCaretToEnd(sender);

    private void OnRepeatIntervalPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        MoveCaretToEnd(sender);

    private void OnRepeatIntervalLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: QuickCommandItemViewModel command })
        {
            command.CommitRepeatIntervalText();
        }
    }

    private void OnParameterRepeatIntervalLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: QuickCommandItemViewModel command })
        {
            command.CommitParameterRepeatIntervalText();
        }
    }

    private void OnQuickCommandPayloadLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: QuickCommandItemViewModel command })
        {
            command.TryAutoGenerateTemplateFromPayload();
        }
    }

    private void OnQuickCommandPayloadPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter
            || sender is not TextBox { DataContext: QuickCommandItemViewModel command })
        {
            return;
        }

        command.TryAutoGenerateTemplateFromPayload();
        e.Handled = true;
    }

    private void OnAiImportClick(object sender, RoutedEventArgs e)
    {
        OpenFolderDialog dialog = new()
        {
            Title = "选择嵌入式工程根目录",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            string prompt = _viewModel.BuildAiImportPrompt(dialog.FolderName);
            AiImportPreviewWindow preview = new(prompt) { Owner = this };
            _viewModel.StatusText = preview.ShowDialog() == true
                ? "AI导入提示词已复制到剪贴板，请粘贴给 Codex。"
                : "已取消复制 AI导入提示词";
        }
        catch (Exception exception)
        {
            _viewModel.StatusText = $"AI导入提示词生成失败：{exception.Message}";
        }
    }

    private static void MoveCaretToEnd(object sender)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        textBox.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (textBox.IsKeyboardFocusWithin)
            {
                textBox.CaretIndex = textBox.Text.Length;
                textBox.SelectionLength = 0;
            }
        }));
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ScrollViewer viewer)
            {
                return viewer;
            }
        }

        return FindVisualChild<ScrollViewer>(element);
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
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }
}
