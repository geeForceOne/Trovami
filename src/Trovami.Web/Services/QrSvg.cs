using QRCoder;

namespace Trovami.Web.Services;

// QR codes as inline SVG, for the label print sheet and the phone pairing code. Sized by its
// container (viewBox), black on white whatever the theme, so phones read it from screen and paper.
public static class QrSvg
{
    public static string Render(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(data).GetGraphic(8, "#000000", "#ffffff", drawQuietZones: true, sizingMode: SvgQRCode.SizingMode.ViewBoxAttribute);
    }
}
