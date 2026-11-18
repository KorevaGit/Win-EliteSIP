using EliteSIP.PanelLink;
using Xunit;

namespace EliteSIP.PanelLink.Tests;

public sealed class UpdateReceiptTests
{
    [Fact]
    public void Confirmation_roundtrips_with_the_same_operation_id()
    {
        var receipt = new UpdateReceipt(Guid.NewGuid(), new Version(1, 0, 3), new Version(1, 0, 4));
        Assert.Equal(receipt, UpdateReceipt.Parse(receipt.ToString()));
    }

    [Theory]
    [InlineData("30ca9ebe703244b99c5c21ed20dcefea;1.0.3;1.0.3")]
    [InlineData("30ca9ebe703244b99c5c21ed20dcefea;1.0.4;1.0.3")]
    [InlineData("30ca9ebe703244b99c5c21ed20dcefea;1.0;1.0.4")]
    [InlineData("broken;1.0.3;1.0.4")]
    public void Invalid_or_non_upgrade_receipts_are_rejected(string text)
        => Assert.Null(UpdateReceipt.Parse(text));
}
