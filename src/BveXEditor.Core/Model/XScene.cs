using System.Numerics;

namespace BveXEditor.Core.Model;

/// <summary>
/// 編集対象の 1 ファイルぶん。
///
/// 座標は .x の値そのまま（BVE 空間: X 右 / Y 上 / Z 進行方向、左手系、m）。
/// 表面は DirectX 流に「表から見て時計回り」。フレーム階層は読み込み時に焼き込んで平らにする。
/// </summary>
public sealed class XScene
{
    public List<XMesh> Meshes { get; } = [];

    public XScene Clone()
    {
        var s = new XScene();
        foreach (var m in Meshes) s.Meshes.Add(m.Clone());
        return s;
    }

    public int TotalVertices => Meshes.Sum(m => m.Positions.Count);
    public int TotalFaces => Meshes.Sum(m => m.Faces.Count);

    public (Vector3 Min, Vector3 Max)? Bounds()
    {
        bool any = false;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in Meshes.SelectMany(m => m.Positions))
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
            any = true;
        }
        return any ? (min, max) : null;
    }
}

public sealed class XMesh
{
    public string Name { get; set; } = "";
    public List<Vector3> Positions { get; } = [];
    /// <summary>頂点ごとの UV。空なら UV なし。あるときは <see cref="Positions"/> と同数。</summary>
    public List<Vector2> TexCoords { get; } = [];
    /// <summary>頂点カラー。空なら無し。あるときは <see cref="Positions"/> と同数。</summary>
    public List<Vector4> VertexColors { get; } = [];
    public List<XFace> Faces { get; } = [];
    public List<XMaterial> Materials { get; } = [];

    /// <summary>
    /// 読み込んだ法線。面の <see cref="XFace.NormalIndices"/> から引く。
    /// 形を変える編集をしたら <see cref="ClearNormals"/> で捨て、書き出し時に作り直す。
    /// </summary>
    public List<Vector3> Normals { get; } = [];

    public bool HasUV => TexCoords.Count == Positions.Count && Positions.Count > 0;
    public bool HasExplicitNormals => Normals.Count > 0 && Faces.All(f => f.NormalIndices != null);

    public void ClearNormals()
    {
        Normals.Clear();
        foreach (var f in Faces) f.NormalIndices = null;
    }

    /// <summary>UV がなければ 0 で埋めて作る。</summary>
    public void EnsureUV()
    {
        while (TexCoords.Count < Positions.Count) TexCoords.Add(Vector2.Zero);
        if (TexCoords.Count > Positions.Count) TexCoords.RemoveRange(Positions.Count, TexCoords.Count - Positions.Count);
    }

    public void EnsureMaterial()
    {
        if (Materials.Count == 0) Materials.Add(new XMaterial());
        foreach (var f in Faces)
            if (f.Material < 0 || f.Material >= Materials.Count) f.Material = 0;
    }

    /// <summary>頂点を 1 つ追加する。UV・頂点カラーがあるならそれも揃えて伸ばす。</summary>
    public int AddVertex(Vector3 p, Vector2? uv = null, Vector4? color = null)
    {
        bool hadUv = HasUV || (Positions.Count == 0 && uv.HasValue);
        bool hadColor = VertexColors.Count == Positions.Count && VertexColors.Count > 0;
        Positions.Add(p);
        if (hadUv || uv.HasValue)
        {
            EnsureUVBefore(Positions.Count - 1);
            TexCoords.Add(uv ?? Vector2.Zero);
        }
        if (hadColor) VertexColors.Add(color ?? Vector4.One);
        return Positions.Count - 1;
    }

    private void EnsureUVBefore(int count)
    {
        while (TexCoords.Count < count) TexCoords.Add(Vector2.Zero);
    }

    public XMesh Clone()
    {
        var m = new XMesh { Name = Name };
        m.Positions.AddRange(Positions);
        m.TexCoords.AddRange(TexCoords);
        m.VertexColors.AddRange(VertexColors);
        m.Normals.AddRange(Normals);
        foreach (var f in Faces) m.Faces.Add(f.Clone());
        foreach (var mat in Materials) m.Materials.Add(mat.Clone());
        return m;
    }

    public Vector3 FaceNormal(XFace f) => MeshMath.PolygonNormal(Positions, f.Indices);

    public Vector3 FaceCenter(XFace f)
    {
        var c = Vector3.Zero;
        foreach (var i in f.Indices) c += Positions[i];
        return c / f.Indices.Length;
    }
}

public sealed class XFace
{
    public int[] Indices { get; set; }
    public int Material { get; set; }
    /// <summary>角ごとの法線インデックス。null なら法線は自動生成。</summary>
    public int[]? NormalIndices { get; set; }

    public XFace(int[] indices, int material = 0)
    {
        Indices = indices;
        Material = material;
    }

    public XFace Clone() => new((int[])Indices.Clone(), Material) { NormalIndices = (int[]?)NormalIndices?.Clone() };
}

public sealed class XMaterial
{
    /// <summary>拡散色 RGBA (0–1)。</summary>
    public Vector4 FaceColor { get; set; } = Vector4.One;
    public float Power { get; set; }
    public Vector3 Specular { get; set; } = Vector3.Zero;
    public Vector3 Emissive { get; set; } = Vector3.Zero;
    /// <summary>テクスチャのファイル名（.x からの相対パス）。無しは null。</summary>
    public string? Texture { get; set; }

    public XMaterial Clone() => (XMaterial)MemberwiseClone();

    public bool SameAs(XMaterial o) =>
        FaceColor == o.FaceColor && Power == o.Power && Specular == o.Specular && Emissive == o.Emissive &&
        string.Equals(Texture ?? "", o.Texture ?? "", StringComparison.OrdinalIgnoreCase);

    public string Describe()
    {
        var tex = string.IsNullOrEmpty(Texture) ? "テクスチャなし" : Texture;
        return $"({FaceColor.X:0.##}, {FaceColor.Y:0.##}, {FaceColor.Z:0.##}, α{FaceColor.W:0.##}) {tex}";
    }
}
