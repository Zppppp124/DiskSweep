using System.Diagnostics;
using System.Security.Principal;

namespace DiskSweep;

public sealed class DiskScanner
{
    private readonly string _userProfile;

    public DiskScanner()
    {
        _userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public DriveInfo GetSystemDrive()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string root = Path.GetPathRoot(windows) ?? "C:\\";
        return new DriveInfo(root);
    }

    public Task<IReadOnlyList<RuleScanResult>> ScanAsync(
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        return Task.Run(() => Scan(progress, cancellationToken), cancellationToken);
    }

    public IReadOnlyList<RuleScanResult> Scan(IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        var rules = BuildRules().ToList();
        var results = new List<RuleScanResult>(rules.Count);
        long totalBytes = 0;

        for (int i = 0; i < rules.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rule = rules[i];
            progress?.Report(new ScanProgress
            {
                CategoryName = rule.Name,
                Completed = i,
                Total = rules.Count,
                BytesFound = totalBytes
            });

            RuleScanResult result;
            try
            {
                result = ScanSingleRule(rule, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = new RuleScanResult
                {
                    RuleId = rule.Id,
                    Name = rule.Name,
                    SizeBytes = 0,
                    ItemCount = 0,
                    Note = $"扫描失败: {ex.Message}"
                };
            }

            totalBytes += result.SizeBytes;
            results.Add(result);
        }

        progress?.Report(new ScanProgress
        {
            CategoryName = "完成",
            Completed = rules.Count,
            Total = rules.Count,
            BytesFound = totalBytes
        });

        return results
            .OrderByDescending(r => r.SizeBytes)
            .ThenBy(r => r.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private RuleScanResult ScanSingleRule(ScanRule rule, CancellationToken cancellationToken)
    {
        var entries = new List<CleanupEntry>();
        long total = 0;
        string note = "";

        switch (rule.Kind)
        {
            case RuleKind.DirectoryContents:
                foreach (string path in rule.Paths)
                {
                    if (!Directory.Exists(path))
                    {
                        continue;
                    }

                    var info = new DirectoryInfo(path);
                    if (rule.MinAgeDays is { } days)
                    {
                        DateTime cutoff = DateTime.Now.AddDays(-days);
                        foreach (var item in EnumerateChildren(info, cancellationToken))
                        {
                            if (item.LastWriteTime >= cutoff)
                            {
                                continue;
                            }

                            var entry = Measure(item, rule.Name, cancellationToken);
                            entries.Add(entry);
                            total += entry.SizeBytes;
                        }
                    }
                    else
                    {
                        foreach (var item in EnumerateChildren(info, cancellationToken))
                        {
                            var entry = Measure(item, rule.Name, cancellationToken);
                            entries.Add(entry);
                            total += entry.SizeBytes;
                        }
                    }
                }
                break;

            case RuleKind.SingleFile:
                foreach (string path in rule.Paths)
                {
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    var info = new FileInfo(path);
                    entries.Add(new CleanupEntry
                    {
                        Path = path,
                        Name = info.Name,
                        Kind = rule.Name,
                        SizeBytes = info.Length,
                        LastWriteTime = info.LastWriteTime
                    });
                    total += info.Length;
                }
                break;

            case RuleKind.RecycleBin:
                foreach (var drive in DriveInfo.GetDrives())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
                    {
                        continue;
                    }

                    string recycle = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin");
                    if (!Directory.Exists(recycle))
                    {
                        continue;
                    }

                    foreach (var item in EnumerateChildren(new DirectoryInfo(recycle), cancellationToken))
                    {
                        var entry = Measure(item, rule.Name, cancellationToken);
                        entries.Add(entry);
                        total += entry.SizeBytes;
                    }
                }
                break;

            case RuleKind.DismAnalyze:
            {
                var (size, text) = AnalyzeComponentStore(cancellationToken);
                total = size;
                note = text;
                if (size > 0)
                {
                    entries.Add(new CleanupEntry
                    {
                        Path = "WinSxS",
                        Name = "Windows 组件存储",
                        Kind = rule.Name,
                        SizeBytes = size,
                        LastWriteTime = DateTime.Now
                    });
                }
                break;
            }
        }

        entries.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
        return new RuleScanResult
        {
            RuleId = rule.Id,
            Name = rule.Name,
            SizeBytes = total,
            ItemCount = entries.Count,
            Entries = entries,
            Note = note
        };
    }

    private static IEnumerable<FileSystemInfo> EnumerateChildren(DirectoryInfo directory, CancellationToken cancellationToken)
    {
        IEnumerable<FileSystemInfo> children;
        try
        {
            children = directory.EnumerateFileSystemInfos();
        }
        catch
        {
            yield break;
        }

        foreach (var child in children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return child;
        }
    }

    private static CleanupEntry Measure(FileSystemInfo info, string kind, CancellationToken cancellationToken)
    {
        long size = 0;
        DateTime lastWrite = info.LastWriteTime;

        if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
        {
            if (info is FileInfo singleFile)
            {
                try
                {
                    size = singleFile.Length;
                }
                catch
                {
                    size = 0;
                }
            }
            else if (info is DirectoryInfo dir)
            {
                try
                {
                    foreach (var nestedFile in EnumerateFilesSafe(dir, cancellationToken))
                    {
                        try
                        {
                            size += nestedFile.Length;
                            if (nestedFile.LastWriteTime > lastWrite)
                            {
                                lastWrite = nestedFile.LastWriteTime;
                            }
                        }
                        catch
                        {
                            // Skip files that disappear during the scan.
                        }
                    }
                }
                catch
                {
                    // Keep the partial size.
                }
            }
        }

        return new CleanupEntry
        {
            Path = info.FullName,
            Name = info.Name,
            Kind = kind,
            SizeBytes = size,
            LastWriteTime = lastWrite
        };
    }

    private static IEnumerable<FileInfo> EnumerateFilesSafe(
        DirectoryInfo root,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DirectoryInfo current = pending.Pop();
            IEnumerable<FileSystemInfo> children;
            try
            {
                children = current.EnumerateFileSystemInfos();
            }
            catch
            {
                continue;
            }

            foreach (var child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if (child is DirectoryInfo childDirectory)
                {
                    pending.Push(childDirectory);
                }
                else if (child is FileInfo childFile)
                {
                    yield return childFile;
                }
            }
        }
    }

    private static (long Size, string Text) AnalyzeComponentStore(CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dism.exe",
                Arguments = "/Online /Cleanup-Image /AnalyzeComponentStore",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return (0, "无法启动 DISM");
            }

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit(120000);
            cancellationToken.ThrowIfCancellationRequested();

            if (process.ExitCode != 0)
            {
                return (0, string.IsNullOrWhiteSpace(error) ? "DISM 分析失败" : error.Trim());
            }

            long size = ParseReclaimableSize(output);
            string note = size > 0
                ? "系统组件存储存在可回收内容"
                : "系统组件存储当前没有建议回收的内容";
            return (size, note);
        }
        catch (Exception ex)
        {
            return (0, $"DISM 分析失败: {ex.Message}");
        }
    }

    private static long ParseReclaimableSize(string output)
    {
        // DISM output is localized; look for common labels and parse the first
        // byte-like value on that line.
        string[] labels =
        [
            "可回收的包",
            "Reclaimable Packages",
            "可回收的程序包",
            "Reclaimable Packages :"
        ];

        foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!labels.Any(label => line.Contains(label, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var match = System.Text.RegularExpressions.Regex.Match(
                line,
                @"([\d.,]+)\s*(B|KB|MB|GB|TB)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success && double.TryParse(match.Groups[1].Value.Replace(",", ""), out double value))
            {
                string unit = match.Groups[2].Value.ToUpperInvariant();
                double multiplier = unit switch
                {
                    "TB" => 1024d * 1024 * 1024 * 1024,
                    "GB" => 1024d * 1024 * 1024,
                    "MB" => 1024d * 1024,
                    "KB" => 1024d,
                    _ => 1d
                };
                return (long)(value * multiplier);
            }
        }

        return 0;
    }

    private List<ScanRule> BuildRules()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string gradle = Path.Combine(_userProfile, ".gradle");
        string nuget = Path.Combine(_userProfile, ".nuget");
        string cache = Path.Combine(_userProfile, ".cache");
        string npm = Path.Combine(localAppData, "npm-cache");
        string pip = Path.Combine(localAppData, "pip", "Cache");
        string yarn = Path.Combine(localAppData, "Yarn", "Cache");
        string[] pnpmCaches =
        [
            Path.Combine(localAppData, "pnpm-cache"),
            Path.Combine(localAppData, "pnpm-state"),
            Path.Combine(localAppData, "pnpm-store-operation-locks")
        ];
        string goBuild = Path.Combine(localAppData, "go-build");

        return
        [
            new ScanRule
            {
                Id = "temp-user",
                Name = "用户临时文件",
                Description = "浏览器、安装器及应用运行产生的临时副本",
                Recommendation = "可安全删除 7 天前的普通文件",
                Risk = RiskLevel.Safe,
                DefaultSelected = true,
                Kind = RuleKind.DirectoryContents,
                Paths = [Path.Combine(localAppData, "Temp")],
                MinAgeDays = 7
            },
            new ScanRule
            {
                Id = "temp-windows",
                Name = "Windows 临时文件",
                Description = "系统安装和更新过程中留下的临时文件",
                Recommendation = "可安全删除 7 天前的普通文件",
                Risk = RiskLevel.Safe,
                DefaultSelected = true,
                RequiresAdmin = true,
                Kind = RuleKind.DirectoryContents,
                Paths = [Path.Combine(windows, "Temp")],
                MinAgeDays = 7
            },
            new ScanRule
            {
                Id = "crash-dumps",
                Name = "崩溃转储",
                Description = "应用崩溃后生成的内存转储文件",
                Recommendation = "除排查特定故障外不再需要",
                Risk = RiskLevel.Safe,
                DefaultSelected = true,
                Kind = RuleKind.DirectoryContents,
                Paths = [Path.Combine(localAppData, "CrashDumps")]
            },
            new ScanRule
            {
                Id = "pip-cache",
                Name = "pip 缓存",
                Description = "Python 包下载缓存，可重新下载",
                Recommendation = "可清理，下次安装依赖会稍慢",
                Risk = RiskLevel.Safe,
                DefaultSelected = true,
                Kind = RuleKind.DirectoryContents,
                Paths = [pip]
            },
            new ScanRule
            {
                Id = "npm-cache",
                Name = "npm 缓存",
                Description = "Node.js 包下载缓存，可重新下载",
                Recommendation = "可清理，下次安装依赖会稍慢",
                Risk = RiskLevel.Safe,
                DefaultSelected = true,
                Kind = RuleKind.DirectoryContents,
                Paths = [npm]
            },
            new ScanRule
            {
                Id = "yarn-cache",
                Name = "Yarn 缓存",
                Description = "Yarn 包下载缓存，可重新下载",
                Recommendation = "可清理，下次安装依赖会稍慢",
                Risk = RiskLevel.Safe,
                DefaultSelected = true,
                Kind = RuleKind.DirectoryContents,
                Paths = [yarn]
            },
            new ScanRule
            {
                Id = "pnpm-cache",
                Name = "pnpm 缓存",
                Description = "pnpm 下载缓存和状态文件，可重新生成",
                Recommendation = "可清理，下次安装依赖会稍慢",
                Risk = RiskLevel.Safe,
                DefaultSelected = true,
                Kind = RuleKind.DirectoryContents,
                Paths = pnpmCaches
            },
            new ScanRule
            {
                Id = "go-build",
                Name = "Go 构建缓存",
                Description = "Go 编译中间产物，可自动重建",
                Recommendation = "可清理，首次重新编译会稍慢",
                Risk = RiskLevel.Safe,
                DefaultSelected = true,
                Kind = RuleKind.DirectoryContents,
                Paths = [goBuild]
            },
            new ScanRule
            {
                Id = "gradle-cache",
                Name = "Gradle 缓存",
                Description = "Gradle 依赖与构建缓存，可重新下载",
                Recommendation = "可清理，首次构建会稍慢",
                Risk = RiskLevel.Safe,
                DefaultSelected = true,
                Kind = RuleKind.DirectoryContents,
                Paths = [Path.Combine(gradle, "caches")]
            },
            new ScanRule
            {
                Id = "nuget-cache",
                Name = "NuGet 缓存",
                Description = ".NET 包缓存与临时包目录，可重新下载",
                Recommendation = "可清理，首次还原会稍慢",
                Risk = RiskLevel.Safe,
                DefaultSelected = true,
                Kind = RuleKind.DirectoryContents,
                Paths = [Path.Combine(nuget, "packages"), Path.Combine(localAppData, "NuGet")]
            },
            new ScanRule
            {
                Id = "windows-update",
                Name = "Windows 更新下载缓存",
                Description = "已安装或已过期更新的下载副本",
                Recommendation = "可清理，下次更新可能需要重新下载",
                Risk = RiskLevel.Medium,
                DefaultSelected = false,
                RequiresAdmin = true,
                Kind = RuleKind.DirectoryContents,
                Paths = [Path.Combine(windows, "SoftwareDistribution", "Download")]
            },
            new ScanRule
            {
                Id = "package-cache",
                Name = "安装器缓存",
                Description = "Visual Studio、驱动和桌面软件的安装包缓存",
                Recommendation = "清理后修复或卸载可能需要重新下载安装包",
                Risk = RiskLevel.Medium,
                DefaultSelected = false,
                RequiresAdmin = true,
                Kind = RuleKind.DirectoryContents,
                Paths =
                [
                    Path.Combine(localAppData, "Package Cache"),
                    Path.Combine(programData, "Package Cache")
                ]
            },
            new ScanRule
            {
                Id = "recycle-bin",
                Name = "回收站（所有固定磁盘）",
                Description = "所有本地固定磁盘中已删除但仍占用空间的文件",
                Recommendation = "确认不再需要还原后即可清空",
                Risk = RiskLevel.Medium,
                DefaultSelected = false,
                Kind = RuleKind.RecycleBin,
                Paths = []
            },
            new ScanRule
            {
                Id = "model-cache",
                Name = "模型与数据集缓存",
                Description = "Hugging Face、ModelScope、Whisper 等缓存",
                Recommendation = "仅在确定模型不再使用时可清理",
                Risk = RiskLevel.Advanced,
                DefaultSelected = false,
                Kind = RuleKind.DirectoryContents,
                Paths =
                [
                    Path.Combine(cache, "huggingface"),
                    Path.Combine(cache, "modelscope"),
                    Path.Combine(cache, "whisper")
                ]
            },
            new ScanRule
            {
                Id = "hibernation",
                Name = "休眠文件",
                Description = "用于休眠和快速启动的大型系统文件",
                Recommendation = "关闭休眠可释放，但会影响快速启动与休眠",
                Risk = RiskLevel.Advanced,
                DefaultSelected = false,
                RequiresAdmin = true,
                Kind = RuleKind.SingleFile,
                Paths = [Path.Combine(Path.GetPathRoot(windows) ?? "C:\\", "hiberfil.sys")]
            },
            new ScanRule
            {
                Id = "dism",
                Name = "Windows 组件存储",
                Description = "WinSxS 中可回收的旧组件包",
                Recommendation = "仅在系统分析明确建议时清理",
                Risk = RiskLevel.Advanced,
                DefaultSelected = false,
                RequiresAdmin = true,
                Kind = RuleKind.DismAnalyze,
                Paths = []
            }
        ];
    }

    private sealed class ScanRule
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Description { get; init; }
        public required string Recommendation { get; init; }
        public required RiskLevel Risk { get; init; }
        public required bool DefaultSelected { get; init; }
        public bool RequiresAdmin { get; init; }
        public required RuleKind Kind { get; init; }
        public required string[] Paths { get; init; }
        public int? MinAgeDays { get; init; }
    }

    private enum RuleKind
    {
        DirectoryContents,
        SingleFile,
        RecycleBin,
        DismAnalyze
    }
}
