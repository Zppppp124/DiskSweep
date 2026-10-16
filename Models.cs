using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DiskSweep;

public enum RiskLevel
{
    Safe,
    Medium,
    Advanced
}

public static class RiskLevelExtensions
{
    public static string Label(this RiskLevel risk) => risk switch
    {
        RiskLevel.Safe => "安全",
        RiskLevel.Medium => "谨慎",
        _ => "高级"
    };

    public static string Description(this RiskLevel risk) => risk switch
    {
        RiskLevel.Safe => "可自动重建，默认勾选",
        RiskLevel.Medium => "可能被程序占用，建议确认后清理",
        _ => "可能影响功能或需要重新下载，默认不勾选"
    };
}

public sealed class CleanupEntry
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public long SizeBytes { get; init; }
    public DateTime LastWriteTime { get; init; }
    public string SizeText => FormatBytes(SizeBytes);
    public string LastWriteText => LastWriteTime == DateTime.MinValue ? "-" : LastWriteTime.ToString("yyyy-MM-dd HH:mm");

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.00} {units[unit]}";
    }
}

public sealed class CleanupCategory : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isExpanded;

    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Recommendation { get; init; }
    public required RiskLevel Risk { get; init; }
    public bool DefaultSelected { get; init; }
    public bool CanSelect { get; init; } = true;
    public bool RequiresAdmin { get; init; }
    public string ActionText { get; init; } = "可清理";
    public string Glyph { get; init; } = "\uE74C";
    public ObservableCollection<CleanupEntry> Entries { get; } = [];
    public long SizeBytes { get; internal set; }
    public int ItemCount { get; internal set; }
    public string SizeText => CleanupEntry.FormatBytes(SizeBytes);
    public string ItemCountText => ItemCount == 1 ? "1 项" : $"{ItemCount:N0} 项";
    public string RiskLabel => Risk.Label();
    public string RiskDescription => Risk.Description();

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class RuleScanResult
{
    public required string RuleId { get; init; }
    public required string Name { get; init; }
    public required long SizeBytes { get; init; }
    public required int ItemCount { get; init; }
    public List<CleanupEntry> Entries { get; init; } = [];
    public string Note { get; init; } = "";
}

public sealed class CleanupResult
{
    public required string RuleId { get; init; }
    public int Deleted { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }
    public long FreedBytes { get; init; }
    public string Message { get; init; } = "";
    public List<string> Failures { get; init; } = [];
}

public sealed class CleanupProgress
{
    public string CategoryName { get; init; } = "";
    public string Path { get; init; } = "";
    public int Completed { get; init; }
    public int Total { get; init; }
    public long FreedBytes { get; init; }
}

public sealed class ScanProgress
{
    public string CategoryName { get; init; } = "";
    public int Completed { get; init; }
    public int Total { get; init; }
    public long BytesFound { get; init; }
}
