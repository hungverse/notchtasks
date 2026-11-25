using System.Windows;
using System.Windows.Media;
using QRCoder;

namespace NotchTasks.Services.Sync;

/// The pair code as a QR image (Mac SyncHub.qr: correction level M). The modules are drawn as vectors, so the image is sharp at
/// any size: a white square, and the dark modules as one geometry in black. The quiet zone is the one QRCoder's matrix carries.
public static class QrImage
{
    /// The code for `text` as a drawing. Its size is the matrix's side in module units (the quiet zone included).
    public static DrawingImage Create(string text)
    {
        var modules = Matrix(text);
        var size = modules.GetLength(0);

        var dark = new GeometryGroup();
        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++)
            {
                if (modules[row, column])
                {
                    dark.Children.Add(new RectangleGeometry(new Rect(column, row, 1, 1)));
                }
            }
        }
        dark.Freeze();

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, size, size))));
        group.Children.Add(new GeometryDrawing(Brushes.Black, null, dark));
        group.Freeze();

        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    /// The QRCoder module matrix of `text` at correction level M, quiet zone included (QRCoder adds the 4-module border):
    /// true for a dark module, indexed [row, column].
    internal static bool[,] Matrix(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var rows = data.ModuleMatrix;
        var size = rows.Count;
        var modules = new bool[size, size];
        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++)
            {
                modules[row, column] = rows[row][column];
            }
        }
        return modules;
    }
}
