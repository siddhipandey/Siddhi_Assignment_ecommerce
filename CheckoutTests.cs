using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;

namespace EcommerceTests.Integration
{
    [TestFixture]
    public class CheckoutTests : PageTest
    {
        [Test]
        public async Task TestCheckoutProcessWithDiscount()
        {

            // Navigate to product page
            await Page.GotoAsync("https://staging.example-shop.com/products/laptop-pro");

            // Add to cart
            var addToCartBtn = Page.Locator("#add-to-cart");
            // siddhi_fix: race condition — WaitForAsync on .cart-count only checks visibility.
            // If .cart-count was already visible (showing "0"), it returned immediately before
            // the add-to-cart API call completed. Response listener must be set up BEFORE the
            // click to avoid missing a fast response; then we await it after the click.
            var cartAddedTask = Page.WaitForResponseAsync("**/api/v1/cart/add");
            await addToCartBtn.ClickAsync();
            await cartAddedTask;

            await Page.GotoAsync("https://staging.example-shop.com/cart");

            var priceElement = Page.Locator(".cart-total");
            // siddhi_fix: null-coalesce guards against TextContentAsync returning null (CS8602)
            var priceText = await priceElement.TextContentAsync() ?? string.Empty;
            // siddhi_fix: use InvariantCulture so double.Parse works correctly on CI servers
            // that run with non-English locales (e.g. German/French use ',' as decimal separator).
            var originalPrice = double.Parse(priceText.Replace("$", "").Replace(",", "").Trim(), System.Globalization.CultureInfo.InvariantCulture);

            Console.WriteLine($"Original price: {originalPrice}");

            var checkoutBtn = Page.Locator("#checkout-button");
            await checkoutBtn.ClickAsync();
            await Page.Locator("#first-name").FillAsync("John");
            await Page.Locator("#last-name").FillAsync("Smith");
            await Page.Locator("#email").FillAsync("john.smith@example.com");
            await Page.Locator("#address").FillAsync("123 Main Street");
            await Page.Locator("#city").FillAsync("New York");
            await Page.Locator("#postal-code").FillAsync("10001");

            var discountInput = Page.Locator("#discount-code");
            await discountInput.FillAsync("SAVE20");

            var applyBtn = Page.Locator("#apply-discount");
            await applyBtn.ClickAsync();

            // siddhi_fix: increased timeout from 3000ms to 10000ms — the pricing-service API can take
            // 2847ms+ for validation alone, causing .discount-applied to time out under CI load.
            await Page.Locator(".discount-applied").WaitForAsync(new LocatorWaitForOptions
            {
                Timeout = 10000
            });

            var finalPriceElement = Page.Locator(".final-price");
            // siddhi_fix: null-coalesce guards against TextContentAsync returning null (CS8602)
            var finalPriceText = await finalPriceElement.TextContentAsync() ?? string.Empty;
            // siddhi_fix: InvariantCulture for locale-safe parsing (same reason as originalPrice)
            var finalPrice = double.Parse(finalPriceText.Replace("$", "").Replace(",", "").Trim(), System.Globalization.CultureInfo.InvariantCulture);

            Console.WriteLine($"Final price: {finalPrice}");

            var expectedPrice = originalPrice * 0.8;

            // siddhi_fix: added delta tolerance of 1.0 — backend rounds discounted price to whole
            // dollars (floor or ceiling), so 4999*0.8=3999.2 can come back as 3999.0 or 4000.0.
            Assert.AreEqual(expectedPrice, finalPrice, 1.0,
                $"Expected price {expectedPrice} but got {finalPrice}");

            var discountBadge = Page.Locator(".discount-badge");
            var badgeText = await discountBadge.TextContentAsync();
            Assert.AreEqual("-20%", badgeText,
                $"Expected discount badge '-20%' but got '{badgeText}'");
            await Page.Locator("#payment-method-card").ClickAsync();
            await Page.Locator("#card-number").FillAsync("4111111111111111");
            // siddhi_fix: card expiry was "12/25" (December 2025) which is already expired as of
            // June 2026 — updated to "12/28" so the staging payment validator accepts it.
            await Page.Locator("#card-expiry").FillAsync("12/28");
            await Page.Locator("#card-cvc").FillAsync("123");

            var placeOrderBtn = Page.Locator("#place-order");
            await placeOrderBtn.ClickAsync();

            // siddhi_fix: "**/order-confirmation" only matches URLs with NO query string.
            // The staging app redirects to /order-confirmation?orderId=abc123 after placing an
            // order, so the original pattern never matched and the wait always timed out.
            // Trailing "**" makes the glob match any query parameters that follow.
            await Page.WaitForURLAsync("**/order-confirmation**", new PageWaitForURLOptions
            {
                Timeout = 10000
            });

            var successMsg = Page.Locator(".success-message");
            // siddhi_fix: null-coalesce guards against TextContentAsync returning null;
            // calling .Contains() on a null string throws NullReferenceException (CS8602).
            var successText = await successMsg.TextContentAsync() ?? string.Empty;
            Assert.IsTrue(successText.Contains("Thank you for your order"));
        }
    }
}
