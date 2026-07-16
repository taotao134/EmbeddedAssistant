using System.ComponentModel;
using System.Collections.Specialized;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
    private readonly MainWindowViewModel _viewModel;
    private readonly DataLogger _chartLogger;
    private readonly DispatcherTimer _chartRefreshTimer;
    private readonly List<double> _chartValues = [];
    private bool _chartDirty;
    private bool _closing;
    private bool _devicePanelAutoCollapsed;
    private bool _commandPanelAutoCollapsed;
    private GridLength _devicePanelExpandedWidth = new(232);
    private GridLength _commandPanelExpandedWidth = new(540);
    private Point _quickCommandDragStartPoint;
    private QuickCommandItemViewModel? _quickCommandDragSource;

    public MainWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

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
        UpdateTerminalColumnWidths(_viewModel.TerminalFontSize);
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
            string direction = item.Direction switch
            {
                PacketDirection.Send => "TX→◇",
                PacketDirection.Receive => "RX←◆",
                _ => item.DirectionText
            };
            string size = item.Size.ToString(System.Globalization.CultureInfo.InvariantCulture);
            builder.Append('[')
                .Append(item.TimeText)
                .Append("]\t")
                .Append(direction)
                .Append("\t[")
                .Append(size)
                .Append("]\t");
            string content = item.GetContinuousTextContent(_viewModel.ReceiveAsHex);
            string continuationPrefix = new string(' ', item.TimeText.Length + 2)
                + '\t'
                + new string(' ', direction.Length)
                + '\t'
                + new string(' ', size.Length + 2)
                + '\t';
            AppendAlignedContinuousContent(builder, content, continuationPrefix);
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
        if (e.PropertyName == nameof(MainWindowViewModel.ReceiveAsHex))
        {
            TerminalPlainTextBox.Clear();
            AppendTerminalPlainText(_viewModel.TerminalRecords.Count);
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.TerminalFontSize))
        {
            UpdateTerminalColumnWidths(_viewModel.TerminalFontSize);
        }
    }

    private void UpdateTerminalColumnWidths(double fontSize)
    {
        double normalized = Math.Clamp(fontSize, 9, 28);
        TerminalTimeColumn.Width = Math.Ceiling(Math.Max(122, normalized * 7.4 + 24));
        TerminalDirectionColumn.Width = Math.Ceiling(Math.Max(58, normalized * 2 + 24));
        TerminalEndpointColumn.Width = Math.Ceiling(Math.Max(150, normalized * 8 + 24));
        TerminalSizeColumn.Width = Math.Ceiling(Math.Max(66, normalized * 2 + 24));
        TerminalContentColumn.Width = Math.Ceiling(Math.Max(760, normalized * 30 + 40));
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
        Button? dragHandle = GetQuickCommandDragHandle(e.OriginalSource as DependencyObject);
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
            DragDrop.DoDragDrop(QuickCommandsList, source, DragDropEffects.Move);
        }
        finally
        {
            QuickCommandsList.Tag = null;
            ClearQuickCommandDropIndicators();
            _ = Dispatcher.InvokeAsync(
                () => SelectQuickCommand(source, forceRefresh: true),
                DispatcherPriority.Loaded);
        }
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

        QuickCommandsList.SelectedItem = source;
        ShowQuickCommandDropIndicator(insertionIndex.Value);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnQuickCommandDragLeave(object sender, DragEventArgs e)
    {
        Point position = e.GetPosition(QuickCommandsList);
        if (position.X < 0
            || position.Y < 0
            || position.X > QuickCommandsList.ActualWidth
            || position.Y > QuickCommandsList.ActualHeight)
        {
            ClearQuickCommandDropIndicators();
        }
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

            bool after = e.GetPosition(container).Y >= container.ActualHeight / 2;
            return targetIndex + (after ? 1 : 0);
        }

        Point listPosition = e.GetPosition(QuickCommandsList);
        if (listPosition.X < 0
            || listPosition.Y < 0
            || listPosition.X > QuickCommandsList.ActualWidth
            || listPosition.Y > QuickCommandsList.ActualHeight)
        {
            return null;
        }

        return _viewModel.QuickCommands.Count;
    }

    private void ShowQuickCommandDropIndicator(int insertionIndex)
    {
        ClearQuickCommandDropIndicators();
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

    private void OnOpenQuickCommandSortMenuClick(object sender, RoutedEventArgs e)
    {
        QuickCommandSortMenu.PlacementTarget = QuickCommandSortButton;
        QuickCommandSortMenu.Placement = PlacementMode.Bottom;
        QuickCommandSortMenu.IsOpen = true;
    }

    private void OnQuickCommandSortMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string sortMode })
        {
            _viewModel.SelectedQuickCommandSort = sortMode;
        }
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

    private async void OnSaveTerminalClick(object sender, RoutedEventArgs e)
    {
        SaveFileDialog dialog = new()
        {
            Title = "保存终端显示",
            Filter = "文本日志 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            FileName = $"DeviceDebugStudio_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            InitialDirectory = Path.GetDirectoryName(AppPaths.CaptureDirectory)
        };
        if (dialog.ShowDialog(this) == true)
        {
            await File.WriteAllTextAsync(dialog.FileName, _viewModel.ExportTerminalText(), new UTF8Encoding(false));
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
    }

    private void OnToggleCommandPanelClick(object sender, RoutedEventArgs e)
    {
        _commandPanelAutoCollapsed = false;
        if (CommandPanelColumn.Width.Value < 10)
        {
            CommandPanelColumn.Width = _commandPanelExpandedWidth;
            CommandSplitterColumn.Width = new GridLength(5);
        }
        else
        {
            _commandPanelExpandedWidth = CommandPanelColumn.Width;
            CommandPanelColumn.Width = new GridLength(0);
            CommandSplitterColumn.Width = new GridLength(0);
        }
    }

    private void OnToggleDevicePanelClick(object sender, RoutedEventArgs e)
    {
        _devicePanelAutoCollapsed = false;
        if (DevicePanelColumn.Width.Value < 10)
        {
            DevicePanelColumn.Width = _devicePanelExpandedWidth;
            DeviceSplitterColumn.Width = new GridLength(5);
        }
        else
        {
            _devicePanelExpandedWidth = DevicePanelColumn.Width;
            DevicePanelColumn.Width = new GridLength(0);
            DeviceSplitterColumn.Width = new GridLength(0);
        }
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DevicePanelColumn is null || CommandPanelColumn is null)
        {
            return;
        }

        if (e.NewSize.Width < 1120 && DevicePanelColumn.Width.Value >= 10)
        {
            _devicePanelExpandedWidth = DevicePanelColumn.Width;
            DevicePanelColumn.Width = new GridLength(0);
            DeviceSplitterColumn.Width = new GridLength(0);
            _devicePanelAutoCollapsed = true;
        }
        else if (e.NewSize.Width >= 1180 && _devicePanelAutoCollapsed)
        {
            DevicePanelColumn.Width = _devicePanelExpandedWidth;
            DeviceSplitterColumn.Width = new GridLength(5);
            _devicePanelAutoCollapsed = false;
        }

        if (e.NewSize.Width < 960 && CommandPanelColumn.Width.Value >= 10)
        {
            _commandPanelExpandedWidth = CommandPanelColumn.Width;
            CommandPanelColumn.Width = new GridLength(0);
            CommandSplitterColumn.Width = new GridLength(0);
            _commandPanelAutoCollapsed = true;
        }
        else if (e.NewSize.Width >= 1020 && _commandPanelAutoCollapsed)
        {
            CommandPanelColumn.Width = _commandPanelExpandedWidth;
            CommandSplitterColumn.Width = new GridLength(5);
            _commandPanelAutoCollapsed = false;
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
