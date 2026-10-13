using System.Runtime.CompilerServices;

namespace MDViewerNative;

// E15 / TD-04:具体的 DocView 只经这里交给宿主(Program、ReadingMemory 等)。
// 工作流层的 DocumentSession 只认 IDocumentViewAdapter;调用写法保持 session.Doc 不变。
// 单独成模块:放进 DocumentSessionView 会与 DocumentSession 互相引用,形成模块循环。
// E19a:DocViewAdapter 与默认视图工厂也移到这里,工作流层不再经 DocumentSessionView 间接依赖 DocView。

// 能交出具体 DocView 的适配器;只有宿主侧知道这一层。
internal interface IDocViewOwner : IDocumentViewAdapter
{
    DocView View { get; }
}

internal sealed class DocViewAdapter : IDocViewOwner
{
    public DocView View { get; } = new();

    public void SetDirectory(string directory) => View.DocumentDirectory = directory ?? "";

    public void Load(string directory, string text)
    {
        SetDirectory(directory);
        View.Load(text);
    }

    public void EnsureLayout() => View.EnsureLayout();

    public bool ShowToc { get => View.ShowToc; set => View.SetToc(value); }

    public void Dispose() => View.Dispose();
}

internal static class DocumentViewAccess
{
    // 程序集加载时注册:应用和自测走同一条路径,各处 new DocumentSession() 无需改动。
    [ModuleInitializer]
    internal static void RegisterViewFactory() => DocumentSession.ViewFactory = static () => new DocViewAdapter();

    extension(DocumentSession session)
    {
        // 需要时按需创建视图并载入会话文本(与原 DocumentSession.Doc 语义相同)
        public DocView Doc => ((IDocViewOwner)session.EnsureView()).View;
    }
}
