"""
tzip (MSZIP 圧縮テキスト) のテスト用 .x を作る。

本物の MSZIP はチャンクをまたいで前の出力を後方参照するので、zlib の zdict に
直前 32KB を渡して再現する。.NET 側では同じことができないため Python で作って置いておく。

    python tests/tools/make_zip_fixtures.py
"""
import struct
import zlib
from pathlib import Path

OUT = Path(__file__).resolve().parent.parent / "BveXEditor.Core.Tests" / "Fixtures"
CHUNK = 32768


def grid_x(n: int) -> bytes:
    lines = ["xof 0303txt 0032", "// 大きめの格子メッシュ（複数チャンクになるように）", "Mesh grid {"]
    verts = [(x * 0.5, 0.0, z * 0.5) for z in range(n + 1) for x in range(n + 1)]
    lines.append(f" {len(verts)};")
    lines += [f" {x:.6f};{y:.6f};{z:.6f};" + (";" if i == len(verts) - 1 else ",") for i, (x, y, z) in enumerate(verts)]
    faces = []
    for z in range(n):
        for x in range(n):
            a = z * (n + 1) + x
            faces.append((a, a + n + 1, a + n + 2, a + 1))
    lines.append(f" {len(faces)};")
    lines += [f" 4;{a},{b},{c},{d};" + (";" if i == len(faces) - 1 else ",") for i, (a, b, c, d) in enumerate(faces)]
    lines += [
        " MeshMaterialList {", "  1;", "  1;", "  0;;",
        "  Material {", "   1.0;1.0;1.0;1.0;;", "   0.0;", "   0.0;0.0;0.0;;", "   0.0;0.0;0.0;;",
        '   TextureFilename { "テクスチャ.png"; }', "  }", " }",
        " MeshTextureCoords {", f"  {len(verts)};",
    ]
    lines += [f"  {x / (n * 0.5):.6f};{z / (n * 0.5):.6f};" + (";" if i == len(verts) - 1 else ",") for i, (x, _, z) in enumerate(verts)]
    lines += [" }", "}", ""]
    return "\n".join(lines).encode("cp932")


def mszip(text: bytes, fmt: bytes, ck_in_size: bool) -> bytes:
    header, body = text[:16], text[16:]
    assert header.startswith(b"xof ")
    header = header[:8] + fmt + header[12:]
    out = bytearray(header)
    out += struct.pack("<I", len(text))
    history = b""
    for i in range(0, len(body), CHUNK):
        raw = body[i:i + CHUNK]
        if history:
            c = zlib.compressobj(9, zlib.DEFLATED, -15, zdict=history[-CHUNK:])
        else:
            c = zlib.compressobj(9, zlib.DEFLATED, -15)
        comp = c.compress(raw) + c.flush(zlib.Z_FINISH)
        size = len(comp) + 2 if ck_in_size else len(comp)
        out += struct.pack("<HH", len(raw), size) + b"CK" + comp
        history += raw
    return bytes(out)


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    text = grid_x(60)
    assert len(text) > CHUNK * 2, len(text)
    (OUT / "grid_txt.x").write_bytes(text)
    (OUT / "grid_tzip.x").write_bytes(mszip(text, b"tzip", ck_in_size=True))
    (OUT / "grid_tzip_nock.x").write_bytes(mszip(text, b"tzip", ck_in_size=False))
    print("text", len(text), "bytes ->", OUT)


if __name__ == "__main__":
    main()
