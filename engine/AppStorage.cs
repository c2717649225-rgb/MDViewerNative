using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace MDViewerNative;

internal static class AppStorage
{
    public static string DirectoryPath
    {
        get
        {
            string configured = Environment.GetEnvironmentVariable("MDV_CONFIG_DIR");
            return Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MDViewerNative")
                : configured);
        }
    }

    // 只读出 JSON 顶层的整数 Schema，不反序列化其余字段：未来版本改了字段类型也能认出版本号（E23）。
    // 没有 Schema 或不是整数时返回 null；JSON 本身无效时抛 JsonException。
    internal static int? ReadSchema(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Schema", out var schema)
            && schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out int value) ? value : null;
    }

    // 读不懂的状态文件在写回前改名保留为 <文件名>.corrupt-<guid>（与草稿一致），不让保存覆盖它（E23）。
    // 改名失败时返回 false，调用方本次不写，原文件保持不动。
    internal static bool SetAside(string path)
    {
        try
        {
            if (File.Exists(path)) File.Move(path, path + ".corrupt-" + Guid.NewGuid().ToString("N"));
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("keep unreadable " + Path.GetFileName(path), ex);
            return false;
        }
    }
}

internal static class AppLog
{
    static readonly object Gate = new();
    public static void Error(string operation, Exception exception) => Write(operation + ": " + exception);
    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                string directory = AppStorage.DirectoryPath;
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "app.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                    File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { } // Diagnostics must never turn a recoverable error into a crash.
    }
}
