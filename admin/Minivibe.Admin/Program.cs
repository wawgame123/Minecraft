using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ServerLauncher.Models;

namespace Minivibe.Admin;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

internal sealed class MainForm : Form
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly string _root = FindRepositoryRoot();
    private readonly TextBox _statusBox = new() { ReadOnly = true, Dock = DockStyle.Bottom };
    private readonly TextBox _updateVersionBox = new();
    private readonly TextBox _updateUrlBox = new();
    private readonly TextBox _updateShaBox = new();
    private readonly CheckBox _mandatoryBox = new() { Text = "Обязательное обновление" };
    private readonly ListBox _notesList = new();
    private readonly TextBox _noteBox = new();
    private readonly ListBox _changelogList = new();
    private readonly TextBox _changelogBox = new();
    private readonly DataGridView _newsGrid = new();
    private readonly DataGridView _modsGrid = new();

    private LauncherManifest _manifest = new();
    private LauncherUpdateManifest _update = new();
    private BindingList<NewsItem> _news = [];
    private BindingList<ManifestFile> _mods = [];

    public MainForm()
    {
        Text = "minivibe admin";
        Width = 1080;
        Height = 720;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);

        Controls.Add(BuildTabs());
        Controls.Add(BuildTopBar());
        Controls.Add(_statusBox);

        LoadData();
    }

    private Control BuildTopBar()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 46,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(8),
            WrapContents = false
        };

        panel.Controls.Add(Button("Сохранить", SaveAll));
        panel.Controls.Add(Button("Отправить на GitHub", PushToGitHub));
        panel.Controls.Add(Button("Открыть папку проекта", () => Process.Start("explorer.exe", _root)));
        panel.Controls.Add(new Label
        {
            AutoSize = true,
            Text = _root,
            Padding = new Padding(12, 8, 0, 0)
        });
        return panel;
    }

    private Control BuildTabs()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(new TabPage("Патч-ноуты") { Padding = new Padding(10), Controls = { BuildPatchTab() } });
        tabs.TabPages.Add(new TabPage("Изменения сборки") { Padding = new Padding(10), Controls = { BuildChangelogTab() } });
        tabs.TabPages.Add(new TabPage("Новости") { Padding = new Padding(10), Controls = { BuildNewsTab() } });
        tabs.TabPages.Add(new TabPage("Моды") { Padding = new Padding(10), Controls = { BuildModsTab() } });
        return tabs;
    }

    private Control BuildPatchTab()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 7
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddRow(root, 0, "Версия", _updateVersionBox);
        AddRow(root, 1, "ZIP URL", _updateUrlBox);
        AddRow(root, 2, "SHA-256", _updateShaBox);
        root.Controls.Add(_mandatoryBox, 1, 3);

        _notesList.Dock = DockStyle.Fill;
        root.Controls.Add(new Label { Text = "Патч-ноуты", Dock = DockStyle.Fill }, 0, 4);
        root.Controls.Add(_notesList, 1, 4);
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        _noteBox.Dock = DockStyle.Fill;
        root.Controls.Add(_noteBox, 1, 5);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        buttons.Controls.Add(Button("Добавить строку", AddNote));
        buttons.Controls.Add(Button("Удалить строку", DeleteNote));
        root.Controls.Add(buttons, 1, 6);
        return root;
    }

    private Control BuildChangelogTab()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        _changelogList.Dock = DockStyle.Fill;
        root.Controls.Add(_changelogList, 0, 0);

        _changelogBox.Dock = DockStyle.Fill;
        root.Controls.Add(_changelogBox, 0, 1);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        buttons.Controls.Add(Button("Добавить изменение", AddChangelogLine));
        buttons.Controls.Add(Button("Удалить изменение", DeleteChangelogLine));
        root.Controls.Add(buttons, 0, 2);
        return root;
    }

    private Control BuildNewsTab()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));

        ConfigureGrid(_newsGrid);
        _newsGrid.AutoGenerateColumns = false;
        _newsGrid.Columns.Add(TextColumn("Title", "Заголовок", 180));
        _newsGrid.Columns.Add(TextColumn("Date", "Дата", 110));
        _newsGrid.Columns.Add(ComboColumn("Kind", "Тип", ["text", "image", "html"], 90));
        _newsGrid.Columns.Add(TextColumn("Text", "Текст", 280));
        _newsGrid.Columns.Add(TextColumn("Url", "URL картинки/HTML", 320));
        root.Controls.Add(_newsGrid, 0, 0);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        buttons.Controls.Add(Button("Текст", AddTextNews));
        buttons.Controls.Add(Button("Картинка", AddImageNews));
        buttons.Controls.Add(Button("HTML", AddHtmlNews));
        buttons.Controls.Add(Button("Удалить", DeleteNews));
        root.Controls.Add(buttons, 0, 1);
        return root;
    }

    private Control BuildModsTab()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));

        ConfigureGrid(_modsGrid);
        _modsGrid.AutoGenerateColumns = false;
        _modsGrid.Columns.Add(TextColumn("Path", "Путь в сборке", 360));
        _modsGrid.Columns.Add(TextColumn("Size", "Размер", 90));
        _modsGrid.Columns.Add(TextColumn("Sha256", "SHA-256", 260));
        _modsGrid.Columns.Add(TextColumn("Url", "URL", 360));
        _modsGrid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            DataPropertyName = nameof(ManifestFile.Required),
            HeaderText = "Обязательный",
            Width = 110
        });
        root.Controls.Add(_modsGrid, 0, 0);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        buttons.Controls.Add(Button("Добавить .jar", AddMod));
        buttons.Controls.Add(Button("Удалить из manifest", DeleteMod));
        buttons.Controls.Add(Button("Пересчитать выбранный", RefreshSelectedMod));
        root.Controls.Add(buttons, 0, 1);
        return root;
    }

    private void LoadData()
    {
        _manifest = ReadJson<LauncherManifest>(ManifestPath()) ?? new LauncherManifest();
        _update = ReadJson<LauncherUpdateManifest>(UpdatePath()) ?? new LauncherUpdateManifest();
        _news = new BindingList<NewsItem>(_manifest.News);
        _mods = new BindingList<ManifestFile>(_manifest.RequiredFiles
            .Where(file => IsMod(file))
            .OrderBy(file => file.Path)
            .ToList());

        _updateVersionBox.Text = _update.Version;
        _updateUrlBox.Text = _update.Url;
        _updateShaBox.Text = _update.Sha256;
        _mandatoryBox.Checked = _update.Mandatory;
        _notesList.Items.Clear();
        foreach (var note in _update.Notes)
        {
            _notesList.Items.Add(note);
        }

        _changelogList.Items.Clear();
        foreach (var line in _manifest.Changelog)
        {
            _changelogList.Items.Add(line);
        }

        _newsGrid.DataSource = _news;
        _modsGrid.DataSource = _mods;
        SetStatus("Данные загружены.");
    }

    private void SaveAll()
    {
        _newsGrid.EndEdit();
        _modsGrid.EndEdit();

        _manifest.News = _news.ToList();
        SyncModsBackToManifest();
        _update.Version = _updateVersionBox.Text.Trim();
        _update.Url = _updateUrlBox.Text.Trim();
        _update.Sha256 = _updateShaBox.Text.Trim();
        _update.Mandatory = _mandatoryBox.Checked;
        _update.Notes = _notesList.Items.Cast<string>().Where(note => !string.IsNullOrWhiteSpace(note)).ToList();
        _manifest.Changelog = _changelogList.Items.Cast<string>().Where(line => !string.IsNullOrWhiteSpace(line)).ToList();

        WriteJson(ManifestPath(), _manifest);
        WriteJson(UpdatePath(), _update);
        SetStatus("Сохранено: manifest.json и launcher/update.json.");
    }

    private void PushToGitHub()
    {
        try
        {
            SaveAll();
            var paths = new List<string>
            {
                "manifest.json",
                "launcher/update.json",
                "server-pack"
            };

            if (Directory.Exists(Path.Combine(_root, "news")))
            {
                paths.Add("news");
            }

            RunGit("add -- " + string.Join(" ", paths.Select(QuoteGitArgument)));

            var hasChanges = RunGit("diff --cached --quiet", allowFailure: true).ExitCode != 0;
            if (!hasChanges)
            {
                SetStatus("Нет изменений для отправки.");
                MessageBox.Show(this, "Нет изменений для отправки.", "minivibe admin", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var message = $"Update launcher content {DateTime.Now:yyyy-MM-dd HH:mm}";
            RunGit($"commit -m \"{message}\"");
            RunGit("push origin main");
            SetStatus("Изменения отправлены на GitHub.");
            MessageBox.Show(this, "Изменения отправлены на GitHub.", "minivibe admin", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка отправки: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Ошибка отправки на GitHub", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SyncModsBackToManifest()
    {
        var nonMods = _manifest.RequiredFiles.Where(file => !IsMod(file)).ToList();
        _manifest.RequiredFiles = nonMods
            .Concat(_mods)
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void AddNote()
    {
        var text = _noteBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _notesList.Items.Add(text);
        _noteBox.Clear();
    }

    private void DeleteNote()
    {
        if (_notesList.SelectedIndex >= 0)
        {
            _notesList.Items.RemoveAt(_notesList.SelectedIndex);
        }
    }

    private void AddChangelogLine()
    {
        var text = _changelogBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _changelogList.Items.Add(text);
        _changelogBox.Clear();
    }

    private void DeleteChangelogLine()
    {
        if (_changelogList.SelectedIndex >= 0)
        {
            _changelogList.Items.RemoveAt(_changelogList.SelectedIndex);
        }
    }

    private void AddTextNews()
    {
        _news.Add(new NewsItem
        {
            Title = "Новая новость",
            Date = DateTime.Now.ToString("yyyy-MM-dd"),
            Kind = NewsItem.TextKind,
            Text = "Текст новости"
        });
    }

    private void AddImageNews()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Выберите картинку",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.gif|All files|*.*"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var relativePath = CopyNewsAsset(dialog.FileName);
        _news.Add(new NewsItem
        {
            Title = Path.GetFileNameWithoutExtension(dialog.FileName),
            Date = DateTime.Now.ToString("yyyy-MM-dd"),
            Kind = NewsItem.ImageKind,
            Url = RawGitHubUrl(relativePath),
            Text = ""
        });
    }

    private void AddHtmlNews()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Выберите HTML-файл",
            Filter = "HTML|*.html;*.htm|All files|*.*"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var relativePath = CopyNewsAsset(dialog.FileName);
        _news.Add(new NewsItem
        {
            Title = Path.GetFileNameWithoutExtension(dialog.FileName),
            Date = DateTime.Now.ToString("yyyy-MM-dd"),
            Kind = NewsItem.HtmlKind,
            Url = RawGitHubUrl(relativePath),
            Text = ""
        });
    }

    private void DeleteNews()
    {
        if (_newsGrid.CurrentRow?.DataBoundItem is NewsItem item)
        {
            _news.Remove(item);
        }
    }

    private void AddMod()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Выберите .jar мод",
            Filter = "Minecraft mods|*.jar|All files|*.*"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var required = MessageBox.Show(
            this,
            "Сделать мод обязательным для запуска?",
            "Обязательность мода",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question) == DialogResult.Yes;

        var relativePath = CopyMod(dialog.FileName);
        var fullPath = Path.Combine(_root, PackRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        var item = new ManifestFile
        {
            Path = relativePath,
            Url = RawGitHubUrl(Path.Combine(PackRoot(), relativePath).Replace('\\', '/')),
            Sha256 = ComputeSha256(fullPath),
            Size = new FileInfo(fullPath).Length,
            Category = "mod",
            Required = required
        };

        var existing = _mods.FirstOrDefault(mod => string.Equals(mod.Path, item.Path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.Url = item.Url;
            existing.Sha256 = item.Sha256;
            existing.Size = item.Size;
            existing.Required = item.Required;
            _mods.ResetItem(_mods.IndexOf(existing));
        }
        else
        {
            _mods.Add(item);
        }

        SetStatus($"Мод добавлен: {relativePath}");
    }

    private void DeleteMod()
    {
        if (_modsGrid.CurrentRow?.DataBoundItem is ManifestFile item)
        {
            _mods.Remove(item);
            SetStatus($"Удалено из manifest: {item.Path}");
        }
    }

    private void RefreshSelectedMod()
    {
        if (_modsGrid.CurrentRow?.DataBoundItem is not ManifestFile item)
        {
            return;
        }

        var fullPath = Path.Combine(_root, PackRoot(), item.Path.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
        {
            MessageBox.Show(this, "Файл не найден: " + fullPath, "minivibe admin", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        item.Sha256 = ComputeSha256(fullPath);
        item.Size = new FileInfo(fullPath).Length;
        item.Url = RawGitHubUrl(Path.Combine(PackRoot(), item.Path).Replace('\\', '/'));
        _mods.ResetItem(_mods.IndexOf(item));
        SetStatus($"Пересчитано: {item.Path}");
    }

    private string CopyNewsAsset(string sourcePath)
    {
        var fileName = $"{DateTime.Now:yyyyMMdd-HHmmss}-{CleanFileName(Path.GetFileName(sourcePath))}";
        var relativePath = $"news/{fileName}";
        var destination = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(sourcePath, destination, overwrite: true);
        SetStatus($"Файл новости скопирован: {relativePath}");
        return relativePath;
    }

    private string CopyMod(string sourcePath)
    {
        var relativePath = $"mods/{CleanFileName(Path.GetFileName(sourcePath))}";
        var destination = Path.Combine(_root, PackRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(sourcePath, destination, overwrite: true);
        return relativePath;
    }

    private string PackRoot()
    {
        return $"server-pack/{_manifest.Loader}-{_manifest.LoaderVersion}";
    }

    private string ManifestPath() => Path.Combine(_root, "manifest.json");
    private string UpdatePath() => Path.Combine(_root, "launcher", "update.json");

    private static bool IsMod(ManifestFile file)
    {
        return string.Equals(file.Category, "mod", StringComparison.OrdinalIgnoreCase)
            || file.Path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)
            || file.Path.StartsWith("mods\\", StringComparison.OrdinalIgnoreCase);
    }

    private static T? ReadJson<T>(string path)
    {
        return File.Exists(path)
            ? JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8), JsonOptions)
            : default;
    }

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
    }

    private string RawGitHubUrl(string relativePath)
    {
        var encoded = string.Join("/", relativePath.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
        return "https://raw.githubusercontent.com/wawgame123/Minecraft/main/" + encoded;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string CleanFileName(string fileName)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalid, '-');
        }

        return fileName.Replace(' ', '-');
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "manifest.json"))
                && File.Exists(Path.Combine(directory.FullName, "ServerLauncher.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Environment.CurrentDirectory;
    }

    private void SetStatus(string text)
    {
        _statusBox.Text = text;
    }

    private GitResult RunGit(string arguments, bool allowFailure = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = _root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Не удалось запустить git.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        var result = new GitResult(process.ExitCode, output, error);
        if (!allowFailure && result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {arguments} завершился с кодом {result.ExitCode}.{Environment.NewLine}{result.Output}{result.Error}");
        }

        return result;
    }

    private static string QuoteGitArgument(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static Button Button(string text, Action action)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            Height = 32,
            Margin = new Padding(4)
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static void AddRow(TableLayoutPanel panel, int row, string label, Control control)
    {
        control.Dock = DockStyle.Fill;
        panel.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
        panel.Controls.Add(control, 1, row);
    }

    private static void ConfigureGrid(DataGridView grid)
    {
        grid.Dock = DockStyle.Fill;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
    }

    private static DataGridViewTextBoxColumn TextColumn(string property, string header, int width)
    {
        return new DataGridViewTextBoxColumn
        {
            DataPropertyName = property,
            HeaderText = header,
            Width = width
        };
    }

    private static DataGridViewComboBoxColumn ComboColumn(string property, string header, string[] values, int width)
    {
        return new DataGridViewComboBoxColumn
        {
            DataPropertyName = property,
            HeaderText = header,
            DataSource = values,
            Width = width
        };
    }

    private sealed record GitResult(int ExitCode, string Output, string Error);
}
