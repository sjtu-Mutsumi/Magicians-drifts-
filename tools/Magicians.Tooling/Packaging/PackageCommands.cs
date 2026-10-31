using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Magicians.Native;

namespace Magicians.Tooling;

public static class PackageCommands
{
    public static readonly string[] Commands = { "build-portable", "audit-package", "archive", "client-regressions" };
    private static readonly JsonSerializerOptions JsonOptions = new()
        { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly string[] DataFolders =
        { "Data", "docs/cards-v1.2", "tools/sim_v12/rules", "releases/v1.2", "releases/ai-v1.2-six-styles-20261029" };

    public static int Run(string command, string root, string[] args)
    {
        if (args.Contains("--help"))
        {
            Console.WriteLine("build-portable --godot <同版.NET控制台exe> --template <Windows release模板> --licenses <许可证目录> --output <新目录>");
            Console.WriteLine("audit-package --package <完整解压目录> [--report <JSON>] [--smoke]");
            Console.WriteLine("archive --package <完整解压目录> --output <新ZIP>");
            Console.WriteLine("client-regressions --godot <Godot.NET控制台exe> [--skip-build]");
            return 0;
        }
        switch (command)
        {
            case "build-portable": Build(root, args); break;
            case "audit-package": Audit(root, Required(args, "--package"), Optional(args, "--report"), args.Contains("--smoke")); break;
            case "archive": Archive(root, Required(args, "--package"), Required(args, "--output")); break;
            case "client-regressions": Regressions(root, args); break;
            default: throw new ArgumentException("Unknown packaging command: " + command);
        }
        return 0;
    }

    private static void Regressions(string root, string[] args)
    {
        var godot = ExistingFile(Required(args, "--godot"));
        var folder = Path.Combine(root, "runtime", "client-regressions");
        Directory.CreateDirectory(folder);
        var dotnet = Environment.ProcessPath ?? throw new InvalidOperationException("找不到当前 .NET 主机。");
        if (!Path.GetFileNameWithoutExtension(dotnet).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            dotnet = Optional(args, "--dotnet") ?? "dotnet";
        using (var log = new StreamWriter(Path.Combine(folder, "build-and-core.log"), false, new UTF8Encoding(false)))
        {
            if (!args.Contains("--skip-build")) Execute(dotnet, new[] { "build", Path.Combine(root, "Magicians!.csproj"), "--nologo", "--no-restore" }, root, log);
            var assembly = typeof(PackageCommands).Assembly.Location;
            Execute(dotnet, new[] { assembly, "--root", root, "verify" }, root, log);
            Execute(dotnet, new[] { assembly, "--root", root, "test" }, root, log);
        }
        foreach (var name in new[] { "V12SinglePlayerRunner", "V12LifecycleRunner", "MainMenuFlowRunner", "V12CardUiRunner", "V12DeletionRunner", "V12SelectionRunner", "V12CombatIntegrationRunner" })
        {
            using var log = new StreamWriter(Path.Combine(folder, name + ".log"), false, new UTF8Encoding(false));
            Execute(godot, new[] { "--headless", "--path", root, "res://Scenes/Tests/" + name + ".tscn" }, root, log, disablePython: true);
            Console.WriteLine("CLIENT_TEST_OK " + name);
        }
        Console.WriteLine("V12_CLIENT_REGRESSIONS_OK native-csharp-toolchain=true python_required=false");
    }

    private static void Build(string root, string[] args)
    {
        DataCommands.Run("verify", root, Array.Empty<string>());
        var godot = ExistingFile(Required(args, "--godot"));
        var template = ExistingFile(Required(args, "--template"));
        var licenses = Path.GetFullPath(Required(args, "--licenses"));
        foreach (var name in new[] { "Godot-LICENSE.txt", "Godot-COPYRIGHT.txt", "DotNet-LICENSE.txt", "DotNet-ThirdPartyNotices.txt" })
            if (!File.Exists(Path.Combine(licenses, name)) || new FileInfo(Path.Combine(licenses, name)).Length == 0)
                throw new ArgumentException("依赖许可证缺失或为空：" + name);
        var output = Path.GetFullPath(Required(args, "--output"));
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("请选择尚不存在的新发行目录。");
        if (IsWithin(output, root)) throw new IOException("发行目录应放在项目外，避免把导出文件再次导入项目。");
        Directory.CreateDirectory(output);
        var presets = Path.Combine(root, "export_presets.cfg");
        var original = File.ReadAllBytes(presets);
        var logPath = output + ".export.log";
        try
        {
            var lines = Encoding.UTF8.GetString(original).TrimStart('\ufeff').Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .Where(line => !line.StartsWith("custom_template/release=", StringComparison.Ordinal)
                    && !line.StartsWith("custom_template/debug=", StringComparison.Ordinal)).ToList();
            var index = lines.IndexOf("[preset.0.options]");
            if (index < 0) throw new InvalidDataException("缺少 Windows 导出配置。");
            lines.Insert(index + 1, "custom_template/release=" + JsonSerializer.Serialize(template.Replace('\\', '/'), JsonOptions));
            var debug = Path.Combine(Path.GetDirectoryName(template)!, "windows_debug_x86_64.exe");
            if (File.Exists(debug)) lines.Insert(index + 1, "custom_template/debug=" + JsonSerializer.Serialize(debug.Replace('\\', '/'), JsonOptions));
            var filterIndex = lines.FindIndex(line => line.StartsWith("exclude_filter=", StringComparison.Ordinal));
            if (filterIndex >= 0) lines[filterIndex] = "exclude_filter=\"archives/*,research/*,releases/*,docs/*,decks/*,Data/*,tools/*,Tools/*,migration/*,runtime/*,*.docx\"";
            File.WriteAllText(presets, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
            using var log = new StreamWriter(logPath, false, new UTF8Encoding(false));
            Execute(godot, new[] { "--headless", "--editor", "--path", root, "--import", "--quit" }, root, log);
            Execute(godot, new[] { "--headless", "--path", root, "--export-release", "Windows Portable", Path.Combine(output, "Magicians.exe") }, root, log);
        }
        finally { File.WriteAllBytes(presets, original); }
        DataCommands.Run("verify", root, Array.Empty<string>());
        var exportLog = File.ReadAllText(logPath);
        if (exportLog.Contains("Export .NET Project: Failed", StringComparison.Ordinal)
            || exportLog.Contains("Failed to export project", StringComparison.Ordinal)
            || !File.Exists(Path.Combine(output, "Magicians.exe"))
            || !File.Exists(Path.Combine(output, "Magicians.pck")))
            throw new InvalidOperationException("游戏导出失败，不能分发这个目录；请检查导出日志。");
        foreach (var relative in DataFolders)
            CopyTree(Path.Combine(root, relative), Path.Combine(output, "runtime-data", relative), skipDeveloperSources: true);
        CopyTree(licenses, Path.Combine(output, "licenses"), skipDeveloperSources: false);
        var data = Path.Combine(output, "runtime-data");
        WriteJson(Path.Combine(output, "BUILD-INFO.json"), new
        {
            version = "1.2", edition = "all-csharp-toolchain", runtime = "native-csharp", cards = 183, pool = 677,
            python_required = false, cards_sha256 = Hash(Path.Combine(data, "releases/v1.2/cards.json")),
            models_sha256 = Hash(Path.Combine(data, "releases/ai-v1.2-six-styles-20261029/manifest.json"))
        });
        File.WriteAllText(Path.Combine(output, "README.txt"),
            "Magicians! v1.2 C# 客户端\n完整解压后双击 Magicians.exe，选择开始单人赛事。\n" +
            "游戏规则与六风格 AI 原生运行，无需安装 Python、Godot 或 .NET。\n" +
            "183 张卡牌、677 份库存与五系定稿卡名沿用原版；小字资料保留但局内隐藏。\n" +
            "请保留全部同目录文件；分享时压缩完整目录，不要仅发送快捷方式。\n", new UTF8Encoding(false));
        var hashes = Files(output).ToDictionary(path => Relative(output, path), Hash, StringComparer.Ordinal);
        WriteJson(Path.Combine(output, "integrity.json"), hashes);
        Audit(root, output, output + ".audit.json", smoke: true);
        Console.WriteLine(JsonSerializer.Serialize(new { client = Path.Combine(output, "Magicians.exe"), log = logPath }, JsonOptions));
    }

    private static void Audit(string root, string packagePath, string? reportPath, bool smoke)
    {
        var package = Path.GetFullPath(packagePath);
        foreach (var required in new[] { "Magicians.exe", "Magicians.pck", "BUILD-INFO.json", "integrity.json",
            "data_Magicians!_windows_x86_64/Magicians!.dll", "data_Magicians!_windows_x86_64/Magicians!.deps.json",
            "data_Magicians!_windows_x86_64/Magicians!.runtimeconfig.json", "data_Magicians!_windows_x86_64/GodotSharp.dll",
            "data_Magicians!_windows_x86_64/hostfxr.dll", "data_Magicians!_windows_x86_64/hostpolicy.dll", "data_Magicians!_windows_x86_64/coreclr.dll",
            "licenses/Godot-LICENSE.txt", "licenses/Godot-COPYRIGHT.txt", "licenses/DotNet-LICENSE.txt", "licenses/DotNet-ThirdPartyNotices.txt" })
            if (!File.Exists(Path.Combine(package, required)) || new FileInfo(Path.Combine(package, required)).Length == 0)
                throw new InvalidDataException("发行包缺少必要文件：" + required);
        var manifest = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(package, "integrity.json")))
            ?? throw new InvalidDataException("完整性清单为空。");
        var files = Files(package).ToDictionary(path => Relative(package, path), path => path, StringComparer.Ordinal);
        if (!manifest.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(files.Keys.Where(name => name != "integrity.json")))
            throw new InvalidDataException("发行包存在未列出的文件或缺失的文件。");
        foreach (var row in manifest)
        {
            var path = Path.GetFullPath(Path.Combine(package, row.Key));
            if (!IsWithin(path, package) || !string.Equals(Hash(path), row.Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("文件完整性校验失败：" + row.Key);
        }
        var python = files.Keys.Where(name => new[] { ".py", ".pyc", ".pyo", ".pyd" }.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase)
            || Regex.IsMatch(Path.GetFileName(name), @"^python.*\.(exe|dll)$", RegexOptions.IgnoreCase)).ToArray();
        if (python.Length != 0) throw new InvalidDataException("发行包仍携带 Python 文件：" + string.Join(", ", python));
        var pck = Encoding.Latin1.GetString(File.ReadAllBytes(Path.Combine(package, "Magicians.pck")));
        if (Regex.IsMatch(pck, @"res://[^\x00\r\n]{0,240}\.py(?:[cd])?(?:\x00|\r|\n)", RegexOptions.IgnoreCase))
            throw new InvalidDataException("PCK 中仍携带 Python 源文件。");
        using var info = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(package, "BUILD-INFO.json")));
        if (info.RootElement.GetProperty("runtime").GetString() != "native-csharp" || info.RootElement.GetProperty("python_required").GetBoolean())
            throw new InvalidDataException("发行包不是无 Python 的 C# 运行版。");
        var dataRoot = Path.Combine(package, "runtime-data");
        foreach (var relative in DataFolders)
            foreach (var source in Files(Path.Combine(root, relative)).Where(path => !SkipSource(path)))
            {
                var name = Relative(root, source);
                var shipped = Path.Combine(dataRoot, name);
                if (!File.Exists(shipped) || Hash(source) != Hash(shipped))
                    throw new InvalidDataException("发行包规则、卡名或模型与源码不一致：" + name);
            }
        if (info.RootElement.GetProperty("cards_sha256").GetString() != Hash(Path.Combine(dataRoot, "releases/v1.2/cards.json"))
            || info.RootElement.GetProperty("models_sha256").GetString() != Hash(Path.Combine(dataRoot, "releases/ai-v1.2-six-styles-20261029/manifest.json")))
            throw new InvalidDataException("发行数据声明的校验值不一致。");
        Catalog.Initialize(dataRoot);
        Catalog.validate_sources();
        foreach (var style in SixStyles.IDS) SixStyles.load_policy(style);
        Catalog.Initialize(root);
        if (smoke)
        {
            var logPath = package + ".smoke.log";
            using (var writer = new StreamWriter(logPath, false, new UTF8Encoding(false)))
                Execute(Path.Combine(package, "Magicians.exe"), new[] { "--headless", "--", "--release-smoke" }, package, writer, disablePython: true);
            if (!File.ReadAllText(logPath).Contains("V12_PACKAGED_OK", StringComparison.Ordinal))
                throw new InvalidDataException("独立程序完整赛事运行检查未通过。");
        }
        var report = new
        {
            status = "passed", runtime = "native-csharp", python_required = false, files = files.Count,
            integrity_entries = manifest.Count, cards = Catalog.CARDS.Count, stock = Catalog.CARDS.Values.Sum(Catalog.copies),
            models = SixStyles.IDS.Length, rule_and_display_data_equal_to_source = true,
            python_files = python, python_disabled_smoke = smoke
        };
        if (reportPath is not null) WriteJson(Path.GetFullPath(reportPath), report);
        Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
    }

