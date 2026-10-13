using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

// E2 依赖方向检查（ADR-0001 第 3 条）：从 engine/*.cs 源码建模块图，检查
//   1) 内容层与工作流层不引用平台、绘制、宿主模块；经未分层模块中转的间接引用同样算违规（E19a）；
//   2) 模块之间没有循环。
// 现有违规列入例外清单，并注明由哪个切片消除；例外不再需要时本检查同样失败，所以清单只减不增。
// 模块 = 文件名首段；共享同一顶层（分部）类型的文件合并为一个模块。
internal static class DependencyRulesTests
{
    static readonly HashSet<string> Content = new()
    {
        "MdParser", "DocumentModel", "BlockText", "MathModel", "HtmlReading", "WikiLinks", "DocumentLinks", "DocumentAnchors", "MathSyntax", "MathUnicode",
        "MermaidLite", "EmojiMap", "HtmlEntity", "TextSearch", "TextDiff", "LineBreaks", "MarkdownSourceColors", "CodeHighlight",
        "ImageSource", "TaskListSource", "CodeBlockTargets",
    };
    // EditorSession 协调 Win32 编辑控件，属宿主侧，不在此列。
    static readonly HashSet<string> Workflow = new()
    {
        "DocumentSession", "DocumentSessionView", "DocumentTabs", "TextEditHistory", "UndoBudget", "NavigationHistory", "DocumentBatch", "DocumentWork",
        "DocumentFile", "DocumentFormat", "AtomicFile", "EditorPosition", "DiskState", "ReadingLocation",
        "RecentList",
    };
    static readonly HashSet<string> Upper = new()
    {
        "Win32", "Dwrite", "RenderResources", "DocView", "Program", "NativeForm", "NativeMenus", "EditorControl", "ToolbarView",
        "FindBar", "MathRender", "MathGlyphs", "ImageWindow", "DocumentPrinter",
    };
    // v3 界面的纯层（颜色、尺寸、版式、页脚内容等；ADR-0003 第 3 条）：同样不得引用平台、绘制、宿主模块。
    static readonly HashSet<string> DeskPure = new()
    {
        "ThemePalette", "DeskMetrics", "Appearance", "PaperTexture", "ReadingStyle", "FooterModel", "CordGeometry", "DeskAnimations", "DeskToolbarLayout", "DeskTip", "DeskFrame", "TitlebarQuiet", "InkCircle", "SelectionBubble", "Juan", "EditBar", "LetterPaper", "InkDrop", "Home", "CommandSearch", "AppearancePanel", "MoreMenu", "ChoiceDialog", "EditorGutter",
    };
    // E15 已消除 DocumentSession → DocView;清单为空,新增违规直接失败。
    static readonly (string From, string To, string Owner)[] LayerExceptions = { };
    static readonly (string From, string To, string Owner)[] CycleExceptions =
    {
        ("MathUnicode", "MathSyntax", "accepted: one math-parsing subdomain"),
        ("UndoBudget", "TextEditHistory", "accepted: undo budget registry"),
    };

