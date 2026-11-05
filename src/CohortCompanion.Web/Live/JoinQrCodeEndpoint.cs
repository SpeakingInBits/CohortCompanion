using QRCoder;

namespace CohortCompanion.Web.Live;

public static class JoinQrCodeEndpoint
{
    /// <summary>Serves the QR code for a join link as an SVG, generated on our own server.</summary>
    public static void MapJoinQrCode(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/qr/{code:regex(^\\d{{6}}$)}.svg", (string code, HttpRequest request) =>
        {
            var url = $"{request.Scheme}://{request.Host}/j/{code}";
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
            return Results.Text(new SvgQRCode(data).GetGraphic(8), "image/svg+xml");
        });
}
