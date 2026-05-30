using System.ComponentModel;
using System.Text;
using System.Windows;

namespace ServerLauncher;

public partial class GameLogWindow : Window
{
    private const int MaxLogCharacters = 240_000;
    private readonly StringBuilder _logBuffer = new();
    private bool _allowClose;

    public GameLogWindow()
    {
        InitializeComponent();
    }

    public void SetProcessStarted(int processId)
    {
        SetStatus($"Minecraft запущен, PID {processId}.");
    }

    public void MarkProcessExited(int exitCode)
    {
        AppendLine($"Процесс Minecraft завершился с кодом {exitCode}.");
        SetStatus(exitCode == 0
            ? "Minecraft завершился без ошибки."
            : $"Minecraft завершился с ошибкой, код {exitCode}.");
        _allowClose = true;
    }

    public void AppendLine(string message)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AppendLine(message));
            return;
        }

        var line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        _logBuffer.Append(line);
        TrimBufferIfNeeded();
        LogBox.Text = _logBuffer.ToString();
        LogBox.CaretIndex = LogBox.Text.Length;
        LogBox.ScrollToEnd();
    }

    private void SetStatus(string status)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetStatus(status));
            return;
        }

        StatusText.Text = status;
    }

    private void TrimBufferIfNeeded()
    {
        if (_logBuffer.Length <= MaxLogCharacters)
        {
            return;
        }

        var removeCount = Math.Min(60_000, _logBuffer.Length - MaxLogCharacters);
        _logBuffer.Remove(0, removeCount);
        _logBuffer.Insert(0, $"[{DateTime.Now:HH:mm:ss}] Лог был обрезан, чтобы окно не тормозило.{Environment.NewLine}");
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _logBuffer.Clear();
        LogBox.Clear();
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (LogBox.Text.Length > 0)
        {
            System.Windows.Clipboard.SetText(LogBox.Text);
        }
    }

    private void HideButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }
}
