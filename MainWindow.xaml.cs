using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace DiskSweep;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly DiskScanner _scanner = new();
    private readonly DiskCleaner _cleaner = new();
    private readonly ObservableCollection<CleanupCategory> _categories = [];
    private readonly ObservableCollection<LogEntry> _logEntries = [];
    private readonly string _logDirectory;
    private bool _isBusy;
    private string _adminText = DiskScanner.IsAdministrator() ? "管理员模式" : "普通权限";
    private string _driveName = "C:";
    private string _driveHint = "";
    private string _freeText = "-";
    private string _freePercentText = "";
    private string _cleanableText = "-";
    private string _selectedText = "尚未扫描";
    private string _scanStatus = "准备扫描";
    private double _scanProgressValue;
    private string _cleanStatus = "尚未清理";
    private string _footerHint = "先扫描，确认分类与大小后再清理。高级项目默认不勾选。";

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        foreach (var category in _categories)
        {
            category.PropertyChanged += (_, _) => UpdateSelectionSummary();
        }

        _logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiskSweep",
            "logs");
        Directory.CreateDirectory(_logDirectory);
        RefreshDriveInfo();
        Loaded += (_, _) => Scan_Click(this, new RoutedEventArgs());
    }

    public ObservableCollection<CleanupCategory> Categories => _categories;
    public ObservableCollection<LogEntry> LogEntries => _logEntries;

    public string AdminText
    {
        get => _adminText;
        private set => SetField(ref _adminText, value);
    }

    public string DriveName
    {
        get => _driveName;
        private set => SetField(ref _driveName, value);
    }

    public string DriveHint
    {
        get => _driveHint;
        private set => SetField(ref _driveHint, value);
    }

    public string FreeText
    {
        get => _freeText;
        private set => SetField(ref _freeText, value);
    }

    public string FreePercentText
    {
        get => _freePercentText;
        private set => SetField(ref _freePercentText, value);
    }

    public string CleanableText
    {
        get => _cleanableText;
        private set => SetField(ref _cleanableText, value);
    }

    public string SelectedText
    {
        get => _selectedText;
        private set => SetField(ref _selectedText, value);
    }

    public string ScanStatus
    {
        get => _scanStatus;
        private set => SetField(ref _scanStatus, value);
    }

    public double ScanProgressValue
    {
        get => _scanProgressValue;
        private set => SetField(ref _scanProgressValue, value);
    }

    public string CleanStatus
    {
        get => _cleanStatus;
        private set => SetField(ref _cleanStatus, value);
    }

    public string FooterHint
    {
        get => _footerHint;
        private set => SetField(ref _footerHint, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        _isBusy = true;
        ScanStatus = "正在扫描...";
        ScanProgressValue = 0;
        FooterHint = "扫描只读取文件元数据，不会修改任何内容。";
        _categories.Clear();
        RefreshDriveInfo();

        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                if (p.Total > 0)
                {
                    ScanProgressValue = p.Completed * 100d / p.Total;
                }

                ScanStatus = $"{p.CategoryName} · {CleanupEntry.FormatBytes(p.BytesFound)}";
            });

            var results = await _scanner.ScanAsync(progress, CancellationToken.None);
            BuildCategories(results);
            ScanStatus = $"扫描完成 · {_categories.Count} 类";
            ScanProgressValue = 100;
            FooterHint = "安全项默认勾选，谨慎和高级项需要手动选择。";
            UpdateSelectionSummary();
        }
        catch (Exception ex)
        {
            ScanStatus = $"扫描失败: {ex.Message}";
            MessageBox.Show(this, ex.Message, "扫描失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        var selected = _categories
            .Where(c => c.IsSelected && c.CanSelect && c.Entries.Count > 0)
            .ToList();

        if (selected.Count == 0)
        {
            MessageBox.Show(this, "请先扫描并勾选至少一个可清理项目。", "没有选择",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        long selectedBytes = selected.Sum(c => c.SizeBytes);
        string risks = string.Join("\n",
            selected.Where(c => c.Risk != RiskLevel.Safe)
                .Select(c => $"· {c.Name}（{c.RiskLabel}）"));
        string message = new StringBuilder()
            .AppendLine($"将清理 {selected.Count} 个类别，预计 {CleanupEntry.FormatBytes(selectedBytes)}：")
            .AppendLine(string.Join("\n", selected.Select(c => $"· {c.Name} - {c.SizeText}")))
            .AppendLine()
            .AppendLine(risks.Length > 0
                ? "其中包含需要确认的项目：\n" + risks + "\n\n继续吗？"
                : "这些项目都属于低风险缓存或临时内容。继续吗？")
            .ToString();

        if (MessageBox.Show(this, message, "确认清理",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        _isBusy = true;
        CleanStatus = "正在清理...";
        FooterHint = "清理过程中会跳过被占用或不安全路径。";
        _logEntries.Clear();

        var progress = new Progress<CleanupProgress>(p =>
        {
            if (p.Total > 0)
            {
                ScanProgressValue = p.Completed * 100d / p.Total;
            }

            CleanStatus = $"{p.CategoryName} · 已释放 {CleanupEntry.FormatBytes(p.FreedBytes)}";
        });

        var overall = new List<CleanupResult>();
        try
        {
            foreach (var category in selected)
            {
                CleanStatus = $"正在清理 {category.Name}";
                var result = await _cleaner.CleanAsync(category, progress, CancellationToken.None);
                overall.Add(result);
                AddLog(category.Name, result);
            }

            long freed = overall.Sum(r => r.FreedBytes);
            int deleted = overall.Sum(r => r.Deleted);
            int skipped = overall.Sum(r => r.Skipped);
            int failed = overall.Sum(r => r.Failed);
            CleanStatus = $"清理完成 · 释放 {CleanupEntry.FormatBytes(freed)} · 删除 {deleted} 项 · 跳过 {skipped} 项 · 失败 {failed} 项";
            WriteLog(overall);
            RefreshDriveInfo();
            await RescanAfterCleanup();
        }
        catch (Exception ex)
        {
            CleanStatus = $"清理失败: {ex.Message}";
            MessageBox.Show(this, ex.Message, "清理失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async Task RescanAfterCleanup()
    {
        ScanStatus = "正在重新扫描...";
        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                if (p.Total > 0)
                {
                    ScanProgressValue = p.Completed * 100d / p.Total;
                }

                ScanStatus = $"{p.CategoryName} · {CleanupEntry.FormatBytes(p.BytesFound)}";
            });
            var results = await _scanner.ScanAsync(progress, CancellationToken.None);
            BuildCategories(results);
            ScanStatus = "清理后重扫完成";
            ScanProgressValue = 100;
            UpdateSelectionSummary();
        }
        catch (Exception ex)
        {
            ScanStatus = $"清理后重扫失败: {ex.Message}";
        }
    }

    private void ViewDetails_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CleanupCategory category })
        {
            return;
        }

        if (category.Entries.Count == 0)
        {
            MessageBox.Show(this, "该项目没有可显示的明细。", category.Name,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        new DetailsWindow(category) { Owner = this }.ShowDialog();
    }

    private void DefaultSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var category in _categories)
        {
            category.IsSelected = category.CanSelect && category.DefaultSelected;
        }

        UpdateSelectionSummary();
    }

    private void Category_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CleanupCategory.IsSelected))
        {
            UpdateSelectionSummary();
        }
    }

    private void UnselectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var category in _categories)
        {
            category.IsSelected = false;
        }

        UpdateSelectionSummary();
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_logDirectory);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _logDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "无法打开日志目录",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BuildCategories(IReadOnlyList<RuleScanResult> results)
    {
        _categories.Clear();
        foreach (var result in results)
        {
            bool canSelect = result.ItemCount > 0 && result.SizeBytes > 0;
            var category = new CleanupCategory
            {
                Id = result.RuleId,
                Name = result.Name,
                Description = result.Note.Length > 0 ? result.Note : Describe(result.RuleId),
                Recommendation = Recommend(result.RuleId),
                Risk = RiskFor(result.RuleId),
                DefaultSelected = DefaultSelectedFor(result.RuleId),
                CanSelect = canSelect,
                RequiresAdmin = RequiresAdminFor(result.RuleId)
            };

            category.SizeBytes = result.SizeBytes;
            category.ItemCount = result.ItemCount;
            foreach (var entry in result.Entries)
            {
                category.Entries.Add(entry);
            }

            category.PropertyChanged += Category_PropertyChanged;
            category.IsSelected = canSelect && category.DefaultSelected;
            _categories.Add(category);
        }

        UpdateSelectionSummary();
    }

    private void UpdateSelectionSummary()
    {
        long all = _categories.Sum(c => c.SizeBytes);
        long selected = _categories.Where(c => c.IsSelected).Sum(c => c.SizeBytes);
        CleanableText = CleanupEntry.FormatBytes(all);
        SelectedText = $"已选 {CleanupEntry.FormatBytes(selected)}";
    }

    private void RefreshDriveInfo()
    {
        var drive = _scanner.GetSystemDrive();
        DriveName = $"{drive.Name} ({CleanupEntry.FormatBytes(drive.TotalSize)} 总容量)";
        DriveHint = $"文件系统 {drive.DriveFormat} · 卷标 {drive.VolumeLabel}";
        FreeText = CleanupEntry.FormatBytes(drive.AvailableFreeSpace);
        double freePercent = drive.TotalSize == 0 ? 0 : drive.AvailableFreeSpace * 100d / drive.TotalSize;
        FreePercentText = $"可用 {freePercent:0.0}%";
    }

    private void AddLog(string category, CleanupResult result)
    {
        _logEntries.Insert(0, new LogEntry
        {
            Time = DateTime.Now.ToString("HH:mm:ss"),
            Category = category,
            Path = result.Message,
            Result = result.Failed > 0 ? "有失败" : result.Skipped > 0 ? "部分跳过" : "完成"
        });
    }

    private void WriteLog(IReadOnlyList<CleanupResult> results)
    {
        try
        {
            Directory.CreateDirectory(_logDirectory);
            string path = Path.Combine(_logDirectory, $"clean-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            var lines = new List<string>
            {
                $"DiskSweep clean log {DateTime.Now:O}",
                $"Total freed: {CleanupEntry.FormatBytes(results.Sum(r => r.FreedBytes))}"
            };
            lines.AddRange(results.SelectMany(r =>
                new[] { $"[{r.RuleId}] {r.Message}" }.Concat(r.Failures.Select(f => "  " + f))));
            File.WriteAllLines(path, lines, Encoding.UTF8);
        }
        catch
        {
            // Logging failure must not interrupt cleanup.
        }
    }

    private static string Describe(string id) => id switch
    {
        "temp-user" => "浏览器、安装器和应用运行产生的临时副本",
        "temp-windows" => "系统安装和更新过程中留下的临时文件",
        "crash-dumps" => "应用崩溃后生成的内存转储",
        "pip-cache" => "Python 包下载缓存",
        "npm-cache" => "Node.js 包下载缓存",
        "yarn-cache" => "Yarn 包下载缓存",
        "pnpm-cache" => "pnpm 包存储与下载缓存",
        "go-build" => "Go 编译中间产物",
        "gradle-cache" => "Gradle 构建缓存",
        "nuget-cache" => ".NET 包缓存",
        "windows-update" => "已安装或已过期更新的下载副本",
        "package-cache" => "桌面软件、驱动和开发工具的安装包缓存",
        "recycle-bin" => "已删除但仍占用磁盘空间的文件",
        "model-cache" => "Hugging Face、ModelScope、Whisper 等模型缓存",
        "hibernation" => "用于休眠和快速启动的系统文件",
        "dism" => "WinSxS 组件存储分析结果",
        _ => "可清理内容"
    };

    private static string Recommend(string id) => id switch
    {
        "temp-user" or "temp-windows" => "删除 7 天前的普通文件，跳过链接和占用项",
        "crash-dumps" => "除排查特定故障外可清理",
        "pip-cache" or "npm-cache" or "yarn-cache" or "pnpm-cache" =>
            "可清理，下次安装依赖会重新下载",
        "go-build" or "gradle-cache" => "可清理，首次重新构建会稍慢",
        "nuget-cache" => "可清理，首次还原依赖时会重新下载",
        "windows-update" => "可以清理，但下次更新可能需要重新下载",
        "package-cache" => "只在确定不需要修复或卸载旧软件时清理",
        "recycle-bin" => "确认其中没有需要还原的文件",
        "model-cache" => "仅在确定这些模型不再使用时清理",
        "hibernation" => "会关闭休眠和快速启动，需要管理员权限",
        "dism" => "只在系统分析明确建议时清理",
        _ => "请确认后清理"
    };

    private static RiskLevel RiskFor(string id) => id switch
    {
        "temp-user" or "temp-windows" or "crash-dumps" or "pip-cache" or "npm-cache" or
            "yarn-cache" or "pnpm-cache" or "go-build" or "gradle-cache" or "nuget-cache" or
        "windows-update" or "package-cache" or "recycle-bin" => RiskLevel.Medium,
        _ => RiskLevel.Advanced
    };

    private static bool DefaultSelectedFor(string id) => id switch
    {
        "temp-user" or "temp-windows" or "crash-dumps" => true,
        _ => false
    };

    private static bool RequiresAdminFor(string id) => id switch
    {
        "temp-windows" or "windows-update" or "package-cache" or "hibernation" or "dism" => true,
        _ => false
    };

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class LogEntry
{
    public required string Time { get; init; }
    public required string Category { get; init; }
    public required string Path { get; init; }
    public required string Result { get; init; }
}