    private static void Archive(string root, string packagePath, string outputPath)
    {
        var package = Path.GetFullPath(packagePath);
        var output = Path.GetFullPath(outputPath);
        if (File.Exists(output) || Directory.Exists(output)) throw new IOException("压缩文件已存在，请选择新的名称。");
        if (IsWithin(output, package)) throw new IOException("压缩包必须放在发行目录外。");
        Audit(root, package, null, smoke: false);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        ZipFile.CreateFromDirectory(package, output, CompressionLevel.Optimal, includeBaseDirectory: true);
        using (var zip = ZipFile.OpenRead(output))
        {
            var prefix = new DirectoryInfo(package).Name + "/";
            var entries = zip.Entries.Where(entry => !entry.FullName.EndsWith('/')).ToArray();
            if (entries.Length != Files(package).Count()) throw new InvalidDataException("ZIP 文件数量不一致。");
            foreach (var entry in entries)
            {
                if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("ZIP 目录结构不正确。");
                using var stream = entry.Open();
                if (Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() != Hash(Path.Combine(package, entry.FullName[prefix.Length..])))
                    throw new InvalidDataException("ZIP 内容校验失败：" + entry.FullName);
            }
        }
        File.WriteAllText(output + ".sha256", Hash(output) + "  " + Path.GetFileName(output) + "\n", new UTF8Encoding(false));
        Console.WriteLine(JsonSerializer.Serialize(new { archive = output, bytes = new FileInfo(output).Length, sha256 = Hash(output) }, JsonOptions));
    }

