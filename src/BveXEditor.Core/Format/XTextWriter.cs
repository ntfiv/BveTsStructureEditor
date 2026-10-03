using System.Globalization;
using System.Numerics;
using System.Text;
using BveXEditor.Core.Model;

namespace BveXEditor.Core.Format;

public sealed class XWriteOptions
{
    /// <summary>ファイルの文字コード。テクスチャ名に日本語があるとき効く。BVE 向けは Shift_JIS が無難。</summary>
    public Encoding Encoding { get; set; } = XFile.ShiftJis;
    /// <summary>小数点以下の最大桁数。</summary>
    public int Precision { get; set; } = 6;
    /// <summary>法線を持たないメッシュに法線を作るときのスムーズ角（度）。</summary>
    public float SmoothAngle { get; set; } = 30f;
    /// <summary>template 宣言を先頭に書く（古いローダー向け）。</summary>
    public bool WriteTemplates { get; set; }
}

/// <summary>
/// テキスト形式 (`xof 0303txt 0032`) の書き出し。
/// テンプレートの並び (MeshMaterialList → MeshNormals → MeshTextureCoords) は BVE で読める実績のある順。
/// </summary>
public static class XTextWriter
{
    public static string Write(XScene scene, XWriteOptions? options = null)
    {
        options ??= new XWriteOptions();
        var fmt = "0." + new string('#', Math.Clamp(options.Precision, 1, 9));
        string F(float v)
        {
            var s = v.ToString(fmt, CultureInfo.InvariantCulture);
            return s == "-0" ? "0" : s;
        }

        var sb = new StringBuilder();
        sb.Append("xof 0303txt 0032\n");
        if (options.WriteTemplates) sb.Append(Templates);

        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mesh in scene.Meshes)
        {
            if (mesh.Faces.Count == 0) continue;
            sb.Append('\n');
            var name = UniqueName(SanitizeName(mesh.Name), usedNames);
            sb.Append("Mesh ").Append(name).Append(" {\n");

            sb.Append(' ').Append(mesh.Positions.Count).Append(";\n");
            for (int i = 0; i < mesh.Positions.Count; i++)
            {
                var p = mesh.Positions[i];
                sb.Append(' ').Append(F(p.X)).Append(';').Append(F(p.Y)).Append(';').Append(F(p.Z)).Append(';')
                  .Append(i == mesh.Positions.Count - 1 ? ";\n" : ",\n");
            }
            WriteFaces(sb, mesh.Faces.Select(f => f.Indices).ToList(), " ");

            // マテリアル
            mesh.EnsureMaterial();
            sb.Append(" MeshMaterialList {\n");
            sb.Append("  ").Append(mesh.Materials.Count).Append(";\n");
            sb.Append("  ").Append(mesh.Faces.Count).Append(";\n");
            for (int i = 0; i < mesh.Faces.Count; i++)
                sb.Append("  ").Append(mesh.Faces[i].Material).Append(i == mesh.Faces.Count - 1 ? ";;\n" : ",\n");
            foreach (var m in mesh.Materials)
            {
                sb.Append("  Material {\n");
                sb.Append("   ").Append(F(m.FaceColor.X)).Append(';').Append(F(m.FaceColor.Y)).Append(';')
                  .Append(F(m.FaceColor.Z)).Append(';').Append(F(m.FaceColor.W)).Append(";;\n");
                sb.Append("   ").Append(F(m.Power)).Append(";\n");
                sb.Append("   ").Append(F(m.Specular.X)).Append(';').Append(F(m.Specular.Y)).Append(';').Append(F(m.Specular.Z)).Append(";;\n");
                sb.Append("   ").Append(F(m.Emissive.X)).Append(';').Append(F(m.Emissive.Y)).Append(';').Append(F(m.Emissive.Z)).Append(";;\n");
                if (!string.IsNullOrEmpty(m.Texture))
                    sb.Append("   TextureFilename {\n    \"").Append(m.Texture.Replace("\"", "")).Append("\";\n   }\n");
                sb.Append("  }\n");
            }
            sb.Append(" }\n");

            // 法線（無ければ作る）
            List<Vector3> normals;
            List<int[]> faceNormals;
            if (mesh.HasExplicitNormals)
            {
                normals = mesh.Normals;
                faceNormals = mesh.Faces.Select(f => f.NormalIndices!).ToList();
            }
            else
            {
                var (n, fn) = MeshMath.ComputeNormals(mesh, options.SmoothAngle);
                normals = n;
                faceNormals = fn.ToList();
            }
            sb.Append(" MeshNormals {\n");
            sb.Append("  ").Append(normals.Count).Append(";\n");
            for (int i = 0; i < normals.Count; i++)
            {
                var n = normals[i];
                sb.Append("  ").Append(F(n.X)).Append(';').Append(F(n.Y)).Append(';').Append(F(n.Z)).Append(';')
                  .Append(i == normals.Count - 1 ? ";\n" : ",\n");
            }
            WriteFaces(sb, faceNormals, "  ");
            sb.Append(" }\n");

            if (mesh.HasUV)
            {
                sb.Append(" MeshTextureCoords {\n");
                sb.Append("  ").Append(mesh.TexCoords.Count).Append(";\n");
                for (int i = 0; i < mesh.TexCoords.Count; i++)
                {
                    var t = mesh.TexCoords[i];
                    sb.Append("  ").Append(F(t.X)).Append(';').Append(F(t.Y)).Append(';')
                      .Append(i == mesh.TexCoords.Count - 1 ? ";\n" : ",\n");
                }
                sb.Append(" }\n");
            }

            if (mesh.VertexColors.Count == mesh.Positions.Count && mesh.VertexColors.Count > 0)
            {
                sb.Append(" MeshVertexColors {\n");
                sb.Append("  ").Append(mesh.VertexColors.Count).Append(";\n");
                for (int i = 0; i < mesh.VertexColors.Count; i++)
                {
                    var c = mesh.VertexColors[i];
                    sb.Append("  ").Append(i).Append(';').Append(F(c.X)).Append(';').Append(F(c.Y)).Append(';')
                      .Append(F(c.Z)).Append(';').Append(F(c.W)).Append(";;")
                      .Append(i == mesh.VertexColors.Count - 1 ? ";\n" : ",\n");
                }
                sb.Append(" }\n");
            }

            sb.Append("}\n");
        }
        return sb.ToString();
    }

    private static void WriteFaces(StringBuilder sb, List<int[]> faces, string indent)
    {
        sb.Append(indent).Append(faces.Count).Append(";\n");
        for (int i = 0; i < faces.Count; i++)
        {
            var f = faces[i];
            sb.Append(indent).Append(f.Length).Append(';');
            for (int j = 0; j < f.Length; j++)
            {
                if (j > 0) sb.Append(',');
                sb.Append(f[j]);
            }
            sb.Append(i == faces.Count - 1 ? ";;\n" : ";,\n");
        }
    }

    /// <summary>.x の名前に使えない文字を _ に置き換える（ASCII 英数字と _ のみ）。</summary>
    public static string SanitizeName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
            sb.Append(c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' ? c : '_');
        if (sb.Length == 0 || char.IsDigit(sb[0])) sb.Insert(0, "m_");
        return sb.ToString();
    }

    private static string UniqueName(string name, HashSet<string> used)
    {
        var n = name;
        for (int i = 2; !used.Add(n); i++) n = $"{name}_{i}";
        return n;
    }

    private const string Templates = """

template Vector {
 <3d82ab5e-62da-11cf-ab39-0020af71e433>
 FLOAT x;
 FLOAT y;
 FLOAT z;
}

template MeshFace {
 <3d82ab5f-62da-11cf-ab39-0020af71e433>
 DWORD nFaceVertexIndices;
 array DWORD faceVertexIndices[nFaceVertexIndices];
}

template Mesh {
 <3d82ab44-62da-11cf-ab39-0020af71e433>
 DWORD nVertices;
 array Vector vertices[nVertices];
 DWORD nFaces;
 array MeshFace faces[nFaces];
 [...]
}

template MeshNormals {
 <f6f23f43-7686-11cf-8f52-0040333594a3>
 DWORD nNormals;
 array Vector normals[nNormals];
 DWORD nFaceNormals;
 array MeshFace faceNormals[nFaceNormals];
}

template Coords2d {
 <f6f23f44-7686-11cf-8f52-0040333594a3>
 FLOAT u;
 FLOAT v;
}

template MeshTextureCoords {
 <f6f23f40-7686-11cf-8f52-0040333594a3>
 DWORD nTextureCoords;
 array Coords2d textureCoords[nTextureCoords];
}

template ColorRGBA {
 <35ff44e0-6c7c-11cf-8f52-0040333594a3>
 FLOAT red;
 FLOAT green;
 FLOAT blue;
 FLOAT alpha;
}

template ColorRGB {
 <d3e16e81-7835-11cf-8f52-0040333594a3>
 FLOAT red;
 FLOAT green;
 FLOAT blue;
}

template Material {
 <3d82ab4d-62da-11cf-ab39-0020af71e433>
 ColorRGBA faceColor;
 FLOAT power;
 ColorRGB specularColor;
 ColorRGB emissiveColor;
 [...]
}

template MeshMaterialList {
 <f6f23f42-7686-11cf-8f52-0040333594a3>
 DWORD nMaterials;
 DWORD nFaceIndexes;
 array DWORD faceIndexes[nFaceIndexes];
 [Material <3d82ab4d-62da-11cf-ab39-0020af71e433>]
}

template TextureFilename {
 <a42790e1-7810-11cf-8f52-0040333594a3>
 STRING filename;
}

template IndexedColor {
 <1630b820-7842-11cf-8f52-0040333594a3>
 DWORD index;
 ColorRGBA indexColor;
}

template MeshVertexColors {
 <1630b821-7842-11cf-8f52-0040333594a3>
 DWORD nVertexColors;
 array IndexedColor vertexColors[nVertexColors];
}

""";
}
