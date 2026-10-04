using System.Numerics;

namespace BveTsStructureEditor.Core.Model;

public static class MeshMath
{
    /// <summary>
    /// 多角形の表向き法線（正規化済み）。DirectX の時計回り＝表に合わせ、
    /// 三角形 a,b,c なら Cross(b-a, c-a) の向き。凹多角形でもよいよう Newell 法で求める。
    /// </summary>
    public static Vector3 PolygonNormal(IReadOnlyList<Vector3> pos, int[] idx)
    {
        var n = Vector3.Zero;
        for (int i = 0; i < idx.Length; i++)
        {
            var a = pos[idx[i]];
            var b = pos[idx[(i + 1) % idx.Length]];
            // Newell 法の結果は三角形なら Cross(b-a, c-a) と同じ向きになる
            n.X += (a.Y - b.Y) * (a.Z + b.Z);
            n.Y += (a.Z - b.Z) * (a.X + b.X);
            n.Z += (a.X - b.X) * (a.Y + b.Y);
        }
        var len = n.Length();
        return len > 1e-12f ? n / len : Vector3.Zero;
    }

    /// <summary>
    /// 角ごとの法線を作る。隣り合う面の角度が <paramref name="smoothAngleDeg"/> 以下なら滑らかにつなぐ。
    /// 戻り値は (法線の一覧, 面ごとの法線インデックス)。
    /// </summary>
    public static (List<Vector3> Normals, int[][] FaceNormalIndices) ComputeNormals(XMesh mesh, float smoothAngleDeg = 30f)
    {
        var faceNormals = mesh.Faces.Select(mesh.FaceNormal).ToArray();
        var cosLimit = MathF.Cos(smoothAngleDeg * MathF.PI / 180f);

        // 頂点 → それを使う面
        var byVertex = new List<int>[mesh.Positions.Count];
        for (int f = 0; f < mesh.Faces.Count; f++)
            foreach (var v in mesh.Faces[f].Indices)
                (byVertex[v] ??= []).Add(f);

        var normals = new List<Vector3>();
        var cache = new Dictionary<(int V, long Key), int>();
        var result = new int[mesh.Faces.Count][];
        for (int f = 0; f < mesh.Faces.Count; f++)
        {
            var face = mesh.Faces[f];
            var nf = faceNormals[f];
            result[f] = new int[face.Indices.Length];
            for (int c = 0; c < face.Indices.Length; c++)
            {
                int v = face.Indices[c];
                var sum = Vector3.Zero;
                long key = 0;
                foreach (var g in byVertex[v])
                {
                    if (Vector3.Dot(faceNormals[g], nf) >= cosLimit - 1e-5f)
                    {
                        sum += faceNormals[g];
                        key = key * 31 + g;
                    }
                }
                var n = sum.LengthSquared() > 1e-12f ? Vector3.Normalize(sum) : (nf == Vector3.Zero ? Vector3.UnitY : nf);
                // 同じ頂点・同じ面グループなら同じ法線を共有する
                var ck = (v, key);
                if (!cache.TryGetValue(ck, out int ni))
                {
                    ni = normals.Count;
                    normals.Add(n);
                    cache[ck] = ni;
                }
                result[f][c] = ni;
            }
        }
        return (normals, result);
    }

    /// <summary>多角形を扇状に三角形分割する（凸を前提）。</summary>
    public static IEnumerable<(int A, int B, int C)> Fan(int[] idx)
    {
        for (int i = 1; i + 1 < idx.Length; i++) yield return (idx[0], idx[i], idx[i + 1]);
    }
}
