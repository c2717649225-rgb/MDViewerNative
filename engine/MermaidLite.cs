// Bounded flowcharts, cycles included. Unsupported syntax falls back to the complete source.
using System;
using System.Collections.Generic;
using System.Text;

namespace MDViewerNative;

internal sealed class MermaidGraph
{
    public sealed class MNode { public string Id, Text, Shown; public int Shape; public int Depth; public float X, Y, W, H; }
    public sealed class MEdge { public int From, To; public string Label; public bool Dashed, Thick; public bool Arrow = true; public List<(float x, float y)> Path; }

    public bool LR, Reverse;
    public List<MNode> Nodes = new();
    public List<MEdge> Edges = new();
    public Dictionary<string, int> Index = new(StringComparer.Ordinal);

    public int GetOrAdd(string id)
    {
        if (!Index.TryGetValue(id, out int i))
        {
            if (Nodes.Count >= MermaidLite.MaxNodes) throw new FormatException("Diagram node budget exceeded.");
            i = Nodes.Count;
            Index[id] = i;
            Nodes.Add(new MNode { Id = id, Text = id });
        }
        return i;
    }

    public void SetText(string id, string text, int shape)
    {
        int i = GetOrAdd(id);
        Nodes[i].Text = text;
        Nodes[i].Shape = shape;
    }
}

internal static class MermaidLite
{
    internal const int MaxLength = 65536, MaxNodes = 256, MaxEdges = 512, MaxLabel = 512;

