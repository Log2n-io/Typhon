namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// The inbound budget a test runtime declares when its catalog has commands: <c>SubscriptionsOptions.IngressBytesPerSecond</c> is required then
/// (design/Subscriptions/11 § 4.2, Q6), and a test that is not about the budget should never meet it.
/// </summary>
static class TestIngress
{
    /// <summary>A mebibyte a second per session: far past anything a test sends.</summary>
    public const int Budget = 1 << 20;
}
