using Bff.Proxy.Csrf;

namespace BFF.Proxy.Tests.Unit;

[Category("Unit")]
public class CsrfTokenTests
{
    [Test]
    public void Identical_tokens_match() =>
        Assert.That(CsrfConstants.TokensMatch("abc123+/=", "abc123+/="), Is.True);

    [TestCase("abc123", "abc124", TestName = "Same length, different value")]
    [TestCase("abc123", "abc1234", TestName = "Prefix of the other")]
    [TestCase("abc123", "", TestName = "Empty")]
    [TestCase("ABC123", "abc123", TestName = "Case differs")]
    [TestCase("é", "é", TestName = "Unicode normalisation is not applied")]
    public void Different_tokens_do_not_match(string a, string b) =>
        Assert.That(CsrfConstants.TokensMatch(a, b), Is.False);
}