    private static void Execute(string executable, IEnumerable<string> arguments, string workingDirectory, StreamWriter log, bool disablePython = false)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (disablePython)
        {
            start.Environment["PATH"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");
            start.Environment["PYTHONHOME"] = Path.Combine(workingDirectory, "nonexistent-python-home");
            start.Environment["MAGICIANS_PYTHON"] = Path.Combine(workingDirectory, "nonexistent-python.exe");
            start.Environment.Remove("PYTHONPATH");
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动：" + Path.GetFileName(executable));
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(300_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException("进程运行超时：" + Path.GetFileName(executable));
        }
        log.Write(stdout.GetAwaiter().GetResult());
        log.Write(stderr.GetAwaiter().GetResult());
        log.Flush();
        if (process.ExitCode != 0) throw new InvalidOperationException(Path.GetFileName(executable) + " 退出码 " + process.ExitCode);
    }

    private static void CopyTree(string source, string destination, bool skipDeveloperSources)
    {
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
        Directory.CreateDirectory(destination);
        foreach (var path in Files(source))
        {
            if (skipDeveloperSources && SkipSource(path)) continue;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("不复制链接文件：" + path);
            var target = Path.Combine(destination, Relative(source, path));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target, overwrite: false);
        }
    }

    private static bool SkipSource(string path) => new[] { ".py", ".pyc", ".pyo", ".pyd", ".zip", ".lnk" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
        || path.Split(Path.DirectorySeparatorChar).Contains("__pycache__", StringComparer.Ordinal);
    private static IEnumerable<string> Files(string directory) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal);
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    private static bool IsWithin(string path, string directory) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string ExistingFile(string path) => File.Exists(Path.GetFullPath(path)) ? Path.GetFullPath(path) : throw new FileNotFoundException("找不到文件。", path);
    private static string Required(string[] args, string key) => Optional(args, key) ?? throw new ArgumentException("需要参数 " + key);
    private static string? Optional(string[] args, string key)
    {
        var index = Array.IndexOf(args, key);
        if (index < 0) return null;
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException(key + " 后需要值。");
        return args[index + 1];
    }
    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions) + "\n", new UTF8Encoding(false));
    }
}
