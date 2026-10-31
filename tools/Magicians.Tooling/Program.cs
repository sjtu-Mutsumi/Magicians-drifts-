using System.Reflection;
using System.Text;
using Magicians.Native;

namespace Magicians.Tooling;

public static class Program
{
    public static int Main(string[] arguments)
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(false);
        var args = arguments.ToList();
        try
        {
            var rootIndex = args.IndexOf("--root");
            var root = rootIndex < 0 ? FindRoot() : Path.GetFullPath(RequireValue(args, rootIndex));
            if (rootIndex >= 0) args.RemoveRange(rootIndex, 2);
            var modules = new[] { "DataCommands", "TrainingCommands", "TestCommands", "PackageCommands" }
                .Select(name => Assembly.GetExecutingAssembly().GetType("Magicians.Tooling." + name))
                .Where(type => type is not null).Cast<Type>().ToArray();
            if (args.Count == 0 || args[0] is "help" or "--help" or "-h")
            {
                Console.WriteLine("Magicians! C# 开发工具（无需 Python）");
                Console.WriteLine("dotnet run --project tools/Magicians.Tooling -- [--root <项目目录>] <命令> [参数]");
                foreach (var type in modules)
                    Console.WriteLine("  " + string.Join(", ", Commands(type)));
                return 0;
            }
            if (!File.Exists(Path.Combine(root, "project.godot")))
                throw new ArgumentException("项目目录中没有 project.godot：" + root);
            Catalog.Initialize(root);
            var command = args[0];
            foreach (var type in modules)
                if (Commands(type).Contains(command, StringComparer.Ordinal))
                {
                    if (type.Name == "TrainingCommands" &&
                        (command is "sim" or "quick-test" or "train" || command.StartsWith("train-", StringComparison.Ordinal)))
                        DataCommands.Run("verify", root, Array.Empty<string>());
                    return (int)type.GetMethod("Run", BindingFlags.Static | BindingFlags.Public)!
                        .Invoke(null, new object[] { command, root, args.Skip(1).ToArray() })!;
                }
            throw new ArgumentException("未知命令：" + command + "；运行 help 查看可用命令。");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            Console.Error.WriteLine("失败：" + ex.InnerException.Message);
            if (Environment.GetEnvironmentVariable("MAGICIANS_TOOL_TRACE") == "1") Console.Error.WriteLine(ex.InnerException);
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("失败：" + ex.Message);
            if (Environment.GetEnvironmentVariable("MAGICIANS_TOOL_TRACE") == "1") Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static string[] Commands(Type type) =>
        (string[]?)type.GetField("Commands", BindingFlags.Static | BindingFlags.Public)?.GetValue(null)
        ?? (string[]?)type.GetProperty("Commands", BindingFlags.Static | BindingFlags.Public)?.GetValue(null)
        ?? Array.Empty<string>();

    private static string RequireValue(List<string> args, int index) =>
        index + 1 < args.Count ? args[index + 1] : throw new ArgumentException("--root 后需要项目目录。");

    private static string FindRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "project.godot"))) return dir.FullName;
        throw new ArgumentException("找不到项目目录，请使用 --root 指定。");
    }
}
