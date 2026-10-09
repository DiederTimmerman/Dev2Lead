namespace Dev2Lead.Services;

internal static class ClientErrors
{
    public static bool IsExpected(Exception exception) => exception is InvalidDataException or IOException or HttpRequestException
        or InvalidOperationException or System.Text.Json.JsonException or OperationCanceledException or UnauthorizedAccessException
        or System.Xml.XmlException or System.Security.Cryptography.CryptographicException or ArgumentException
        or NotSupportedException or UglyToad.PdfPig.Core.PdfDocumentFormatException or KeyNotFoundException
        or Google.Apis.Auth.InvalidJwtException;
}
