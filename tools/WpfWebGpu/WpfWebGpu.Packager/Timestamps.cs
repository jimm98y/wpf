// RFC 3161 time stamps, as a signature carries them so that it stays valid after its certificate expires: the
// token a time-stamp server gives for a signature's value (Authenticode, Apple's code signatures and packages).

using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;

namespace WpfWebGpu.Packager;

internal static class Timestamps
{
    /// <summary>Apple's time-stamp server: codesign's and productsign's, the one notarization takes.</summary>
    public const string Apple = "http://timestamp.apple.com/ts01";

    private const string TimeStampTokenOid = "1.2.840.113549.1.9.16.2.14";   // id-smime-aa-timeStampToken

    /// <summary>The time-stamp token (a SignedData, as the server gives it) of <paramref name="data"/>, hashed with
    /// <paramref name="hash"/>.</summary>
    public static byte[] Token(byte[] data, HashAlgorithmName hash, string url)
    {
        var request = Rfc3161TimestampRequest.CreateFromData(data, hash, requestSignerCertificates: true, nonce: RandomNumberGenerator.GetBytes(8));
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        using var body = new ByteArrayContent(request.Encode());
        body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/timestamp-query");
        HttpResponseMessage response;
        try { response = http.PostAsync(url, body).GetAwaiter().GetResult(); }
        catch (HttpRequestException e) { throw new PackagerError($"the time-stamp server {url}: {e.Message}"); }
        catch (TaskCanceledException) { throw new PackagerError($"the time-stamp server {url} did not answer"); }
        using (response)
        {
            if (!response.IsSuccessStatusCode) throw new PackagerError($"{url}: {(int)response.StatusCode} {response.ReasonPhrase}");
            var token = request.ProcessResponse(response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult(), out _);
            return token.AsSignedCms().Encode();
        }
    }

    /// <summary>A CMS's signer stamped: the token of its signature's value, its unsigned attribute (as codesign and
    /// productsign stamp theirs).</summary>
    public static void Stamp(SignedCms cms, HashAlgorithmName hash, string url)
    {
        SignerInfo signer = cms.SignerInfos[0];
        byte[] token = Token(signer.GetSignature(), hash, url);
        signer.AddUnsignedAttribute(new System.Security.Cryptography.AsnEncodedData(new Oid(TimeStampTokenOid), token));
    }
}
