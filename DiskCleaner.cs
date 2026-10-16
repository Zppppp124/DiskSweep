using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DiskSweep;

public sealed class DiskCleaner
{
    private static readonly Dictionary<string, string[]> RuleRoots;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);

    static DiskCleaner()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var recycleRoots = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin"))
            .ToArray();

        RuleRoots = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["temp-user"] = [Path.Combine(localAppData, "Temp")],
            ["temp-windows"] = [Path.Combine(windows, "Temp")],
            ["crash-dumps"] = [Path.Combine(localAppData, "CrashDumps")],
            ["pip-cache"] = [Path.Combine(localAppData, "pip", "Cache")],
            ["npm-cache"] = [Path.Combine(localAppData, "npm-cache")],
            ["yarn-cache"] = [Path.Combine(localAppData, "Yarn", "Cache")],
            ["pnpm-cache"] =
            [
                Path.Combine(localAppData, "pnpm-cache"),
                Path.Combine(localAppData, "pnpm-state"),
                Path.Combine(localAppData, "pnpm-store-operation-locks")
            ],
            ["go-build"] = [Path.Combine(localAppData, "go-build")],
            ["gradle-cache"] = [Path.Combine(profile, ".gradle", "caches")],
            ["nuget-cache"] =
            [
                Path.Combine(profile, ".nuget", "packages"),
                Path.Combine(localAppData, "NuGet")
            ],
            ["windows-update"] = [Path.Combine(windows, "SoftwareDistribution", "Download")],
            ["package-cache"] =
            [
                Path.Combine(localAppData, "Package Cache"),
                Path.Combine(programData, "Package Cache")
            ],
            ["model-cache"] =
            [
                Path.Combine(profile, ".cache", "huggingface"),
                Path.Combine(profile, ".cache", "modelscope"),
                Path.Combine(profile, ".cache", "whisper")
            ],
            ["recycle-bin"] = recycleRoots
        };
    }

    public Task<CleanupResult> CleanAsync(
        CleanupCategory category,
        IProgress<CleanupProgress>? progress,
        CancellationToken cancellationToken)
    {
        return Task.Run(() => Clean(category, progress, cancellationToken), cancellationToken);
    }

    public CleanupResult Clean(
        CleanupCategory category,
        IProgress<CleanupProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (category.Id == "recycle-bin")
        {
            return CleanRecycleBin(category, progress, cancellationToken);
        }

        if (category.Id == "hibernation")
        {
            return CleanHibernation(category, progress, cancellationToken);
        }

        if (category.Id == "dism")
        {
            return CleanComponentStore(category, progress, cancellationToken);
        }

        var entries = category.Entries.ToList();
        var failures = new List<string>();
        int deleted = 0;
        int skipped = 0;
        int failed = 0;
        long freed = 0;

        for (int i = 0; i < entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[i];
            progress?.Report(new CleanupProgress
            {
                CategoryName = category.Name,
                Path = entry.Path,
                Completed = i,
                Total = entries.Count,
                FreedBytes = freed
            });

            if (!IsPathAllowed(category.Id, entry.Path))
            {
                skipped++;
                failures.Add($"跳过不安全路径: {entry.Path}");
                continue;
            }

            try
            {
                if (File.Exists(entry.Path))
                {
                    var info = new FileInfo(entry.Path);
                    long size = info.Length;
                    File.SetAttributes(entry.Path, FileAttributes.Normal);
                    File.Delete(entry.Path);
                    deleted++;
                    freed += size;
                }
                else if (Directory.Exists(entry.Path))
                {
                    long size = entry.SizeBytes;
                    DeleteDirectory(entry.Path);
                    deleted++;
                    freed += size;
                }
                else
                {
                    skipped++;
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                failed++;
                failures.Add($"{entry.Path}: {ex.Message}");
            }
            catch (IOException ex)
            {
                skipped++;
                failures.Add($"{entry.Path}: 被占用或暂时无法删除 ({ex.Message})");
            }
            catch (Exception ex)
            {
                failed++;
                failures.Add($"{entry.Path}: {ex.Message}");
            }
        }

        progress?.Report(new CleanupProgress
        {
            CategoryName = category.Name,
            Path = "完成",
            Completed = entries.Count,
            Total = entries.Count,
            FreedBytes = freed
        });

        return new CleanupResult
        {
            RuleId = category.Id,
            Deleted = deleted,
            Skipped = skipped,
            Failed = failed,
            FreedBytes = freed,
            Message = $"删除 {deleted} 项，跳过 {skipped} 项，失败 {failed} 项",
            Failures = failures
        };
    }

    private static CleanupResult CleanRecycleBin(
        CleanupCategory category,
        IProgress<CleanupProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int emptied = 0;
        int failed = 0;
        var failures = new List<string>();
        var drives = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .ToList();

        for (int i = 0; i < drives.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var drive = drives[i];
            progress?.Report(new CleanupProgress
            {
                CategoryName = category.Name,
                Path = drive.Name,
                Completed = i,
                Total = drives.Count
            });

            int result = SHEmptyRecycleBin(IntPtr.Zero, drive.RootDirectory.FullName, 0x7);
            if (result == 0 || result == unchecked((int)0x8000FFFF))
            {
                emptied++;
            }
            else
            {
                failed++;
                failures.Add($"{drive.Name}: Shell API 返回 0x{result:X8}");
            }
        }

        progress?.Report(new CleanupProgress
        {
            CategoryName = category.Name,
            Path = "完成",
            Completed = drives.Count,
            Total = drives.Count,
            FreedBytes = category.SizeBytes
        });

        return new CleanupResult
        {
            RuleId = category.Id,
            Deleted = emptied,
            Failed = failed,
            FreedBytes = emptied > 0 ? category.SizeBytes : 0,
            Message = emptied > 0
                ? $"已清空 {emptied} 个磁盘的回收站"
                : "回收站没有可清空内容",
            Failures = failures
        };
    }

    private static CleanupResult CleanHibernation(
        CleanupCategory category,
        IProgress<CleanupProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new CleanupProgress
        {
            CategoryName = "休眠文件",
            Path = "powercfg /hibernate off",
            Completed = 0,
            Total = 1
        });

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powercfg.exe",
                Arguments = "/hibernate off",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return FailedResult("休眠文件", "无法启动 powercfg");
            }

            string error = process.StandardError.ReadToEnd();
            process.WaitForExit(30000);
            cancellationToken.ThrowIfCancellationRequested();

            if (process.ExitCode != 0)
            {
                return FailedResult("休眠文件", string.IsNullOrWhiteSpace(error)
                    ? $"powercfg 返回 {process.ExitCode}"
                    : error.Trim());
            }

            progress?.Report(new CleanupProgress
            {
                CategoryName = "休眠文件",
                Path = "完成",
                Completed = 1,
                Total = 1,
                FreedBytes = category.SizeBytes
            });

            return new CleanupResult
            {
                RuleId = "hibernation",
                Deleted = 1,
                FreedBytes = category.SizeBytes,
                Message = "已关闭休眠并移除休眠文件"
            };
        }
        catch (Exception ex)
        {
            return FailedResult("休眠文件", ex.Message);
        }
    }

    private static CleanupResult CleanComponentStore(
        CleanupCategory category,
        IProgress<CleanupProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new CleanupProgress
        {
            CategoryName = "Windows 组件存储",
            Path = "DISM /StartComponentCleanup",
            Completed = 0,
            Total = 1
        });

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dism.exe",
                Arguments = "/Online /Cleanup-Image /StartComponentCleanup",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return FailedResult("Windows 组件存储", "无法启动 DISM");
            }

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit(600000);
            cancellationToken.ThrowIfCancellationRequested();

            if (process.ExitCode != 0)
            {
                return FailedResult("Windows 组件存储", string.IsNullOrWhiteSpace(error)
                    ? $"DISM 返回 {process.ExitCode}"
                    : error.Trim());
            }

            progress?.Report(new CleanupProgress
            {
                CategoryName = "Windows 组件存储",
                Path = "完成",
                Completed = 1,
                Total = 1
            });

            return new CleanupResult
            {
                RuleId = "dism",
                Deleted = 1,
                Message = "Windows 组件存储清理命令已完成"
            };
        }
        catch (Exception ex)
        {
            return FailedResult("Windows 组件存储", ex.Message);
        }
    }

    private static CleanupResult FailedResult(string name, string message) => new()
    {
        RuleId = name,
        Failed = 1,
        Message = message,
        Failures = [message]
    };

    private static void DeleteDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            directory.Delete();
            return;
        }

        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                if (entry is DirectoryInfo linkDirectory)
                {
                    linkDirectory.Delete();
                }
                else
                {
                    File.Delete(entry.FullName);
                }
                continue;
            }

            if (entry is DirectoryInfo child)
            {
                DeleteDirectory(child.FullName);
            }
            else
            {
                File.SetAttributes(entry.FullName, FileAttributes.Normal);
                File.Delete(entry.FullName);
            }
        }

        directory.Delete();
    }

    public static bool IsPathAllowed(string ruleId, string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            if (!RuleRoots.TryGetValue(ruleId, out var roots))
            {
                return false;
            }

            foreach (string root in roots)
            {
                string normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (full.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (full.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }
}
