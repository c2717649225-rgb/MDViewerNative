using System;
using System.Collections.Generic;

namespace MDViewerNative;

// 最近文件列表的唯一规则：移到最前、去掉原位置、按上限截断。
// 路径按 Windows 规则不区分大小写（与标签复用、书签、离线路径一致，E21），并去掉全部重复项，
// 旧配置里已有的大小写重复在该文件再次打开时自然合并；保留最新一次的写法。
// 列表由宿主持有并负责持久化；这里不读配置、不碰磁盘。
internal static class RecentList
{
    public static void Promote(List<string> list, string path, int max)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(path);
        RemoveAll(list, path);
        list.Insert(0, path);
        if (list.Count > max) list.RemoveRange(max, list.Count - max);
    }

    public static void Rename(List<string> list, string oldPath, string newPath, int max)
    {
        RemoveAll(list, oldPath);
        Promote(list, newPath, max);
    }

    static void RemoveAll(List<string> list, string path) =>
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
}
