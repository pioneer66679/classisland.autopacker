using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClassIsland.AutoPacker.Core;

/// <summary>打包进度回调。</summary>
public sealed class PackProgress
{
    public string Phase { get; init; } = "";
    public int Done { get; init; }
    public int Total { get; init; }
    public string Current { get; init; } = "";

    public double Percent => Total <= 0 ? 0 : Math.Min(100.0, Done * 100.0 / Total);
}

/// <summary>打包结果。</summary>
public sealed class PackResult
{
    public bool Success { get; init; }
    public string OutputPath { get; init; } = "";
    public long SizeBytes { get; init; }
    public int FileCount { get; init; }
    public int SkippedCount { get; init; }
    public TimeSpan Elapsed { get; init; }
    public string? Error { get; init; }
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// 打包配置。
/// </summary>
public sealed class PackerConfig
{
    /// <summary>要打包的源目录（留空 = 自动探测 ClassIsland 程序根目录）。</summary>
    public string SourceFolder { get; set; } = "";

    /// <summary>输出目录（zip 放这里）。</summary>
    public string OutputFolder { get; set; } = "";

    /// <summary>文件名模板，支持 {name} {date} {time} {version} {stamp}。</summary>
    public string FileNameTemplate { get; set; } = "ClassIsland备份_{stamp}";

    /// <summary>压缩级别：0=最快(不压缩) 1=较快 2=标准 3=最大压缩。</summary>
    public int CompressionLevel { get; set; } = 2;

    /// <summary>是否在打包后自动清理旧文件（保留最近 N 份）。</summary>
    public bool AutoCleanOld { get; set; } = false;

    /// <summary>保留份数（配合 AutoCleanOld）。</summary>
    public int KeepCount { get; set; } = 5;

    /// <summary>排除规则（通配符，匹配相对路径或文件名）。</summary>
    public List<string> ExcludePatterns { get; set; } = new()
    {
        "*.log", "*.tmp", "*.pdb", "*.zip", "*.7z", "*.rar",
        "Cache\\*", "Logs\\*", "logs\\*", "CrashDumps\\*",
        "Backups\\*", "backups\\*"
    };

    /// <summary>是否包含 data 目录（配置/插件）。</summary>
    public bool IncludeData { get; set; } = true;

    /// <summary>是否只打包 data 目录。</summary>
    public bool OnlyData { get; set; } = false;

    /// <summary>是否验证打包结果的完整性（重新打开 zip 校验条目数）。</summary>
    public bool VerifyAfterPack { get; set; } = true;
}

/// <summary>
/// 打包引擎。
///
/// 关键防护：
///   1. 输出目录若位于源目录内部，务必排除 —— 否则本次正在写的 zip 可能被自己打包，
///      多次打包会滚成巨大的嵌套文件。这里用「规范化全路径前缀比较」来识别。
///   2. 目标 zip 本身（含 .tmp 临时名）永远排除。
///   3. 文件被占用（ClassIsland 正在运行写日志等）时跳过并记警告，不整体失败。
/// </summary>
public static class Packer
{
    private static readonly string[] SizeUnits = { "B", "KB", "MB", "GB", "TB" };

    public static string FormatSize(long bytes)
    {
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < SizeUnits.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {SizeUnits[i]}";
    }

