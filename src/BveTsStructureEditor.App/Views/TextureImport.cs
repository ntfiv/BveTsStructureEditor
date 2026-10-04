using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BveTsStructureEditor.App.Rendering;
using BveTsStructureEditor.Core.Imaging;
using Microsoft.Win32;

namespace BveTsStructureEditor.App.Views;

/// <summary>取り込んだテクスチャ画像。<see cref="Aspect"/> は板にするときの見た目の縦横比（幅 / 高さ）。</summary>
public sealed record ImportedTexture(string Path, float Aspect, bool Edited);

/// <summary>画像の取り込み前に編集ダイアログを出し、編集した画像を PNG で保存する。</summary>
public static class TextureImport
{
    /// <summary>
    /// 編集ダイアログを開く。「編集した画像を使う」なら保存したパス、「そのまま使う」なら元のパス、キャンセルなら null。
    /// 保存先は .x と同じフォルダー（未保存なら聞く）で、名前は「元の名前_edit.png」。
    /// </summary>
    public static ImportedTexture? Run(Window? owner, string sourcePath, string? modelDirectory, TextureCache textures)
    {
        var bitmap = textures.Get(Path.GetFullPath(sourcePath), out var error);
        if (bitmap == null)
        {
            MessageBox.Show(owner!, error ?? "画像を読めません", "テクスチャの編集", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        var dlg = new TextureEditorDialog(ToRgba(bitmap), Path.GetFileName(sourcePath)) { Owner = owner };
        if (dlg.ShowDialog() != true) return null;
        if (dlg.UseOriginal) return new ImportedTexture(sourcePath, (float)bitmap.PixelWidth / bitmap.PixelHeight, false);

        var result = dlg.Result!;
        var path = ChooseOutputPath(owner, sourcePath, modelDirectory);
        if (path == null) return null;
        SavePng(result.Image, path);
        return new ImportedTexture(path, result.DisplayAspect, true);
    }

    private static string? ChooseOutputPath(Window? owner, string sourcePath, string? modelDirectory)
    {
        var baseName = Path.GetFileNameWithoutExtension(sourcePath) + "_edit";
        if (modelDirectory == null)
        {
            var save = new SaveFileDialog
            {
                Title = "編集した画像の保存先（.x が未保存なので場所を選んでください）",
                FileName = baseName + ".png",
                Filter = "PNG (*.png)|*.png",
                InitialDirectory = Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? "",
            };
            return save.ShowDialog(owner) == true ? save.FileName : null;
        }
        var path = Path.Combine(modelDirectory, baseName + ".png");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(modelDirectory, $"{baseName}_{i}.png");
        return path;
    }

    public static RgbaImage ToRgba(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        return new RgbaImage(converted.PixelWidth, converted.PixelHeight, pixels);
    }

    public static BitmapSource ToBitmap(RgbaImage image)
    {
        var bmp = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Bgra, image.Width * 4);
        bmp.Freeze();
        return bmp;
    }

    public static void SavePng(RgbaImage image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(ToBitmap(image)));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
