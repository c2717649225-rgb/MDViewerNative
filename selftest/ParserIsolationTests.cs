using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MDViewerNative;

internal static class ParserIsolationTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        var sources = Enumerable.Range(0, 12).Select(i =>
            $"[shared]: /document-{i} \"title-{i}\"\n\n" +
            string.Concat(Enumerable.Repeat($"> [reference][shared] and note[^n]\n>\n> > **nested {i}**\n\n", 20)) +
            $"[^n]: footnote-{i}\n").ToArray();
        string Render(string source) => HtmlExport.Build(MdParser.Parse(source), "isolation", false);
        var expected = sources.Select(Render).ToArray();
        int failures = 0;
        string firstError = "";
        Parallel.For(0, 240, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
        {
            try
            {
                if (Render(sources[i % sources.Length]) != expected[i % sources.Length])
                { Interlocked.Increment(ref failures); Interlocked.CompareExchange(ref firstError, "cross-document output drift", ""); }
            }
            catch (Exception ex)
            { Interlocked.Increment(ref failures); Interlocked.CompareExchange(ref firstError, ex.GetType().Name, ""); }
        });
        check("Parser/parallel references footnotes and quotes", failures == 0, $"240 parses; failures={failures} {firstError}");
        check("Parser/undefined references do not inherit context", !Render("[reference][shared]\n").Contains("/document-"), "fresh context after parallel work");
    }
}