    public static bool TryParse(string text, out MermaidGraph graph)
    {
        graph = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLength) return false;
        try
        {
            var statements = Statements(text);
            if (statements.Count < 2) return false;
            string[] header = statements[0].Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (header.Length != 2 || !(header[0].Equals("graph", StringComparison.OrdinalIgnoreCase) || header[0].Equals("flowchart", StringComparison.OrdinalIgnoreCase))) return false;
            string direction = header[1].ToUpperInvariant();
            if (direction is not ("TD" or "TB" or "BT" or "LR" or "RL")) return false;
            var candidate = new MermaidGraph { LR = direction is "LR" or "RL", Reverse = direction is "RL" or "BT" };
            for (int i = 1; i < statements.Count; i++) new Reader(statements[i], candidate).Read();
            if (candidate.Nodes.Count == 0 || !Layout(candidate)) return false;
            graph = candidate;
            return true;
        }
        catch (FormatException) { return false; }
    }

    private static List<string> Statements(string text)
    {
        var result = new List<string>();
        var statement = new StringBuilder();
        char closing = '\0';
        bool quoted = false, escaped = false;
        void Flush()
        {
            string value = statement.ToString().Trim();
            statement.Clear();
            if (value.Length == 0) return;
            if (result.Count >= 1024) throw new FormatException("Too many diagram statements.");
            result.Add(value);
        }
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c is '\r' or '\n') throw new FormatException("Multiline labels are unsupported.");
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') quoted = false;
            }
            else if (c == '"') quoted = true;
            else if (closing != '\0')
            {
                if (c == closing) closing = '\0';
                else if (c is '[' or ']' or '(' or ')' or '{' or '}' or '\r' or '\n') throw new FormatException("Unsupported label nesting.");
            }
            else if (c == '%' && i + 1 < text.Length && text[i + 1] == '%')
            {
                Flush();
                while (i < text.Length && text[i] is not ('\r' or '\n')) i++;
                continue;
            }
            else if (c is '\r' or '\n' or ';') { Flush(); continue; }
            else if (c is '[' or '(' or '{') closing = c == '[' ? ']' : c == '(' ? ')' : '}';
            else if (c is ']' or ')' or '}') throw new FormatException("Mismatched shape delimiter.");
            statement.Append(c);
        }
        if (quoted || closing != '\0') throw new FormatException("Unclosed diagram label.");
        Flush();
        return result;
    }

    private sealed class Reader
    {
        private readonly string _text;
        private readonly MermaidGraph _graph;
        private int _position;
        internal Reader(string text, MermaidGraph graph) { _text = text; _graph = graph; }
        private void Space() { while (_position < _text.Length && char.IsWhiteSpace(_text[_position])) _position++; }
        internal void Read()
        {
            int from = Node();
            if (_graph.Nodes[from].Id is "subgraph" or "end" or "classDef" or "class" or "style" or "click" or "linkStyle" or "direction")
                throw new FormatException("Unsupported diagram directive.");
            while (true)
            {
                Space();
                if (_position == _text.Length) return;
                string edge = null;
                foreach (string token in new[] { "-.->", "-->", "---", "==>", "===" })
                    if (_text.AsSpan(_position).StartsWith(token, StringComparison.Ordinal)) { edge = token; break; }
                if (edge == null || _graph.Edges.Count >= MaxEdges) throw new FormatException("Unsupported edge or edge budget exceeded.");
                _position += edge.Length;
                Space();
                string label = "";
                if (_position < _text.Length && _text[_position] == '|') { _position++; label = Label('|'); }
                int to = Node();
                _graph.Edges.Add(new MermaidGraph.MEdge { From = from, To = to, Label = label, Dashed = edge == "-.->", Thick = edge[0] == '=', Arrow = edge.EndsWith('>') });
                from = to;
            }
        }
        private int Node()
        {
            Space();
            int start = _position;
            while (_position < _text.Length && (char.IsLetterOrDigit(_text[_position]) || _text[_position] == '_')) _position++;
            if (_position == start || _position - start > 128) throw new FormatException("Invalid node identifier.");
            string id = _text[start.._position];
            int node = _graph.GetOrAdd(id);
            Space();
            if (_position < _text.Length && _text[_position] is '[' or '(' or '{')
            {
                char shape = _text[_position++];
                string label = Label(shape == '[' ? ']' : shape == '(' ? ')' : '}');
                _graph.SetText(id, label, shape == '(' ? 1 : shape == '{' ? 2 : 0);
            }
            return node;
        }
        private string Label(char closing)
        {
            int start = _position;
            bool quoted = false, escaped = false;
            while (_position < _text.Length)
            {
                char c = _text[_position++];
                if (quoted)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') quoted = false;
                }
                else if (c == '"') quoted = true;
                else if (c == closing)
                {
                    string raw = _text[start..(_position - 1)].Trim();
                    if (raw.Length > MaxLabel * 2 + 2) throw new FormatException("Diagram label budget exceeded.");
                    if (raw.StartsWith('"'))
                    {
                        if (raw.Length < 2 || raw[^1] != '"') throw new FormatException("Malformed quoted label.");
                        var decoded = new StringBuilder();
                        for (int i = 1; i < raw.Length - 1; i++)
                        {
                            char value = raw[i];
                            if (value == '\\')
                            {
                                if (++i >= raw.Length - 1 || raw[i] is not ('"' or '\\')) throw new FormatException("Unsupported label escape.");
                                value = raw[i];
                            }
                            else if (value == '"') throw new FormatException("Unexpected label quote.");
                            decoded.Append(value);
                        }
                        raw = decoded.ToString();
                    }
                    else if (raw.Contains('"')) throw new FormatException("Quotes must enclose the entire label.");
                    if (raw.Length > MaxLabel) throw new FormatException("Diagram label budget exceeded.");
                    return raw;
                }
            }
            throw new FormatException("Unclosed diagram label.");
        }
    }

    // Layered ("Sugiyama") drawing, kept small and bounded:
    //  1. longest-path layers, O(nodes + edges); back edges of cycles are layered reversed;
    //  2. an edge spanning several layers gets one virtual slot per inner layer, so it is
    //     routed through a gap in each row instead of cutting straight through boxes;
    //  3. alternating barycenter sweeps reorder each layer, keeping the order with the
    //     fewest crossings seen;
    //  4. cross-axis positions: each slot aims at the mean of its neighbours in the
    //     adjacent layer, solved per layer as an ordered least-squares fit with minimum
    //     spacing (pool adjacent violators). The last sweep runs upwards, so a parent ends
    //     centred over its children.
    internal const int MaxVirtual = 2048;
    private const float Margin = 30f, Band = 32f, RowGap = 60f, LineH = 18f, WrapAt = 140f, LaneGap = 16f;

    private static bool Layout(MermaidGraph graph)
    {
        int n = graph.Nodes.Count;
        // Cycles ("失败 → 重试 → 回到检查"): a depth-first search in declaration order marks each
        // edge into a node still on its stack as a back edge. A back edge takes part in layering
        // reversed, so it gets virtual lanes like any long edge, and is drawn back the original
        // way; the declared order stays the reading order. Acyclic charts have no back edges and
        // lay out exactly as before. Self loops take no part in layering and sit beside the box.
        int m = graph.Edges.Count;
        var back = new bool[m]; var self = new bool[m];
        var adjacent = new List<int>[n];
        for (int i = 0; i < n; i++) adjacent[i] = new();
        for (int e = 0; e < m; e++)
            if (graph.Edges[e].From == graph.Edges[e].To) self[e] = true;
            else adjacent[graph.Edges[e].From].Add(e);
        var state = new byte[n];   // 0 unseen, 1 on the stack, 2 finished
        var stack = new Stack<(int node, int next)>();
        for (int root = 0; root < n; root++)
        {
            if (state[root] != 0) continue;
            state[root] = 1; stack.Push((root, 0));
            while (stack.TryPop(out var frame))
            {
                if (frame.next == adjacent[frame.node].Count) { state[frame.node] = 2; continue; }
                stack.Push((frame.node, frame.next + 1));
                int e = adjacent[frame.node][frame.next], to = graph.Edges[e].To;
                if (state[to] == 1) back[e] = true;
                else if (state[to] == 0) { state[to] = 1; stack.Push((to, 0)); }
            }
        }
        int U(int e) => back[e] ? graph.Edges[e].To : graph.Edges[e].From;   // upper end in the layering
        int V(int e) => back[e] ? graph.Edges[e].From : graph.Edges[e].To;   // lower end
        var depth = new int[n];
        var incoming = new int[n];
        var outgoing = new List<int>[n];
        for (int i = 0; i < n; i++) outgoing[i] = new();
        for (int e = 0; e < m; e++) { if (self[e]) continue; outgoing[U(e)].Add(V(e)); incoming[V(e)]++; }
        var ready = new Queue<int>();
        for (int i = 0; i < n; i++) if (incoming[i] == 0) ready.Enqueue(i);
        int visited = 0;
        while (ready.TryDequeue(out int from))
        {
            visited++;
            foreach (int to in outgoing[from])
            {
                depth[to] = Math.Max(depth[to], depth[from] + 1);
                if (--incoming[to] == 0) ready.Enqueue(to);
            }
        }
        if (visited != n) return false;
        int maxDepth = 0;
        for (int i = 0; i < n; i++)
        {
            var node = graph.Nodes[i];
            node.Depth = depth[i];
            maxDepth = Math.Max(maxDepth, depth[i]);
            (node.Shown, float textW, int lines) = Wrap(node.Text);
            node.W = textW + 36f;
            node.H = Band + (lines - 1) * LineH;
        }

        // Slots: 0..n-1 are the real nodes, later ones are virtual lanes of long edges.
        var layerOf = new List<int>(depth);
        var ups = new List<List<int>>(); var downs = new List<List<int>>();
        for (int i = 0; i < n; i++) { ups.Add(new()); downs.Add(new()); }
        var chains = new List<int>[graph.Edges.Count];
        int virtualBudget = 0;
        for (int e = 0; e < m; e++) if (!self[e]) virtualBudget += depth[V(e)] - depth[U(e)] - 1;
        bool lanes = virtualBudget <= MaxVirtual;
        for (int e = 0; e < graph.Edges.Count; e++)
        {
            chains[e] = new List<int>();
            if (self[e]) continue;
            int previous = U(e);
            if (lanes)
                for (int d = depth[U(e)] + 1; d < depth[V(e)]; d++)
                {
                    int slot = layerOf.Count;
                    layerOf.Add(d); ups.Add(new()); downs.Add(new());
                    downs[previous].Add(slot); ups[slot].Add(previous);
                    chains[e].Add(slot);
                    previous = slot;
                }
            downs[previous].Add(V(e)); ups[V(e)].Add(previous);
        }
        int slots = layerOf.Count;
        var layers = new List<int>[maxDepth + 1];
        for (int d = 0; d <= maxDepth; d++) layers[d] = new();
        for (int s = 0; s < slots; s++) layers[layerOf[s]].Add(s);

        var order = new float[slots];
        void Index() { foreach (var layer in layers) for (int k = 0; k < layer.Count; k++) order[layer[k]] = k; }
        Index();
        int Crossings()
        {
            int total = 0;
            for (int d = 0; d < maxDepth; d++)
            {
                var links = new List<(float a, float b)>();
                foreach (int s in layers[d]) foreach (int t in downs[s]) links.Add((order[s], order[t]));
                if (links.Count > 600) return -1;   // too large to count cheaply; keep the last sweep
                for (int i = 0; i < links.Count; i++)
                    for (int j = i + 1; j < links.Count; j++)
                        if ((links[i].a - links[j].a) * (links[i].b - links[j].b) < 0f) total++;
            }
            return total;
        }
        int best = Crossings();
        var bestLayers = Array.ConvertAll(layers, l => new List<int>(l));
        for (int sweep = 0; sweep < 8 && best != 0; sweep++)
        {
            bool down = sweep % 2 == 0;
            for (int step = 1; step <= maxDepth; step++)
            {
                int d = down ? step : maxDepth - step;
                var layer = layers[d];
                var key = new Dictionary<int, float>(layer.Count);
                foreach (int s in layer)
                {
                    var near = down ? ups[s] : downs[s];
                    float sum = 0f;
                    foreach (int t in near) sum += order[t];
                    key[s] = near.Count == 0 ? order[s] : sum / near.Count;
                }
                var sorted = new List<int>(layer);
                sorted.Sort((a, b) => key[a] != key[b] ? key[a].CompareTo(key[b]) : order[a].CompareTo(order[b]));
                layers[d] = sorted;
                for (int k = 0; k < sorted.Count; k++) order[sorted[k]] = k;
            }
            int now = Crossings();
            if (now < 0) { bestLayers = Array.ConvertAll(layers, l => new List<int>(l)); best = -1; break; }
            if (best < 0 || now < best) { best = now; bestLayers = Array.ConvertAll(layers, l => new List<int>(l)); }
        }
        layers = bestLayers;
        Index();

        bool lr = graph.LR;
        float Size(int s) => s < n ? (lr ? graph.Nodes[s].H : graph.Nodes[s].W) : 0f;
        float Gap(int a, int b) => a < n && b < n ? (lr ? 36f : 40f) : LaneGap;
        var center = new float[slots];
        foreach (var layer in layers)
        {
            float cursor = 0f;
            for (int k = 0; k < layer.Count; k++)
            {
                if (k > 0) cursor += Gap(layer[k - 1], layer[k]);
                center[layer[k]] = cursor + Size(layer[k]) / 2f;
                cursor += Size(layer[k]);
            }
            foreach (int s in layer) center[s] -= cursor / 2f;
        }
        for (int pass = 0; pass < 8; pass++)
        {
            bool down = pass % 2 == 0;
            for (int step = 1; step <= maxDepth; step++)
            {
                var layer = layers[down ? step : maxDepth - step];
                var want = new float[layer.Count];
                for (int k = 0; k < layer.Count; k++)
                {
                    var near = down ? ups[layer[k]] : downs[layer[k]];
                    float sum = 0f;
                    foreach (int t in near) sum += center[t];
                    want[k] = near.Count == 0 ? center[layer[k]] : sum / near.Count;
                }
                Place(layer, want, center, Size, Gap);
            }
        }
        float low = float.MaxValue;
        for (int s = 0; s < slots; s++) low = MathF.Min(low, center[s] - Size(s) / 2f);
        for (int s = 0; s < slots; s++) center[s] += Margin - low;

        // Main axis: rows of fixed pitch (TD) or columns as wide as their widest box (LR).
        var mainStart = new float[maxDepth + 1];
        var mainSize = new float[maxDepth + 1];
        float main = Margin;
        for (int d = 0; d <= maxDepth; d++)
        {
            float widest = 0f;
            float tallest = 0f;
            foreach (int s in layers[d]) if (s < n) tallest = MathF.Max(tallest, graph.Nodes[s].H);
            foreach (int s in layers[d]) if (s < n) widest = MathF.Max(widest, graph.Nodes[s].W);
            mainStart[d] = main;
            mainSize[d] = lr ? MathF.Max(widest, 40f) : MathF.Max(tallest, Band);
            main += lr ? mainSize[d] + 70f : mainSize[d] + RowGap;
        }
        for (int i = 0; i < n; i++)
        {
            var node = graph.Nodes[i];
            if (lr) { node.X = mainStart[depth[i]] + (mainSize[depth[i]] - node.W) / 2f; node.Y = center[i] - node.H / 2f; }
            else { node.X = center[i] - node.W / 2f; node.Y = mainStart[depth[i]] + (mainSize[depth[i]] - node.H) / 2f; }
        }

        // Ports: several edges on one side of a box fan out across its middle, ordered by
        // where they are heading, instead of all meeting in one point.
        var outPort = new float[graph.Edges.Count];
        var inPort = new float[graph.Edges.Count];
        float Next(int e) => chains[e].Count > 0 ? center[chains[e][0]] : center[V(e)];
        float Prev(int e) => chains[e].Count > 0 ? center[chains[e][^1]] : center[U(e)];
        var byNodeOut = new List<int>[n]; var byNodeIn = new List<int>[n];
        for (int i = 0; i < n; i++) { byNodeOut[i] = new(); byNodeIn[i] = new(); }
        for (int e = 0; e < m; e++) { if (self[e]) continue; byNodeOut[U(e)].Add(e); byNodeIn[V(e)].Add(e); }
        void Spread(int node, List<int> edges, float[] port, Func<int, float> toward)
        {
            if (edges.Count == 0) return;
            edges.Sort((a, b) => toward(a).CompareTo(toward(b)));
            float size = Size(node);
            float step = edges.Count > 1 ? MathF.Min(12f, size * 0.6f / (edges.Count - 1)) : 0f;
            for (int k = 0; k < edges.Count; k++) port[edges[k]] = center[node] + (k - (edges.Count - 1) / 2f) * step;
        }
        for (int i = 0; i < n; i++) { Spread(i, byNodeOut[i], outPort, Next); Spread(i, byNodeIn[i], inPort, Prev); }
        (float x, float y) Point(float cross, float along) => lr ? (along, cross) : (cross, along);
        for (int e = 0; e < graph.Edges.Count; e++)
        {
            var edge = graph.Edges[e];
            if (self[e]) { edge.Path = SelfLoop(graph.Nodes[edge.From], lr); continue; }
            var from = graph.Nodes[U(e)]; var to = graph.Nodes[V(e)];
            var path = new List<(float x, float y)> { Point(outPort[e], lr ? from.X + from.W : from.Y + from.H) };
            foreach (int s in chains[e])
            {
                path.Add(Point(center[s], mainStart[layerOf[s]]));
                path.Add(Point(center[s], mainStart[layerOf[s]] + mainSize[layerOf[s]]));
            }
            path.Add(Point(inPort[e], lr ? to.X : to.Y));
            if (back[e]) path.Reverse();   // laid out reversed, drawn the original way: the arrow still ends at the target
            edge.Path = path;
        }

        if (graph.Reverse)
        {
            var extent = Extent(graph);
            foreach (var node in graph.Nodes)
                if (lr) node.X = extent.w - node.X - node.W;
                else node.Y = extent.h - node.Y - node.H;
            foreach (var edge in graph.Edges)
                for (int k = 0; k < edge.Path.Count; k++)
                {
                    var (x, y) = edge.Path[k];
                    edge.Path[k] = lr ? (extent.w - x, y) : (x, extent.h - y);
                }
        }

        // S86:横线上方的标签、翻到左侧的标签可能伸出上/左边距,此时整图平移,
        // 内容四周至少留 Margin —— 底板上下留白相等,左侧宽标签也不被裁掉。
        float minX = float.MaxValue, minY = float.MaxValue;
        foreach (var edge in graph.Edges)
            if (edge.Path != null && edge.Path.Count >= 2 && !string.IsNullOrEmpty(edge.Label))
            {
                var (lx, ly, _, _) = LabelBox(graph, edge);
                minX = MathF.Min(minX, lx); minY = MathF.Min(minY, ly);
            }
        float dx = MathF.Max(0f, Margin - minX), dy = MathF.Max(0f, Margin - minY);
        if (dx > 0f || dy > 0f)
        {
            foreach (var node in graph.Nodes) { node.X += dx; node.Y += dy; }
            foreach (var edge in graph.Edges)
                if (edge.Path != null)
                    for (int k = 0; k < edge.Path.Count; k++)
                        edge.Path[k] = (edge.Path[k].x + dx, edge.Path[k].y + dy);
        }
        return true;
    }

    // A self loop ("轮询 → 轮询") leaves and re-enters its box on a side that layered edges do not
    // use: the right side in top-down charts, the bottom in left-to-right ones.
    private const float LoopOut = 24f;
    private static List<(float x, float y)> SelfLoop(MermaidGraph.MNode node, bool lr)
    {
        if (lr)
        {
            float y = node.Y + node.H, a = node.X + node.W * 0.3f, b = node.X + node.W * 0.7f;
            return new() { (a, y), (a, y + LoopOut), (b, y + LoopOut), (b, y) };
        }
        float x = node.X + node.W, upper = node.Y + node.H * 0.2f, lower = node.Y + node.H * 0.8f;
        return new() { (x, upper), (x + LoopOut, upper), (x + LoopOut, lower), (x, lower) };
    }

    // Long labels ("DocumentTabs 打开与标签事务") break once, at the space or CJK boundary that
    // balances the two lines, so a row of siblings stays readable instead of very wide.
    // The break is decided here, not left to DirectWrite, so a name is never split from
    // an arbitrary point of its description.
    private static float TextWidth(string text, int start, int end)
    {
        float width = 0f;
        for (int i = start; i < end; i++) width += text[i] > 0x2E7F ? 17f : 8.5f;
        return width;
    }

    private static (string shown, float width, int lines) Wrap(string text)
    {
        float whole = TextWidth(text, 0, text.Length);
        if (whole <= WrapAt) return (text, whole, 1);
        int bestAt = -1; float bestWidth = whole;
        bool spaced = text.IndexOf(' ') > 0;   // a name and its description: only break between words
        for (int i = 1; i < text.Length; i++)
        {
            bool space = text[i] == ' ';
            bool cjk = text[i - 1] > 0x2E7F && text[i] > 0x2E7F;
            if (spaced ? !space : !cjk) continue;
            float width = MathF.Max(TextWidth(text, 0, i), TextWidth(text, space ? i + 1 : i, text.Length));
            if (width < bestWidth) { bestWidth = width; bestAt = i; }
        }
        if (bestAt < 0) return (text, whole, 1);
        int rest = text[bestAt] == ' ' ? bestAt + 1 : bestAt;
        return (text.Substring(0, bestAt).TrimEnd() + "\n" + text.Substring(rest).TrimStart(), bestWidth, 2);
    }

    // Ordered least-squares placement of one layer: minimise sum (x_k - want_k)^2 subject
    // to keeping the order and each pair at least (half sizes + gap) apart. Subtracting the
    // cumulative minimum spacing turns it into isotonic regression, solved by pooling.
    private static void Place(List<int> layer, float[] want, float[] center, Func<int, float> size, Func<int, int, float> gap)
    {
        int m = layer.Count;
        if (m == 0) return;
        var offset = new float[m];
        for (int k = 1; k < m; k++) offset[k] = offset[k - 1] + (size(layer[k - 1]) + size(layer[k])) / 2f + gap(layer[k - 1], layer[k]);
        var value = new List<float>(); var weight = new List<int>();
        for (int k = 0; k < m; k++)
        {
            value.Add(want[k] - offset[k]); weight.Add(1);
            while (value.Count > 1 && value[^2] > value[^1])
            {
                float merged = (value[^2] * weight[^2] + value[^1] * weight[^1]) / (weight[^2] + weight[^1]);
                int w = weight[^2] + weight[^1];
                value.RemoveAt(value.Count - 1); weight.RemoveAt(weight.Count - 1);
                value[^1] = merged; weight[^1] = w;
            }
        }
        int index = 0;
        for (int block = 0; block < value.Count; block++)
            for (int j = 0; j < weight[block]; j++, index++) center[layer[index]] = value[block] + offset[index];
    }

    // Full polyline from the source port to the target port, in graph coordinates.
    internal static List<(float x, float y)> EdgePath(MermaidGraph graph, MermaidGraph.MEdge edge)
    {
        if (edge.Path != null && edge.Path.Count >= 2) return new List<(float x, float y)>(edge.Path);
        var from = graph.Nodes[edge.From]; var to = graph.Nodes[edge.To];
        return graph.LR
            ? new() { (from.X + (graph.Reverse ? 0f : from.W), from.Y + from.H / 2f), (to.X + (graph.Reverse ? to.W : 0f), to.Y + to.H / 2f) }
            : new() { (from.X + from.W / 2f, from.Y + (graph.Reverse ? 0f : from.H)), (to.X + to.W / 2f, to.Y + (graph.Reverse ? to.H : 0f)) };
    }

    // Edge labels sit by the middle segment of the path: above it when it runs across, beside it
    // (to the right) when it runs up or down, so the text never lies on the line itself.
    internal const float LabelGap = 6f;
    internal static (float x, float y, bool beside) LabelAt(List<(float x, float y)> path)
    {
        int mid = (path.Count - 1) / 2;
        var (ax, ay) = path[mid]; var (bx, by) = path[mid + 1];
        return ((ax + bx) / 2f, (ay + by) / 2f, MathF.Abs(by - ay) > 2f * MathF.Abs(bx - ax));
    }

    // 标签相对锚点的摆放,绘制与测试共用:横线放上方、竖线放右侧;outside 时翻到下方 / 左侧。
    internal const float LabelBelowGap = 2f;
    internal static (float x, float y) LabelOrigin(float x, float y, bool beside, bool outside, float w, float h)
        => beside ? (outside ? x - LabelGap - w : x + LabelGap, y - h / 2f)
                  : (x - w / 2f, outside ? y + LabelBelowGap : y - h);

    // S85:一对来回的平行边(同一对节点、走向相同)只隔约 12,标签放在两线之间会压到另一条。
    // 另一条在本条上方(横线)或右侧(竖线)时,本条标签翻到下方 / 左侧,两个标签各在外侧。
    internal static bool LabelOutside(MermaidGraph graph, MermaidGraph.MEdge edge)
    {
        if (edge.From == edge.To) return false;
        var (x, y, beside) = LabelAt(EdgePath(graph, edge));
        foreach (var other in graph.Edges)
        {
            if (ReferenceEquals(other, edge) || other.From == other.To) continue;
            if (!(other.From == edge.From && other.To == edge.To || other.From == edge.To && other.To == edge.From)) continue;
            var (ox, oy, otherBeside) = LabelAt(EdgePath(graph, other));
            if (otherBeside == beside && (beside ? ox > x : oy < y)) return true;
        }
        return false;
    }

    internal static (float x, float y, float w, float h) LabelBox(MermaidGraph graph, MermaidGraph.MEdge edge)
    {
        var (x, y, beside) = LabelAt(EdgePath(graph, edge));
        float w = TextWidth(edge.Label, 0, edge.Label.Length), h = LineH;
        var (lx, ly) = LabelOrigin(x, y, beside, LabelOutside(graph, edge), w, h);
        return (lx, ly, w, h);
    }

    internal static (float ax, float ay, float bx, float by) EdgePoints(MermaidGraph graph, MermaidGraph.MEdge edge)
    {
        var path = EdgePath(graph, edge);
        return (path[0].x, path[0].y, path[^1].x, path[^1].y);
    }

    public static (float w, float h) Extent(MermaidGraph graph)
    {
        float width = 0, height = 0;
        foreach (var node in graph.Nodes) { width = MathF.Max(width, node.X + node.W); height = MathF.Max(height, node.Y + node.H); }
        foreach (var edge in graph.Edges)
            if (edge.Path != null)
                foreach (var (x, y) in edge.Path) { width = MathF.Max(width, x); height = MathF.Max(height, y); }
        foreach (var edge in graph.Edges)
            if (edge.Path != null && edge.Path.Count >= 2 && !string.IsNullOrEmpty(edge.Label))
            {
                bool beside = LabelAt(edge.Path).beside;
                var (lx, ly, lw, lh) = LabelBox(graph, edge);
                if (beside) width = MathF.Max(width, lx + lw);   // 放在右侧的标签(翻到左侧的不越过线)
                else height = MathF.Max(height, ly + lh);        // 翻到下方的标签(放在上方的不越过线)
            }
        return (width + Margin, height + Margin);
    }
}
