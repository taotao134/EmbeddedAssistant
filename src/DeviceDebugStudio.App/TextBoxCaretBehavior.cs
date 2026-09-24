using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DeviceDebugStudio.App;

/// <summary>
/// 输入框光标定位行为。
/// </summary>
/// <remarks>
/// 统一交互约定：首次点击（尚未获得键盘焦点）时把光标放到文本末尾，便于直接修改个位数字；
/// 已有焦点后再点则保持 WPF 默认行为，允许在文本中间精确定位。
/// </remarks>
internal static class TextBoxCaretBehavior
{
    /// <summary>
    /// 注册输入框鼠标按下的类处理器，必须在创建任何窗口前调用一次。
    /// </summary>
    /// <returns>无返回值</returns>
    public static void Register()
    {
        EventManager.RegisterClassHandler(
            typeof(TextBox),
            UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnPreviewMouseLeftButtonDown),
            true);
    }

    /// <summary>
    /// 处理输入框的首次鼠标点击，把光标定位到文本末尾。
    /// </summary>
    /// <param name="sender">事件来源的输入框</param>
    /// <param name="e">鼠标左键按下事件参数</param>
    /// <returns>无返回值</returns>
    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 双击、三击保留 WPF 的词选与段选语义；只读文本框保留点击定位，便于在日志中选中内容。
        if (e.ChangedButton != MouseButton.Left
            || e.ClickCount != 1
            || sender is not TextBox { IsReadOnly: false } textBox
            || textBox.IsKeyboardFocusWithin)
        {
            return;
        }

        // 定位必须放在 Focus 之后：Focus 会同步触发前一个输入框的 LostKeyboardFocus，
        // 那里的文本回写会把光标打回 0，之后再定位才能覆盖掉。
        textBox.Focus();
        textBox.Select(textBox.Text.Length, 0);

        // 与上面的定位成对出现：吞掉本次按下，WPF 才不会按点击位置重新定位光标。
        e.Handled = true;
    }
}
