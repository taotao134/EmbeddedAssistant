using System.Windows;
using System.Windows.Controls;
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
    private readonly MainWindowViewModel _viewModel;
    private Point _dragStartPoint;
    private QuickCommandItemViewModel? _dragSource;

    public QuickCommandWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        App.ApplyWindowTitleBarTheme(this, ApplicationThemeManager.GetAppTheme());
    }

    private void OnQuickCommandSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.OfType<QuickCommandItemViewModel>().FirstOrDefault() is { } command)
        {
            command.IsExpanded = true;
        }
    }

    private void OnQuickCommandPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? source = e.OriginalSource as DependencyObject;
        ListBoxItem? item = ItemsControl.ContainerFromElement(QuickCommandsList, source) as ListBoxItem;
        if (item?.DataContext is not QuickCommandItemViewModel command
            || FindVisualParent<Button>(source) is not { Name: "QuickCommandDragHandle" })
        {
            _dragSource = null;
            return;
        }

        _dragStartPoint = e.GetPosition(QuickCommandsList);
        _dragSource = command;
        if (_viewModel.SelectedQuickCommandSort != "手动顺序")
        {
            _viewModel.SelectedQuickCommandSort = "手动顺序";
        }
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
        try
        {
            DragDrop.DoDragDrop(QuickCommandsList, source, DragDropEffects.Move);
        }
        finally
        {
            QuickCommandsList.SelectedItem = source;
        }
    }

    private void OnQuickCommandDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(QuickCommandItemViewModel))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnQuickCommandDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(QuickCommandItemViewModel)) is not QuickCommandItemViewModel source)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        List<QuickCommandItemViewModel> viewItems = _viewModel.QuickCommandsView
            .OfType<QuickCommandItemViewModel>()
            .ToList();
        List<QuickCommandItemViewModel> orderedItems = source.IsPinned
            ? viewItems
            : _viewModel.QuickCommands.ToList();
        ListBoxItem? targetItem = ItemsControl.ContainerFromElement(
            QuickCommandsList,
            e.OriginalSource as DependencyObject) as ListBoxItem;
        if (targetItem?.DataContext is not QuickCommandItemViewModel target)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        int targetIndex = orderedItems.IndexOf(target);
        if (targetIndex < 0)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (e.GetPosition(targetItem).Y >= targetItem.ActualHeight / 2)
        {
            targetIndex++;
        }

        if (source.IsPinned)
        {
            List<QuickCommandItemViewModel> pinned = viewItems.Where(command => command.IsPinned).ToList();
            int sourceIndex = pinned.IndexOf(source);
            int insertionIndex = Math.Clamp(targetIndex, 0, pinned.Count);
            if (sourceIndex >= 0)
            {
                if (sourceIndex < insertionIndex)
                {
                    insertionIndex--;
                }
                pinned.RemoveAt(sourceIndex);
                pinned.Insert(Math.Clamp(insertionIndex, 0, pinned.Count), source);
                _viewModel.ReorderPinnedQuickCommands(pinned);
            }
        }
        else
        {
            int sourceIndex = _viewModel.QuickCommands.IndexOf(source);
            int insertionIndex = Math.Clamp(targetIndex, 0, orderedItems.Count);
            if (sourceIndex >= 0)
            {
                if (sourceIndex < insertionIndex)
                {
                    insertionIndex--;
                }
                insertionIndex = Math.Clamp(insertionIndex, 0, _viewModel.QuickCommands.Count - 1);
                if (sourceIndex != insertionIndex)
                {
                    _viewModel.QuickCommands.Move(sourceIndex, insertionIndex);
                }
            }
        }

        QuickCommandsList.SelectedItem = source;
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnClearQuickCommandSearchClick(object sender, RoutedEventArgs e)
    {
        _viewModel.QuickCommandSearchText = string.Empty;
        QuickCommandSearchTextBox.Focus();
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