    /// <summary>定位 ClassIsland 程序根目录（含 data 与 app-* 的那一层）。</summary>
    public static string? LocateInstallRoot()
    {
        // 插件位于 <CL>\data\Plugins\<id>\ → 往上三级即程序根
        var baseDir = AppContext.BaseDirectory;

        var candidates = new List<string>();
        try
        {
            candidates.Add(Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..")));
            candidates.Add(Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..")));
        }
        catch { /* 路径异常忽略 */ }

        // 逐级向上收集候选
        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            candidates.Add(dir.FullName);

        // 用户级数据目录也作为兜底候选
        try
        {
            candidates.Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        }
        catch { }

        // 打分挑选：同时有 data\Profiles 与 app-* 目录的最优先
        string? best = null;
        var bestScore = 0;

        foreach (var c in candidates.Distinct())
        {
            if (string.IsNullOrWhiteSpace(c) || !Directory.Exists(c)) continue;

            var score = 0;

            if (Directory.Exists(Path.Combine(c, "data", "Profiles"))) score += 4;
            else if (Directory.Exists(Path.Combine(c, "data"))) score += 2;

            try
            {
                if (Directory.GetDirectories(c, "app-*").Length > 0) score += 2;
                if (Directory.GetFiles(c, "ClassIsland*.dll").Length > 0) score += 1;
                if (Directory.GetFiles(c, "ClassIsland*.exe").Length > 0) score += 1;
            }
            catch { }

            // 得分相同则偏好路径更短的（更接近根）
            if (score > bestScore || (score == bestScore && score > 0 &&
                best is not null && c.Length < best.Length))
            {
                bestScore = score;
                best = c;
            }
        }

        return bestScore > 0 ? best : null;
    }

    public static string BuildFileName(PackerConfig cfg, string rootName)
    {
        var now = DateTime.Now;
        var t = string.IsNullOrWhiteSpace(cfg.FileNameTemplate)
            ? "ClassIsland备份_{stamp}"
            : cfg.FileNameTemplate;

        var name = t
            .Replace("{name}", rootName)
            .Replace("{date}", now.ToString("yyyyMMdd"))
            .Replace("{time}", now.ToString("HHmmss"))
            .Replace("{stamp}", now.ToString("yyyyMMdd_HHmmss"))
            .Replace("{version}", now.ToString("yyyyMMdd.HHmm"));

        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');

        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            name += ".zip";

        return name;
    }

    /// <summary>
    /// 执行打包。
    /// </summary>
    public static async Task<PackResult> PackAsync(
        PackerConfig cfg,
        IProgress<PackProgress>? progress = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var warnings = new List<string>();
        var skipped = 0;

        try
        {
            // ---- 解析源目录 ----
            var root = cfg.SourceFolder;
            if (string.IsNullOrWhiteSpace(root))
                root = LocateInstallRoot() ?? "";

            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return new PackResult
                {
                    Success = false,
                    Error = "找不到要打包的目录：" + (string.IsNullOrWhiteSpace(root) ? "(自动探测失败)" : root),
                    Elapsed = sw.Elapsed
                };
            }

            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);

            // ---- 解析输出目录 ----
            if (string.IsNullOrWhiteSpace(cfg.OutputFolder))
            {
                return new PackResult
                {
                    Success = false,
                    Error = "还没设置输出文件夹。",
                    Elapsed = sw.Elapsed
                };
            }

            var outDir = Path.GetFullPath(cfg.OutputFolder);
            Directory.CreateDirectory(outDir);

            var rootName = new DirectoryInfo(root).Name;
            var zipName = BuildFileName(cfg, rootName);
            var zipPath = Path.Combine(outDir, zipName);

            // 同名则加序号，绝不覆盖已有备份
            var n = 1;
            while (File.Exists(zipPath) || File.Exists(zipPath + ".tmp"))
            {
                var baseName = Path.GetFileNameWithoutExtension(zipName);
                zipPath = Path.Combine(outDir, $"{baseName}({n}).zip");
                n++;
            }

            // ---- 决定打包范围 ----
            var packRoot = root;
            if (cfg.OnlyData)
            {
                var dataDir = Path.Combine(root, "data");
                if (Directory.Exists(dataDir))
                    packRoot = dataDir;
                else
                    warnings.Add("设置了「只打包 data」，但源目录下没有 data，改为打包整个目录。");
            }

            // ---- 收集文件 ----
            progress?.Report(new PackProgress { Phase = "正在扫描文件…" });

            var outDirNorm = outDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var zipNorm = Path.GetFullPath(zipPath);
            var tmpNorm = Path.GetFullPath(zipPath + ".tmp");

            var files = new List<(string Full, string Rel)>();
            var packRootNorm = Path.GetFullPath(packRoot).TrimEnd(Path.DirectorySeparatorChar);

            await Task.Run(() =>
            {
                foreach (var f in Directory.EnumerateFiles(packRoot, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();

                    var full = Path.GetFullPath(f);

                    // 1) 输出目录整体排除（防止把自己包进去）
                    if (full.StartsWith(outDirNorm, StringComparison.OrdinalIgnoreCase))
                    {
                        skipped++;
                        continue;
                    }

                    // 2) 目标 zip 与临时文件排除
                    if (string.Equals(full, zipNorm, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(full, tmpNorm, StringComparison.OrdinalIgnoreCase))
                    {
                        skipped++;
                        continue;
                    }

                    // 3) data 排除（若设置为不包含）
                    var rel = Path.GetRelativePath(packRootNorm, full);
                    if (!cfg.IncludeData && !cfg.OnlyData)
                    {
                        var firstSeg = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                        if (string.Equals(firstSeg, "data", StringComparison.OrdinalIgnoreCase))
                        {
                            skipped++;
                            continue;
                        }
                    }

                    // 4) 排除规则
                    if (IsExcluded(rel, cfg.ExcludePatterns))
                    {
                        skipped++;
                        continue;
                    }

                    files.Add((full, rel));
                }
            }, ct);

            if (files.Count == 0)
            {
                return new PackResult
                {
                    Success = false,
                    Error = "没有可打包的文件（可能全被排除规则过滤掉了）。",
                    Elapsed = sw.Elapsed
                };
            }

            // ---- 写入 zip ----
            var level = cfg.CompressionLevel switch
            {
                0 => CompressionLevel.NoCompression,
                1 => CompressionLevel.Fastest,
                3 => CompressionLevel.SmallestSize,
                _ => CompressionLevel.Optimal
            };

            var tmp = zipPath + ".tmp";

            await Task.Run(() =>
            {
                using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
                using var archive = new ZipArchive(fs, ZipArchiveMode.Create);

                var i = 0;
                foreach (var (full, rel) in files)
                {
                    ct.ThrowIfCancellationRequested();

                    i++;
                    if (i % 20 == 0 || i == files.Count)
                        progress?.Report(new PackProgress
                        {
                            Phase = "正在压缩…",
                            Done = i,
                            Total = files.Count,
                            Current = rel
                        });

                    try
                    {
                        var entry = archive.CreateEntry(rel.Replace('\\', '/'), level);
                        entry.LastWriteTime = File.GetLastWriteTime(full);

                        using var src = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var dst = entry.Open();
                        src.CopyTo(dst);
                    }
                    catch (IOException ex)
                    {
                        // 文件被占用（CL 正在写日志等）：跳过，不整体失败
                        warnings.Add($"跳过被占用的文件：{rel}（{ex.Message}）");
                        skipped++;
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        warnings.Add($"跳过无权限的文件：{rel}（{ex.Message}）");
                        skipped++;
                    }
                }
            }, ct);

            // ---- 原子替换 ----
            if (File.Exists(zipPath)) File.Delete(zipPath);
            File.Move(tmp, zipPath);

            // ---- 校验 ----
            var verified = files.Count;
            if (cfg.VerifyAfterPack)
            {
                progress?.Report(new PackProgress { Phase = "正在校验…" });
                try
                {
                    using var za = ZipFile.OpenRead(zipPath);
                    var entries = za.Entries.Count;
                    if (entries < files.Count - skipped)
                        warnings.Add($"校验提示：压缩包内条目数 {entries} 少于预期 {files.Count - skipped}。");
                    verified = entries;
                }
                catch (Exception ex)
                {
                    warnings.Add("校验时无法重新打开压缩包：" + ex.Message);
                }
            }

            // ---- 清理旧备份 ----
            if (cfg.AutoCleanOld && cfg.KeepCount > 0)
            {
                try
                {
                    var olds = new DirectoryInfo(outDir).GetFiles("*.zip")
                        .OrderByDescending(x => x.LastWriteTime)
                        .Skip(cfg.KeepCount)
                        .ToList();

                    foreach (var o in olds)
                    {
                        try { o.Delete(); warnings.Add("已清理旧备份：" + o.Name); }
                        catch { /* 占用则留着 */ }
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add("清理旧备份失败：" + ex.Message);
                }
            }

            sw.Stop();

            var result = new PackResult
            {
                Success = true,
                OutputPath = zipPath,
                SizeBytes = new FileInfo(zipPath).Length,
                FileCount = files.Count,
                SkippedCount = skipped,
                Elapsed = sw.Elapsed
            };
            result.Warnings.AddRange(warnings);
            return result;
        }
        catch (OperationCanceledException)
        {
            return new PackResult { Success = false, Error = "已取消。", Elapsed = sw.Elapsed };
        }
        catch (Exception ex)
        {
            return new PackResult { Success = false, Error = ex.Message, Elapsed = sw.Elapsed };
        }
    }

    /// <summary>通配符排除判断：支持 * 与 ?，既匹配文件名也匹配整条相对路径。</summary>
    public static bool IsExcluded(string relPath, IEnumerable<string> patterns)
    {
        var rel = relPath.Replace('\\', '/');
        var fileName = Path.GetFileName(relPath);

        foreach (var p in patterns)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;

            var pat = p.Trim().Replace('\\', '/');

            // 目录前缀形式（"Cache\*"）→ 前缀匹配
            if (pat.EndsWith("/*") || pat.EndsWith("\\*"))
            {
                var prefix = pat.Substring(0, pat.Length - 1).TrimEnd('/');
                if (rel.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase) ||
                    rel.Equals(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
                continue;
            }

            if (WildcardMatch(fileName, pat, ignoreCase: true)) return true;
            if (WildcardMatch(rel, pat, ignoreCase: true)) return true;
        }

        return false;
    }

    /// <summary>简单的通配符匹配（* 与 ?），无正则依赖。</summary>
    public static bool WildcardMatch(string input, string pattern, bool ignoreCase)
    {
        var cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        int i = 0, p = 0, star = -1, mark = 0;

        while (i < input.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' ||
                string.Compare(pattern, p, input, i, 1, cmp) == 0))
            {
                i++; p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = i;
            }
            else if (star != -1)
            {
                p = star + 1;
                i = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}
