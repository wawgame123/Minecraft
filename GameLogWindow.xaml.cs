using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace ServerLauncher;

public partial class GameLogWindow : Window
{
    private const int MaxLogCharacters = 120_000;
    private const int MaxPendingLines = 4_000;
    private const int MaxFlushLines = 350;
    private readonly StringBuilder _logBuffer = new();
    private readonly Queue<string> _pendingLines = new();
    private readonly DispatcherTimer _flushTimer;
    private bool _allowClose;
    private int _droppedLines;

    public GameLogWindow()
    {
        InitializeComponent();
        _flushTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _flushTimer.Tick += (_, _) => FlushPendingLines();
        _flushTimer.Start();
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

        var line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        lock (_pendingLines)
        {
            if (_pendingLines.Count >= MaxPendingLines)
            {
                _pendingLines.Dequeue();
                _droppedLines += 1;
            }

            _pendingLines.Enqueue(line);
        }
    }

    private void FlushPendingLines()
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        var flushed = 0;
        var hadLines = false;
        lock (_pendingLines)
        {
            if (_droppedLines > 0)
            {
                _logBuffer.Append($"[{DateTime.Now:HH:mm:ss}] Пропущено строк лога: {_droppedLines}. Консоль ограничивает поток, чтобы не снижать FPS.{Environment.NewLine}");
                _droppedLines = 0;
                hadLines = true;
            }

            while (_pendingLines.Count > 0 && flushed < MaxFlushLines)
            {
                _logBuffer.Append(_pendingLines.Dequeue());
                flushed += 1;
                hadLines = true;
            }
        }

        if (!hadLines)
        {
            return;
        }

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
        lock (_pendingLines)
        {
            _pendingLines.Clear();
            _droppedLines = 0;
        }

        _logBuffer.Clear();
        LogBox.Clear();
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        FlushPendingLines();
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

    protected override void OnClosed(EventArgs e)
    {
        _flushTimer.Stop();
        base.OnClosed(e);
    }
}
