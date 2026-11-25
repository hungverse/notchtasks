using NotchTasks.Services.Sync;
using QRCoder;
using Xunit;

namespace NotchTasks.AppTests;

/// The pair QR code (Mac SyncHub.qr): the QRCoder matrix at correction level M, drawn as a square vector image.
public sealed class QrImageTests
{
    private const string Url = "https://notchtasks.web.app/pair#AAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public void Qr_Matrix_IsSquareAndEncodesUrl_LevelM()
    {
        var modules = QrImage.Matrix(Url);

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(Url, QRCodeGenerator.ECCLevel.M);
        var size = data.ModuleMatrix.Count;

        Assert.Equal(size, modules.GetLength(0));
        Assert.Equal(size, modules.GetLength(1));
        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++)
            {
                Assert.Equal(data.ModuleMatrix[row][column], modules[row, column]);
            }
        }

        // QRCoder's matrix carries the 4-module quiet zone: its first rows are light, and the finder patterns start at (4, 4).
        for (var column = 0; column < size; column++)
        {
            Assert.False(modules[0, column]);
        }
        Assert.True(modules[4, 4]);
        Assert.True(modules[4, size - 5]);
        Assert.True(modules[size - 5, 4]);
    }

    [Fact]
    public void Create_IsASquareImageOfTheMatrixSize()
    {
        Sta.Run(() =>
        {
            var size = QrImage.Matrix(Url).GetLength(0);
            var image = QrImage.Create(Url);

            Assert.Equal(size, image.Width);
            Assert.Equal(image.Width, image.Height);
        });
    }
}
