namespace ServerLauncher.Models;

public sealed class FileStatusItem
{
    public string Path { get; set; } = "";
    public string Category { get; set; } = "";
    public string Status { get; set; } = "Не проверен";
    public long Size { get; set; }
    public bool Required { get; set; }
}