    internal static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok, string detail) => check("Dependencies/" + name, ok, detail);
        C("detector finds a synthetic cycle and layer violation", SelfCheck(), "synthetic sources; comments and strings ignored");

        string engine = FindEngine();
        var sources = engine == null ? new Dictionary<string, string>()
            : Directory.GetFiles(engine, "*.cs").ToDictionary(Path.GetFileName, File.ReadAllText);
        var modules = Modules(sources);
        var names = new HashSet<string>(modules.Values);
        var unknown = Content.Concat(Workflow).Concat(DeskPure).Concat(Upper)
            .Concat(LayerExceptions.SelectMany(e => new[] { e.From, e.To }))
            .Concat(CycleExceptions.SelectMany(e => new[] { e.From, e.To }))
            .Where(n => !names.Contains(n)).Distinct().ToList();
        C("engine sources found and rule names exist", sources.Count >= 50 && unknown.Count == 0,
            "files=" + sources.Count + (unknown.Count > 0 ? " unknown=" + string.Join(",", unknown) : ""));
        if (sources.Count < 50) return;

        var edges = Edges(sources, modules);
        var layer = LayerViolations(edges);
        var layerAllowed = LayerExceptions.Select(e => (e.From, e.To)).ToHashSet();
        var newLayer = layer.Keys.Where(v => !layerAllowed.Contains(v)).ToList();
        C("content and workflow layers do not reference upper layers", newLayer.Count == 0, string.Join("; ", newLayer.Select(v => layer[v])));

        var cycleAllowed = CycleExceptions.Select(e => (e.From, e.To)).ToHashSet();
        var cycles = Cycles(edges.Keys.Where(e => !cycleAllowed.Contains(e)));
        C("no module cycles beyond listed exceptions", cycles.Count == 0,
            string.Join(" | ", cycles.Select(c => string.Join(",", c))));

        var inCycle = Cycles(edges.Keys).SelectMany(c => c.Select(m => (m, c))).ToDictionary(x => x.m, x => x.c);
        var stale = LayerExceptions.Where(e => !layer.ContainsKey((e.From, e.To))).Select(e => "layer " + e.From + "->" + e.To)
            .Concat(CycleExceptions.Where(e => !edges.ContainsKey((e.From, e.To)) || !inCycle.TryGetValue(e.From, out var c) || !c.Contains(e.To))
                .Select(e => "cycle " + e.From + "->" + e.To))
            .ToList();
        C("every listed exception is still needed", stale.Count == 0, "remove from the list: " + string.Join(", ", stale));
    }

    static bool SelfCheck()
    {
        var sources = new Dictionary<string, string>
        {
            ["TextSearch.cs"] = "namespace X;\ninternal static class TextSearch { static DocView v; static Loop l; }\n",
            ["DocView.cs"] = "namespace X;\ninternal sealed class DocView { }\n",
            ["Loop.cs"] = "namespace X;\ninternal sealed class Loop { TextSearch s; }\n",
            ["Quiet.cs"] = "namespace X;\ninternal sealed class Quiet { string a = \"DocView\"; char b = '{'; // Loop\n /* TextSearch */ }\n",
            ["Part.a.cs"] = "namespace X;\ninternal partial class Part { }\n",
            ["Other.cs"] = "namespace X;\ninternal partial class Part { Quiet q; }\n",
            ["MdParser.cs"] = "namespace X;\ninternal static class MdParser { static Relay r; }\n",
            ["Relay.cs"] = "namespace X;\ninternal sealed class Relay { DocView v; }\n",
        };
        var modules = Modules(sources);
        var edges = Edges(sources, modules);
        var layer = LayerViolations(edges);
        return layer.ContainsKey(("TextSearch", "DocView"))
            && layer.ContainsKey(("MdParser", "DocView")) && !layer.Keys.Any(v => v.From == "Relay")
            && Cycles(edges.Keys).Any(c => c.Contains("TextSearch") && c.Contains("Loop"))
            && !edges.Keys.Any(e => e.From == "Quiet")
            && modules["Part.a.cs"] == modules["Other.cs"];
    }

    static string FindEngine()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "engine");
                if (File.Exists(Path.Combine(candidate, "MDViewerNative.csproj"))) return candidate;
            }
        }
        return null;
    }

    static readonly Regex Raw = new("\"\"\"[\\s\\S]*?\"\"\"");
    static readonly Regex CharLiteral = new(@"'(?:\\.|[^'\\])'");
    static readonly Regex Block = new(@"/\*[\s\S]*?\*/");
    static readonly Regex Verbatim = new("(?:\\$@|@\\$|@)\"(?:[^\"]|\"\")*\"");
    static readonly Regex Regular = new("\\$?\"(?:\\\\.|[^\"\\\\\\n])*\"");
    static readonly Regex Line = new(@"//[^\n]*");
    static readonly Regex Declaration = new(@"[{}]|\b(?:class|struct|enum|interface|record)\s+([A-Z]\w*)");
    static readonly Regex Identifier = new(@"\b[A-Z]\w*\b");

    static string Strip(string s)
    {
        s = Raw.Replace(s, "\"\"");
        s = CharLiteral.Replace(s, "''");
        s = Block.Replace(s, "");
        s = Verbatim.Replace(s, "\"\"");
        s = Regular.Replace(s, "\"\"");
        return Line.Replace(s, "");
    }

    static Dictionary<string, List<string>> TopLevelTypes(Dictionary<string, string> code)
    {
        var owners = new Dictionary<string, List<string>>();
        foreach (var (file, text) in code)
        {
            int depth = 0;
            foreach (Match m in Declaration.Matches(text))
            {
                if (m.Value == "{") depth++;
                else if (m.Value == "}") depth--;
                else if (depth == 0)
                {
                    if (!owners.TryGetValue(m.Groups[1].Value, out var list)) owners[m.Groups[1].Value] = list = new();
                    if (!list.Contains(file)) list.Add(file);
                }
            }
        }
        return owners;
    }

    static Dictionary<string, string> Modules(Dictionary<string, string> sources)
    {
        var code = sources.ToDictionary(p => p.Key, p => Strip(p.Value));
        var parent = code.Keys.ToDictionary(f => f, f => f);
        string Find(string f) { while (parent[f] != f) f = parent[f] = parent[parent[f]]; return f; }
        foreach (var files in TopLevelTypes(code).Values)
            foreach (string f in files.Skip(1)) parent[Find(f)] = Find(files[0]);
        string Short(string file) => Path.GetFileNameWithoutExtension(file).Split('.')[0];
        var groups = code.Keys.GroupBy(Find).ToDictionary(g => g.Key, g => g.Select(Short).OrderBy(n => n.Length).ThenBy(n => n, StringComparer.Ordinal).First());
        return code.Keys.ToDictionary(f => f, f => groups[Find(f)]);
    }

    static Dictionary<(string From, string To), SortedSet<string>> Edges(Dictionary<string, string> sources, Dictionary<string, string> modules)
    {
        var code = sources.ToDictionary(p => p.Key, p => Strip(p.Value));
        var owners = TopLevelTypes(code);
        var edges = new Dictionary<(string, string), SortedSet<string>>();
        foreach (var (file, text) in code)
        {
            foreach (string type in Identifier.Matches(text).Select(m => m.Value).Distinct())
            {
                if (!owners.TryGetValue(type, out var files)) continue;
                foreach (string target in files)
                {
                    var key = (modules[file], modules[target]);
                    if (key.Item1 == key.Item2) continue;
                    if (!edges.TryGetValue(key, out var set)) edges[key] = set = new(StringComparer.Ordinal);
                    set.Add(type);
                }
            }
        }
        return edges;
    }

    // 从每个内容层、工作流层模块出发广度优先，遇到上层模块即记一条违规（不再穿过上层继续走）。
    // 值是最短路径，形如 A->B[类型]->C[类型]，便于定位是哪条边把上层带进来的。
    static Dictionary<(string From, string To), string> LayerViolations(Dictionary<(string From, string To), SortedSet<string>> edges)
    {
        var next = edges.Keys.GroupBy(e => e.From)
            .ToDictionary(g => g.Key, g => g.Select(e => e.To).OrderBy(t => t, StringComparer.Ordinal).ToList());
        var result = new Dictionary<(string From, string To), string>();
        foreach (string from in next.Keys.Where(m => Content.Contains(m) || Workflow.Contains(m) || DeskPure.Contains(m)))
        {
            var via = new Dictionary<string, string> { [from] = null };
            var queue = new Queue<string>();
            queue.Enqueue(from);
            while (queue.Count > 0)
            {
                string at = queue.Dequeue();
                if (!next.TryGetValue(at, out var targets)) continue;
                foreach (string to in targets)
                {
                    if (via.ContainsKey(to)) continue;
                    via[to] = at;
                    if (!Upper.Contains(to)) { queue.Enqueue(to); continue; }
                    var hops = new List<string>();
                    for (string m = to; via[m] != null; m = via[m])
                        hops.Insert(0, "->" + m + "[" + string.Join(",", edges[(via[m], m)]) + "]");
                    result[(from, to)] = from + string.Concat(hops);
                }
            }
        }
        return result;
    }

    // Tarjan 强连通分量；只返回含两个及以上模块的分量。
    static List<List<string>> Cycles(IEnumerable<(string From, string To)> edgeList)
    {
        var graph = new Dictionary<string, List<string>>();
        foreach (var (from, to) in edgeList)
        {
            if (!graph.TryGetValue(from, out var next)) graph[from] = next = new();
            next.Add(to);
            if (!graph.ContainsKey(to)) graph[to] = new();
        }
        var index = new Dictionary<string, int>();
        var low = new Dictionary<string, int>();
        var stack = new Stack<string>();
        var onStack = new HashSet<string>();
        var result = new List<List<string>>();
        void Visit(string v)
        {
            index[v] = low[v] = index.Count;
            stack.Push(v); onStack.Add(v);
            foreach (string w in graph[v])
            {
                if (!index.ContainsKey(w)) { Visit(w); low[v] = Math.Min(low[v], low[w]); }
                else if (onStack.Contains(w)) low[v] = Math.Min(low[v], index[w]);
            }
            if (low[v] != index[v]) return;
            var component = new List<string>();
            string x;
            do { x = stack.Pop(); onStack.Remove(x); component.Add(x); } while (x != v);
            if (component.Count > 1) { component.Sort(StringComparer.Ordinal); result.Add(component); }
        }
        foreach (string v in graph.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList())
            if (!index.ContainsKey(v)) Visit(v);
        return result;
    }
}
