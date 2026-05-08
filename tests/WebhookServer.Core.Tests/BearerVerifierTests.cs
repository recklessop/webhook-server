using WebhookServer.Core.Auth;
using Xunit;

namespace WebhookServer.Core.Tests;

public class BearerVerifierTests
{
    [Fact]
    public void Accepts_correct_token() =>
        Assert.True(BearerVerifier.Verify("Bearer s3cret", "s3cret").Success);

    [Fact]
    public void Rejects_wrong_token() =>
        Assert.False(BearerVerifier.Verify("Bearer nope", "s3cret").Success);

    [Fact]
    public void Rejects_missing_header() =>
        Assert.False(BearerVerifier.Verify(null, "s3cret").Success);

    [Fact]
    public void Rejects_non_bearer_scheme() =>
        Assert.False(BearerVerifier.Verify("Basic s3cret", "s3cret").Success);

    [Fact]
    public void Rejects_when_server_secret_empty() =>
        Assert.False(BearerVerifier.Verify("Bearer s3cret", "").Success);
}
