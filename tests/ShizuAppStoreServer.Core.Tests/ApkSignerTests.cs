using ShizuAppStoreServer.Core.Enrichment;
using Xunit;

namespace ShizuAppStoreServer.Core.Tests;

/// <summary>Hermetic apksigner tests: verbatim captured output + env-gated live run.</summary>
public sealed class ApkSignerTests
{
    // Captured from `apksigner verify --print-certs` (build-tools 35.0.0)
    // against a locally signed test APK — locks the parsed format.
    private const string RealOutput = """
        Signer #1 certificate DN: CN=SigTest, OU=Test, O=Test, C=DE
        Signer #1 certificate SHA-256 digest: 980ceb20fd248b13eb6e224d73b3dfcd722ab120dfa6632ae8528e7be1cfd6c9
        Signer #1 certificate SHA-1 digest: 086d90932033f759f6be9b3a76db127910822809
        Signer #1 certificate MD5 digest: c7b19fa46b32caa0fc9a49b6c8789253
        """;

    [Fact]
    public void ParsesVerbatimApksignerOutput()
    {
        var signer = Assert.Single(ApkSignerParser.Parse(RealOutput));

        Assert.Equal("980ceb20fd248b13eb6e224d73b3dfcd722ab120dfa6632ae8528e7be1cfd6c9", signer.Sha256);
        Assert.Equal("086d90932033f759f6be9b3a76db127910822809", signer.Sha1);
        Assert.Equal("c7b19fa46b32caa0fc9a49b6c8789253", signer.Md5);
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

        var signers = ApkSignerParser.Parse(output);

        Assert.Equal(["aaaa", "bbbb"], signers.Select(s => s.Sha256));
        Assert.Equal(["1111", "2222"], signers.Select(s => s.Md5));
    }

    [Fact]
    public void ToleratesLegacyOutputWithoutMd5()
    {
        // Older build-tools print SHA-256 + SHA-1 only.
        const string output = "Signer #1 certificate SHA-256 digest: AAAA\n";

        var signer = Assert.Single(ApkSignerParser.Parse(output));

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

        var signers = ApkSignerParser.Parse(await new ApkSignerRunner(binary).PrintCertsAsync(apk));

        Assert.NotEmpty(signers);
        Assert.All(signers, s =>
        {
            Assert.NotNull(s.Sha256);
            Assert.Equal(64, s.Sha256!.Length);
            Assert.Equal(32, s.Md5!.Length);
        });
    }
}
