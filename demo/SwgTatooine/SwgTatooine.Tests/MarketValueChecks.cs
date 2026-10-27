using NUnit.Framework;
using SwgTatooine.Server;

namespace SwgTatooine.Tests;

/// <summary>
/// The market values (bazaar.md § 2.3) pinned against <c>balance_model.py</c>: <c>mat_value(b)</c> and <c>core_value(b)</c> as the model prints them, the
/// material to 100 milli-credits and the core to the credit; an add-on is a core's [EST]; everything else has none. And the vendor's floor (B7, C3).
/// </summary>
[TestFixture]
internal sealed class MarketValueChecks
{
    // balance_model.py, run 2026-10-27: mat_value(1..5) and core_value(1..5), credits.
    private static readonly double[] ModelMaterial = [0, 1.746673558820081, 2.794430923383652, 4.470694679112143, 7.15247986507397, 11.442957278945357];
    private static readonly double[] ModelCore = [0, 725.7587808359945, 1549.2626156631964, 3098.972218872761, 5949.953966203947, 11105.787173524714];

    [Test]
    public void TheValues_AreTheModels_Rounded()
    {
        for (byte mk = 1; mk <= 5; mk++)
        {
            Assert.That(MarketValues.MilliOf(ItemCatalog.Hide, mk), Is.EqualTo(ModelMaterial[mk] * 1000).Within(50), $"material Mk {mk}");
            Assert.That(MarketValues.MilliOf(ItemCatalog.Hide, mk) % 100, Is.Zero, $"material Mk {mk} is not rounded to 100 milli-credits");
            Assert.That(MarketValues.MilliOf(ItemCatalog.CoreArmor, mk), Is.EqualTo(ModelCore[mk] * 1000).Within(500), $"core Mk {mk}");
            Assert.That(MarketValues.MilliOf(ItemCatalog.CoreArmor, mk) % 1000, Is.Zero, $"core Mk {mk} is not rounded to the credit");
            Assert.That(MarketValues.MilliOf(ItemCatalog.AddonWeapon, mk), Is.EqualTo(MarketValues.MilliOf(ItemCatalog.CoreRanged, mk)), "an add-on is a core's [EST]");
        }

        Assert.Multiple(() =>
        {
            Assert.That(MarketValues.MilliOf(20, 0), Is.Zero, "a part has no value yet");
            Assert.That(MarketValues.MilliOf(66, 0), Is.Zero, "the crate has no value");
            Assert.That(MarketValues.MilliOf(ItemCatalog.Hide, 0), Is.Zero, "a Mk the template refuses");
            Assert.That(MarketValues.MilliOf(0, 1), Is.Zero, "no template");
            Assert.That(MarketValues.CreditsOf(ItemCatalog.CoreArmor, 2), Is.EqualTo(1_549), "bazaar.md § 9's example value");
        });
    }

    [Test]
    public void TheVendor_PaysAFifth_RoundedDown()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MarketValues.VendorPayout(ItemCatalog.Hide, 2, 30), Is.EqualTo(16), "⌊30 × 2.8 × 20 %⌋");
            Assert.That(MarketValues.VendorPayout(ItemCatalog.Hide, 1, 2), Is.Zero, "⌊2 × 1.7 × 20 %⌋ = 0: refused worthless");
            Assert.That(MarketValues.VendorPayout(ItemCatalog.Hide, 1, 3), Is.EqualTo(1));
            Assert.That(MarketValues.VendorPayout(ItemCatalog.CoreArmor, 3, 1), Is.EqualTo(619), "⌊3 099 × 20 %⌋");
            Assert.That(MarketValues.VendorPayout(20, 0, 99), Is.Zero);
            Assert.That(TatooineData.ListFee(693), Is.EqualTo(6));
            Assert.That(TatooineData.ListFee(1), Is.EqualTo(1), "the list fee's floor");
            Assert.That(TatooineData.SalesTax(600), Is.EqualTo(48));
            Assert.That(TatooineData.SalesTax(12), Is.Zero, "under 13 credits the tax rounds to 0");
        });
    }
}
