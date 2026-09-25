using ShizuAppStoreServer.Core.Enrichment;
using Xunit;

namespace ShizuAppStoreServer.Core.Tests;

/// <summary>Hermetic apksigner tests: verbatim captured output + env-gated live run.</summary>
public sealed class ApkSignerTests
{
    // Captured from `apksigner verify --print-certs` (build-tools 35.0.0)
    // against a locally signed test APK; locks the parsed format.
    private const string RealOutput = """
        Verified using v1 scheme (JAR signing): false
        Verified using v2 scheme (APK Signature Scheme v2): true
        Verified using v3 scheme (APK Signature Scheme v3): true
        Verified using v4 scheme (APK Signature Scheme v4): false
        Signer #1 certificate DN: CN=SigTest, OU=Test, O=Test, C=DE
        Signer #1 certificate SHA-256 digest: 980ceb20fd248b13eb6e224d73b3dfcd722ab120dfa6632ae8528e7be1cfd6c9
        Signer #1 certificate SHA-1 digest: 086d90932033f759f6be9b3a76db127910822809
        Signer #1 certificate MD5 digest: c7b19fa46b32caa0fc9a49b6c8789253
        Signer #1 key algorithm: RSA
        Signer #1 key size (bits): 2048
        Signer #1 public key SHA-256 digest: ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff
        """;

    [Fact]
    public void ParsesVerbatimApksignerOutput()
    {
        var info = ApkSignerParser.Parse(RealOutput);
        var signer = Assert.Single(info.Signers);

        Assert.Equal("980ceb20fd248b13eb6e224d73b3dfcd722ab120dfa6632ae8528e7be1cfd6c9", signer.Sha256);
        Assert.Equal("086d90932033f759f6be9b3a76db127910822809", signer.Sha1);
        Assert.Equal("c7b19fa46b32caa0fc9a49b6c8789253", signer.Md5);
        Assert.Equal("CN=SigTest, OU=Test, O=Test, C=DE", signer.Dn);
        Assert.Equal("RSA", signer.KeyAlgorithm);
        Assert.Equal(2048, signer.KeySizeBits);
    }

    [Fact]
    public void CollectsVerifiedSchemesOnly()
    {
        var info = ApkSignerParser.Parse(RealOutput);

        Assert.Equal(["v2", "v3"], info.Schemes);
        Assert.Equal("v2+v3", info.Scheme);
        Assert.Equal("CN=SigTest, OU=Test, O=Test, C=DE", info.Dn);
        Assert.Equal("RSA 2048", info.KeyAlgorithm);
    }

    [Fact]
    public void NoVerifiedSchemeYieldsNullScheme()
    {
        var info = ApkSignerParser.Parse("Signer #1 certificate SHA-256 digest: AAAA\n");

        Assert.Empty(info.Schemes);
        Assert.Null(info.Scheme);
        Assert.Null(info.Dn);
        Assert.Null(info.KeyAlgorithm);
    }

    [Fact]
    public void CollectsRotatedSignersInOrder()
    {
        const string output = """
            Signer #1 certificate SHA-256 digest: AAAA
            Signer #1 certificate MD5 digest: 1111
            Signer #2 certificate SHA-256 digest: BBBB
            Signer #2 certificate MD5 digest: 2222
            """;

        var info = ApkSignerParser.Parse(output);

        Assert.Equal(["aaaa", "bbbb"], info.Signers.Select(s => s.Sha256));
        Assert.Equal(["1111", "2222"], info.Signers.Select(s => s.Md5));
    }

    [Fact]
    public void RotationKeepsEveryDistinctDn()
    {
        const string output = """
            Signer #1 certificate DN: CN=Old
            Signer #1 certificate SHA-256 digest: AAAA
            Signer #2 certificate DN: CN=New
            Signer #2 certificate SHA-256 digest: BBBB
            """;

        var info = ApkSignerParser.Parse(output);

        Assert.Equal("CN=Old\nCN=New", info.Dn);
    }

    [Fact]
    public void ToleratesLegacyOutputWithoutMd5()
    {
        // Older build-tools print SHA-256 + SHA-1 only.
        const string output = "Signer #1 certificate SHA-256 digest: AAAA\n";

        var signer = Assert.Single(ApkSignerParser.Parse(output).Signers);

        Assert.Equal("aaaa", signer.Sha256);
        Assert.Null(signer.Md5);
    }

    [Fact]
    public void RejectsOutputWithoutSigners() =>
        Assert.Throws<ApkSignerParseException>(() => ApkSignerParser.Parse("DOES NOT VERIFY\n"));

    [Fact]
    public async Task MissingBinaryThrowsApkSignerException()
    {
        var runner = new ApkSignerRunner("/nonexistent/apksigner-shizu-test");

        await Assert.ThrowsAsync<ApkSignerException>(() => runner.PrintCertsAsync("whatever.apk"));
    }

    [Fact]
    public async Task LiveRunAgainstRealSignedApk()
    {
        // Needs a JRE + build-tools apksigner and a signed APK, e.g.:
        //   SHIZU_REAL_APKSIGNER=/home/tim/Android/Sdk/build-tools/35.0.0/apksigner \
        //   SHIZU_REAL_SIGNED_APK=/tmp/opencode/sigtest/signed.apk
        var binary = Environment.GetEnvironmentVariable("SHIZU_REAL_APKSIGNER");
        var apk = Environment.GetEnvironmentVariable("SHIZU_REAL_SIGNED_APK");
        if (string.IsNullOrEmpty(binary) || string.IsNullOrEmpty(apk) || !File.Exists(apk))
        {
            return; // Not a failure: env-gated by design (RealAapt2 precedent).
        }

        var info = ApkSignerParser.Parse(await new ApkSignerRunner(binary).PrintCertsAsync(apk));

        Assert.NotEmpty(info.Signers);
        Assert.All(info.Signers, s =>
        {
            Assert.NotNull(s.Sha256);
            Assert.Equal(64, s.Sha256!.Length);
            Assert.Equal(32, s.Md5!.Length);
        });
    }
}
