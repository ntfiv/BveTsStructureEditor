using BveXEditor.App.Document;
using BveXEditor.App.Rendering;
using BveXEditor.Core.Model;

namespace BveXEditor.App;

/// <summary>各パネルからメインウィンドウに頼むこと。</summary>
public interface IEditorHost
{
    EditorDocument Document { get; }
    TextureCache Textures { get; }

    /// <summary>元に戻せる操作として実行し、結果をステータスバーに出す。例外はメッセージにする。</summary>
    void Run(string label, Func<XScene, string> operation, ChangeKind kind = ChangeKind.Geometry | ChangeKind.Structure);

    void SetStatus(string message);
}
