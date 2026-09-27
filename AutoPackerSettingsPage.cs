using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ClassIsland.AutoPacker.Core;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Enums.SettingsWindow;
using Microsoft.Extensions.Logging;

namespace ClassIsland.AutoPacker;

/// <summary>
/// 独立配置界面（设置 → ClassIsland 一键打包）。
/// </summary>
[SettingsPageInfo("classisland.autopacker.settings", "ClassIsland 一键打包", SettingsPageCategory.External)]
public class AutoPackerSettingsPage : SettingsPageBase
{
    private readonly ILogger<AutoPackerSettingsPage>? _logger;

    private readonly TextBox _sourceBox = new() { Watermark = "留空则自动探测 ClassIsland 程序目录" };
    private readonly TextBox _outputBox = new() { Watermark = "zip 输出到哪个文件夹（必填）" };
    private readonly TextBox _nameTemplateBox = new();
    private readonly ComboBox _levelBox = new() { Width = 260 };
    private readonly CheckBox _includeData = new() { Content = "包含 data 目录（配置 + 插件）" };
    private readonly CheckBox _onlyData = new() { Content = "只打包 data 目录（不含主程序，体积小）" };
    private readonly CheckBox _verify = new() { Content = "打包后校验压缩包完整性" };
    private readonly CheckBox _autoClean = new() { Content = "自动清理旧备份" };
    private readonly NumericUpDown _keepCount = new() { Minimum = 1, Maximum = 999, Value = 5, Width = 110 };
    private readonly TextBox _excludeBox = new()
    {
        AcceptsReturn = true,
        Height = 90,
        TextWrapping = TextWrapping.NoWrap,
        FontFamily = new FontFamily("Consolas, monospace")
    };
    private readonly ProgressBar _progress = new() { Height = 8, IsVisible = false };
    private readonly TextBlock _progressText = new() { FontSize = 12, Opacity = 0.8 };
    private readonly TextBox _logBox = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        Height = 190,
        TextWrapping = TextWrapping.NoWrap,
        FontFamily = new FontFamily("Consolas, monospace")
    };
    private readonly TextBlock _statusText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _historyList = new() { Height = 110 };

    private PackerConfig _config = new();
    private string _configPath = "";
    private CancellationTokenSource? _cts;

    private static string ConfigFolderOverride { get; set; } = "";
    private string PluginConfigFolder => string.IsNullOrWhiteSpace(ConfigFolderOverride)
        ? AppContext.BaseDirectory
        : ConfigFolderOverride;

    public static void SetConfigFolder(string folder) => ConfigFolderOverride = folder ?? "";

    public AutoPackerSettingsPage(ILogger<AutoPackerSettingsPage>? logger = null)
    {
        _logger = logger;

        _levelBox.Items.Add("最快（不压缩，体积最大）");
        _levelBox.Items.Add("较快");
        _levelBox.Items.Add("标准（推荐）");
        _levelBox.Items.Add("最大压缩（最慢，体积最小）");
        _levelBox.SelectedIndex = 2;

        Content = BuildUi();
        LoadConfig();
        RefreshStatus();
        RefreshHistory();
    }

    // ---------------- UI ----------------

    private Control BuildUi()
    {
        var panel = new StackPanel
        {
            Spacing = 10,
            Margin = new Avalonia.Thickness(16)
        };

        panel.Children.Add(Header("一键打包 ClassIsland"));
        panel.Children.Add(Hint(
            "把 ClassIsland 目录整体压缩成 zip 输出到指定文件夹，适合备份或迁移到班级大屏。" +
            "输出目录若在源目录内部，会被自动排除，不会把压缩包自己包进去。"));

        panel.Children.Add(Header("当前状态"));
        panel.Children.Add(_statusText);

        panel.Children.Add(Header("打包范围"));
        panel.Children.Add(new TextBlock { Text = "源目录：", FontSize = 12, Opacity = 0.8 });
        panel.Children.Add(_sourceBox);
        var srcRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        srcRow.Children.Add(MakeButton("自动探测", (_, _) => { AutoDetect(); }));
        srcRow.Children.Add(MakeButton("浏览…", OnPickSource));
        panel.Children.Add(srcRow);
        panel.Children.Add(_includeData);
        panel.Children.Add(_onlyData);

        panel.Children.Add(Header("输出位置"));
        panel.Children.Add(_outputBox);
        var outRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        outRow.Children.Add(MakeButton("选择输出文件夹…", OnPickOutput));
        outRow.Children.Add(MakeButton("打开输出文件夹", OnOpenOutput));
        panel.Children.Add(outRow);

        panel.Children.Add(new TextBlock { Text = "文件名模板（可用 {name} {date} {time} {stamp}）：", FontSize = 12, Opacity = 0.8 });
        panel.Children.Add(_nameTemplateBox);

        panel.Children.Add(Header("压缩选项"));
        var lvlRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        lvlRow.Children.Add(new TextBlock { Text = "压缩级别：", VerticalAlignment = VerticalAlignment.Center });
        lvlRow.Children.Add(_levelBox);
        panel.Children.Add(lvlRow);
        panel.Children.Add(_verify);

        panel.Children.Add(Header("排除规则（每行一条，支持 * 与 ?）"));
        panel.Children.Add(_excludeBox);

        panel.Children.Add(Header("旧备份清理"));
        panel.Children.Add(_autoClean);
        var keepRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        keepRow.Children.Add(new TextBlock { Text = "保留最近：", VerticalAlignment = VerticalAlignment.Center });
        keepRow.Children.Add(_keepCount);
        keepRow.Children.Add(new TextBlock { Text = "份", VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(keepRow);

        panel.Children.Add(Header("执行"));
        var runRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        runRow.Children.Add(MakeButton("📦 立即打包", OnPack));
        runRow.Children.Add(MakeButton("取消", (_, _) => { _cts?.Cancel(); Log("已请求取消…"); }));
        runRow.Children.Add(MakeButton("保存设置", (_, _) => { SaveConfig(); Log("设置已保存。"); }));
        panel.Children.Add(runRow);
        panel.Children.Add(_progress);
        panel.Children.Add(_progressText);

        panel.Children.Add(Header("历史记录"));
        panel.Children.Add(_historyList);

        panel.Children.Add(Header("运行日志"));
        panel.Children.Add(new ScrollViewer
        {
            Content = _logBox,
            Height = 190,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        });
        var footRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        footRow.Children.Add(MakeButton("清空日志", (_, _) => _logBox.Text = ""));
        panel.Children.Add(footRow);

        return new ScrollViewer { Content = panel };
    }

    private static TextBlock Header(string s) => new()
    {
        Text = s,
        FontWeight = FontWeight.SemiBold,
        FontSize = 15,
        Margin = new Avalonia.Thickness(0, 8, 0, 0)
    };

    private static TextBlock Hint(string s) => new()
    {
        Text = s,
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.75,
        FontSize = 12
    };

    private static Button MakeButton(string text, EventHandler<Avalonia.Interactivity.RoutedEventArgs> h)
    {
        var b = new Button { Content = text, Padding = new Avalonia.Thickness(14, 6) };
        b.Click += h;
        return b;
    }

    // ---------------- 配置 ----------------

    private void LoadConfig()
    {
        _configPath = Path.Combine(PluginConfigFolder, "Settings.json");

        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                var loaded = JsonSerializer.Deserialize<PackerConfig>(json, JsonOpts);
                if (loaded is not null) _config = loaded;
            }
        }
        catch (Exception ex)
        {
            Log("读取配置失败，使用默认值：" + ex.Message);
            _config = new PackerConfig();
        }

        if (string.IsNullOrWhiteSpace(_config.SourceFolder))
            _config.SourceFolder = Packer.LocateInstallRoot() ?? "";

        _sourceBox.Text = _config.SourceFolder;
        _outputBox.Text = _config.OutputFolder;
        _nameTemplateBox.Text = _config.FileNameTemplate;
        _levelBox.SelectedIndex = Math.Clamp(_config.CompressionLevel, 0, 3);
        _includeData.IsChecked = _config.IncludeData;
        _onlyData.IsChecked = _config.OnlyData;
        _verify.IsChecked = _config.VerifyAfterPack;
        _autoClean.IsChecked = _config.AutoCleanOld;
        _keepCount.Value = _config.KeepCount;
        _excludeBox.Text = string.Join(Environment.NewLine, _config.ExcludePatterns);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private void CollectConfig()
    {
        _config.SourceFolder = _sourceBox.Text?.Trim() ?? "";
        _config.OutputFolder = _outputBox.Text?.Trim() ?? "";
        _config.FileNameTemplate = string.IsNullOrWhiteSpace(_nameTemplateBox.Text)
            ? "ClassIsland备份_{stamp}"
            : _nameTemplateBox.Text!.Trim();
        _config.CompressionLevel = Math.Clamp(_levelBox.SelectedIndex, 0, 3);
        _config.IncludeData = _includeData.IsChecked == true;
        _config.OnlyData = _onlyData.IsChecked == true;
        _config.VerifyAfterPack = _verify.IsChecked == true;
        _config.AutoCleanOld = _autoClean.IsChecked == true;
        _config.KeepCount = (int)(_keepCount.Value ?? 5);
        _config.ExcludePatterns = (_excludeBox.Text ?? "")
            .Split('\n')
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToList();
    }

    private void SaveConfig()
    {
        try
        {
            CollectConfig();
            Directory.CreateDirectory(PluginConfigFolder);
            File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, JsonOpts));
        }
        catch (Exception ex)
        {
            Log("保存配置失败：" + ex.Message);
        }
    }

    // ---------------- 行为 ----------------

    private void AutoDetect()
    {
        var root = Packer.LocateInstallRoot();
        if (root is null)
        {
            Log("✗ 自动探测失败，请手动选择源目录。");
            return;
        }
        _sourceBox.Text = root;
        Log("已探测到程序目录：" + root);
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var src = _sourceBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(src))
            src = Packer.LocateInstallRoot() ?? "";

        var sb = new StringBuilder();
        if (string.IsNullOrWhiteSpace(src) || !Directory.Exists(src))
        {
            _statusText.Text = "⚠ 源目录无效，请先「自动探测」或手动选择。";
            return;
        }

        var rootName = new DirectoryInfo(src).Name;
        var dataDir = Path.Combine(src, "data");
        var hasData = Directory.Exists(dataDir);

        sb.AppendLine($"程序目录：{src}");
        sb.AppendLine($"目录名：{rootName}");
        sb.AppendLine($"data 目录：{(hasData ? dataDir : "（不存在）")}");

        if (hasData)
        {
            var profileDir = Path.Combine(dataDir, "Profiles");
            var pluginDir = Path.Combine(dataDir, "Plugins");
            if (Directory.Exists(profileDir))
                sb.AppendLine($"  档案数：{Directory.GetFiles(profileDir, "*.json").Length}");
            if (Directory.Exists(pluginDir))
                sb.AppendLine($"  已装插件：{Directory.GetDirectories(pluginDir).Length} 个");

            var settingsPath = Path.Combine(dataDir, "Settings.json");
            sb.AppendLine($"  Settings.json：{(File.Exists(settingsPath) ? "有" : "无")}");
        }

        _statusText.Text = sb.ToString();
    }

    private void RefreshHistory()
    {
        _historyList.Items.Clear();

        var outDir = _outputBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(outDir) || !Directory.Exists(outDir)) return;

        try
        {
            var files = new DirectoryInfo(outDir).GetFiles("*.zip")
                .OrderByDescending(f => f.LastWriteTime)
                .Take(20)
                .ToList();

            foreach (var f in files)
                _historyList.Items.Add(
                    $"{f.LastWriteTime:yyyy-MM-dd HH:mm}  {f.Name}  ({Packer.FormatSize(f.Length)})");

            if (files.Count == 0)
                _historyList.Items.Add("（还没有备份文件）");
        }
        catch (Exception ex)
        {
            _historyList.Items.Add("读取失败：" + ex.Message);
        }
    }

    private async void OnPickSource(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var p = await PickFolder("选择要打包的 ClassIsland 目录");
        if (p is null) return;
        _sourceBox.Text = p;
        Log("已选择源目录：" + p);
        RefreshStatus();
    }

    private async void OnPickOutput(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var p = await PickFolder("选择 zip 输出文件夹");
        if (p is null) return;
        _outputBox.Text = p;
        Log("已选择输出目录：" + p);
        RefreshHistory();
    }

    /// <summary>
    /// 统一的文件夹选择：用健壮的解析器拿窗口，失败时给出明确提示而不是静默无反应。
    /// </summary>
    private async Task<string?> PickFolder(string title)
    {
        try
        {
            var top = TopLevelResolver.Resolve(this);
            if (top is null)
            {
                Log("⚠ 无法打开文件夹对话框（拿不到窗口）。");
                foreach (var line in TopLevelResolver.Diagnose(this).Split('\n'))
                    Log("   " + line.TrimEnd());
                Log("   → 可以直接在上面的输入框里手写完整路径。");
                return null;
            }

            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false
            });

            if (folders is null || folders.Count == 0) { Log("已取消选择。"); return null; }

            var p = ToLocalPath(folders[0]);
            if (string.IsNullOrWhiteSpace(p))
            {
                Log("⚠ 选中的文件夹拿不到本地路径，请手动填写完整路径。");
                return null;
            }

            return p;
        }
        catch (Exception ex)
        {
            Log("✗ 选择文件夹失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>从 IStorageItem 取本地路径，多路兜底。</summary>
    private static string ToLocalPath(Avalonia.Platform.Storage.IStorageItem? item)
    {
        if (item is null) return "";

        try
        {
            var lp = item.Path?.LocalPath;
            if (!string.IsNullOrWhiteSpace(lp)) return lp;
        }
        catch { }

        try
        {
            var uri = item.Path;
            if (uri is not null)
            {
                if (uri.IsFile) return uri.LocalPath;
                var s = uri.ToString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }
        catch { }

        return "";
    }

    /// <summary>
    /// 路径预检：把「盘符不存在 / 非法字符 / 无权限」在打包前就看出来，
    /// 而不是打到一半才报错。
    /// </summary>
    private static bool ValidatePath(string path, out string error)
    {
        error = "";

        if (string.IsNullOrWhiteSpace(path)) { error = "路径为空"; return false; }

        try
        {
            // 含非法字符？
            var invalid = Path.GetInvalidPathChars();
            if (path.IndexOfAny(invalid) >= 0) { error = "路径含非法字符"; return false; }

            var full = Path.GetFullPath(path);

            // 盘符/根存在吗？
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) { error = "无法解析根目录"; return false; }
            if (!Directory.Exists(root)) { error = $"根目录不存在（{root}）"; return false; }

            // 已存在就直接过；不存在则试着看父级能否写
            if (Directory.Exists(full)) return true;

            var parent = Path.GetDirectoryName(full);
            while (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                parent = Path.GetDirectoryName(parent);

            if (string.IsNullOrEmpty(parent)) { error = "找不到任何已存在的父目录"; return false; }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void OnOpenOutput(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var outDir = _outputBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(outDir))
            {
                Log("✗ 还没设置输出目录。可以先点「选择输出文件夹…」，或直接手写路径。");
                return;
            }

            // 目录不存在就顺手建出来，不让用户白点一次
            if (!Directory.Exists(outDir))
            {
                try
                {
                    Directory.CreateDirectory(outDir);
                    Log("输出目录原本不存在，已自动创建：" + outDir);
                }
                catch (Exception ex)
                {
                    Log("✗ 无法创建输出目录：" + ex.Message);
                    Log("   请检查路径是否合法、盘符是否存在、有没有写权限。");
                    return;
                }
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = outDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log("✗ 打开输出目录失败：" + ex.Message);
        }
    }

    private async void OnPack(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SaveConfig();

        // 一键直达：没设输出目录就先弹文件夹选择框，选完接着打包。
        if (string.IsNullOrWhiteSpace(_config.OutputFolder))
        {
            var picked = await PickFolder("选择 zip 输出文件夹");
            if (string.IsNullOrWhiteSpace(picked))
            {
                Log("已取消打包。");
                return;
            }
            _outputBox.Text = picked;
            _config.OutputFolder = picked;
            Log("已选择输出目录：" + picked);
            SaveConfig();
        }

        // 源目录留空时自动探测，不给主人添麻烦
        if (string.IsNullOrWhiteSpace(_config.SourceFolder))
        {
            var auto = Packer.LocateInstallRoot();
            if (!string.IsNullOrWhiteSpace(auto))
            {
                _config.SourceFolder = auto;
                _sourceBox.Text = auto;
                Log("源目录留空，已自动探测到：" + auto);
                SaveConfig();
            }
            else
            {
                Log("✗ 没能自动探测到 ClassIsland 目录，请点「自动探测」或「浏览…」选一个。");
                return;
            }
        }

        // 打包前预检：路径非法/盘符不存在要现在就说，别等打到一半才失败
        if (!ValidatePath(_config.OutputFolder, out var pathErr))
        {
            Log("✗ 输出目录无效：" + pathErr);
            Log("   当前值：" + _config.OutputFolder);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_config.SourceFolder) &&
            !ValidatePath(_config.SourceFolder, out var srcErr))
        {
            Log("✗ 源目录无效：" + srcErr);
            Log("   当前值：" + _config.SourceFolder);
            return;
        }

        _cts = new CancellationTokenSource();
        _progress.IsVisible = true;
        _progress.Value = 0;
        _progressText.Text = "准备中…";

        Log("──────── 开始打包 ────────");
        Log($"  源目录：{_config.SourceFolder}");
        Log($"  输出目录：{_config.OutputFolder}");
        Log($"  范围：{(_config.OnlyData ? "仅 data" : _config.IncludeData ? "整个目录（含 data）" : "整个目录（不含 data）")}");

        var progress = new Progress<PackProgress>(p =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (p.Total > 0)
                {
                    _progress.IsIndeterminate = false;
                    _progress.Value = p.Percent;
                    _progressText.Text = $"{p.Phase} {p.Done}/{p.Total}（{p.Percent:0.#}%）  {Trunc(p.Current, 70)}";
                }
                else
                {
                    _progress.IsIndeterminate = true;
                    _progressText.Text = p.Phase;
                }
            });
        });

        try
        {
            var result = await Packer.PackAsync(_config, progress, _cts.Token);

            _progress.IsIndeterminate = false;

            if (result.Success)
            {
                _progress.Value = 100;
                _progressText.Text = "完成 ✓";

                Log($"✓ 打包成功");
                Log($"  文件：{result.OutputPath}");
                Log($"  大小：{Packer.FormatSize(result.SizeBytes)}");
                Log($"  条目数：{result.FileCount}（跳过 {result.SkippedCount}）");
                Log($"  耗时：{result.Elapsed.TotalSeconds:0.##} 秒");

                foreach (var w in result.Warnings.Take(15))
                    Log("  ⚠ " + w);
                if (result.Warnings.Count > 15)
                    Log($"  …其余 {result.Warnings.Count - 15} 条警告省略");
            }
            else
            {
                _progress.Value = 0;
                _progressText.Text = "失败 ✗";
                Log("✗ 打包失败：" + result.Error);
                foreach (var w in result.Warnings.Take(10))
                    Log("  ⚠ " + w);
            }

            RefreshHistory();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "打包异常");
            Log("✗ 打包异常：" + ex.Message);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            Dispatcher.UIThread.Post(() => _progress.IsVisible = false, DispatcherPriority.Background);
        }
    }

    private void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        _logBox.Text = string.IsNullOrEmpty(_logBox.Text) ? line : _logBox.Text + Environment.NewLine + line;
    }

    private static string Trunc(string s, int n)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : "…" + s.Substring(s.Length - n));
}
