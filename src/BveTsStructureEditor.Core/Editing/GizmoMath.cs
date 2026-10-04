using System.Numerics;
using BveTsStructureEditor.Core.Model;

namespace BveTsStructureEditor.Core.Editing;

/// <summary>
/// ギズモのドラッグ計算。座標はすべて BVE 空間。レイはマウス位置から画面の奥へ向かう線。
/// </summary>
public static class GizmoMath
{
    /// <summary>
    /// 軸 (origin + t·axis) 上で、レイに一番近い点の t。軸とレイがほぼ平行なら null。
    /// </summary>
    public static float? ClosestOnAxis(Vector3 origin, Vector3 axis, Vector3 rayOrigin, Vector3 rayDir)
    {
        var a = Vector3.Normalize(axis);
        var d = Vector3.Normalize(rayDir);
        float b = Vector3.Dot(a, d);
        float denom = 1 - b * b;
        if (denom < 1e-4f) return null;
        var w = origin - rayOrigin;
        return (b * Vector3.Dot(d, w) - Vector3.Dot(a, w)) / denom;
    }

    /// <summary>点 <paramref name="planePoint"/> を通り法線 <paramref name="normal"/> の平面とレイの交点。平行なら null。</summary>
    public static Vector3? RayPlane(Vector3 planePoint, Vector3 normal, Vector3 rayOrigin, Vector3 rayDir)
    {
        float denom = Vector3.Dot(normal, rayDir);
        if (MathF.Abs(denom) < 1e-6f) return null;
        float u = Vector3.Dot(normal, planePoint - rayOrigin) / denom;
        return u < 0 ? null : rayOrigin + rayDir * u;
    }

    public static float Snap(float value, float step) => step > 0 ? MathF.Round(value / step) * step : value;

    public static Vector3 Snap(Vector3 v, float step) => new(Snap(v.X, step), Snap(v.Y, step), Snap(v.Z, step));

    /// <summary>角度差 (ラジアン) を -π〜π に収める。</summary>
    public static float WrapAngle(float a)
    {
        while (a > MathF.PI) a -= MathF.Tau;
        while (a < -MathF.PI) a += MathF.Tau;
        return a;
    }

    /// <summary><paramref name="axis"/> に垂直な単位ベクトルを 2 本（リングを描く・投影するための基底）。</summary>
    public static (Vector3 U, Vector3 V) Perpendiculars(Vector3 axis)
    {
        var a = Vector3.Normalize(axis);
        var helper = MathF.Abs(a.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        var u = Vector3.Normalize(Vector3.Cross(helper, a));
        var v = Vector3.Cross(a, u);
        return (u, v);
    }

    /// <summary>拡大で倍率がこれより小さくならないようにする（0 や裏返しで形が潰れないように）。</summary>
    public const float MinScale = 0.01f;

    /// <summary>
    /// 中心からの距離の比で倍率を決める。<paramref name="start"/> はドラッグ開始時の距離、<paramref name="current"/> は今の距離。
    /// 開始位置が中心に近すぎる（ほぼ 0）ときは 1 のまま。<paramref name="snap"/> が正ならその刻みに丸める。
    /// </summary>
    public static float ScaleFactor(float current, float start, float snap = 0)
    {
        if (MathF.Abs(start) < 1e-6f) return 1f;
        float f = current / start;
        if (snap > 0) f = Snap(f, snap);
        return MathF.Max(f, MinScale);
    }

    /// <summary><paramref name="pivot"/> を中心に軸ごとの倍率で拡大する行列。</summary>
    public static Matrix4x4 Scaling(Vector3 scale, Vector3 pivot) =>
        Matrix4x4.CreateTranslation(-pivot) * Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(pivot);

    /// <summary>軸まわりに <paramref name="angle"/> ラジアン回す、<paramref name="pivot"/> 中心の行列。</summary>
    public static Matrix4x4 Rotation(Vector3 axis, float angle, Vector3 pivot) =>
        Matrix4x4.CreateTranslation(-pivot) *
        Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(axis), angle) *
        Matrix4x4.CreateTranslation(pivot);
}

public static class MeshCopy
{
    /// <summary>
    /// ドラッグ中に「開始時の形に戻してから変形し直す」ためのコピー。
    /// 頂点位置・法線・面の頂点順（鏡像で入れ替わる）だけを戻す。面の数が違えば何もしない。
    /// </summary>
    public static void RestoreGeometry(XMesh target, XMesh source)
    {
        if (target.Positions.Count != source.Positions.Count || target.Faces.Count != source.Faces.Count) return;
        for (int i = 0; i < source.Positions.Count; i++) target.Positions[i] = source.Positions[i];
        target.Normals.Clear();
        target.Normals.AddRange(source.Normals);
        for (int i = 0; i < source.Faces.Count; i++)
        {
            target.Faces[i].Indices = (int[])source.Faces[i].Indices.Clone();
            target.Faces[i].NormalIndices = (int[]?)source.Faces[i].NormalIndices?.Clone();
        }
    }
}
