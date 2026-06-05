# Debugging Challenge — What Was Wrong and How I Fixed It

This document walks through every bug I found in `CheckoutTests.cs`, why it was causing the test to fail in CI, and exactly what I changed to fix it.

---

## The Starting Point

The CI pipeline was failing on `test_checkout_process_with_discount` roughly 40% of the time. The development team said "the code is fine, it must be the tests." Looking at the CI logs and network logs, the failures fell into two groups: timeouts waiting for a discount confirmation, and wrong price calculations. But digging deeper revealed several more issues hiding underneath.

---

## Bug 1 — Discount wait timeout was far too short

**What was wrong:**
The test clicked "Apply Discount" and then waited only 3 seconds for a `.discount-applied` element to appear on the page. Looking at the network logs, just validating the discount code with the backend took 2847ms — almost the entire budget. When the pricing service was under load (which it frequently was, shown by a 503 health check and queue depth of 234), the combined wait exceeded 3 seconds and the test gave up too early.

**What I changed:**
Raised the timeout from `3000ms` to `10000ms`. This gives the two backend calls enough breathing room even when the pricing service is running slow.

---

## Bug 2 — Price comparison was too strict

**What was wrong:**
The expected price was `4999.0 × 0.8 = 3999.2`. The test compared this to the final price returned from the backend using exact equality. But the CI logs showed the backend was returning `3999.0` in some runs and `4000.0` in others — it rounds the discounted price to a whole dollar, sometimes rounding down and sometimes up. An exact comparison will always fail against a rounded value.

**What I changed:**
Added a delta tolerance of `1.0` to the `Assert.AreEqual` call. This means the test accepts any final price within $1.00 of the expected price, which covers both rounding outcomes from the backend.

---

## Bug 3 — The credit card was already expired

**What was wrong:**
The test was filling in `12/25` as the card expiry date — December 2025. The current date is June 2026. Any staging environment with real payment validation rejects expired cards. This means the payment step would fail 100% of the time on a properly configured staging environment.

**What I changed:**
Updated the expiry to `12/28` (December 2028), which is well in the future.

---

## Bug 4, 5, 6 — Price and success text could crash with a null reference

**What was wrong:**
In Playwright (C#), `TextContentAsync()` returns `string?` — it can return null if the element has no text content. The code was calling `.Replace()` directly on the cart price text, `.Replace()` on the final price text, and `.Contains()` on the success message text — all without checking for null first. In C#, calling any method on a null string throws a `NullReferenceException`. The compiler was already flagging these as warnings (CS8602), but they were being ignored.

**What I changed:**
Added `?? string.Empty` after each `TextContentAsync()` call. If the element returns null, it falls back to an empty string instead of crashing. The three places fixed were: reading the cart total price, reading the final discounted price, and reading the order success message.

---

## Bug 7 — Price parsing would break on non-English CI servers

**What was wrong:**
`double.Parse("4999.0")` uses the culture of the machine running the code. On CI servers configured with a German or French locale, the decimal separator is a comma, not a period. So `double.Parse("4999.0")` would throw a `FormatException` because that machine does not recognise `.` as a decimal point. The test would fail immediately at the price reading step with no clear error message about why.

**What I changed:**
Added `System.Globalization.CultureInfo.InvariantCulture` to both `double.Parse` calls. Invariant culture always uses `.` as the decimal separator regardless of the server's locale setting.

---

## Bug 8 — Cart update check had a race condition

**What was wrong:**
After clicking "Add to Cart", the test called `WaitForAsync()` on the `.cart-count` element. This only waits for the element to be *visible on the page*. If the cart count badge was already showing on the page (displaying "0 items"), this wait completed immediately — before the add-to-cart API call had even returned. The test would then navigate to the cart page and read the price before the item was actually registered, potentially finding an empty cart or the wrong total.

The listener also needs to be registered *before* the click, not after — otherwise a fast API response can come back before the listener is even set up, and it gets missed entirely.

**What I changed:**
Replaced the `.cart-count` visibility wait with `WaitForResponseAsync("**/api/v1/cart/add")`. This listens for the actual network response from the add-to-cart endpoint. The listener is set up before the button click, then awaited after — so there is no window where a fast response could slip through unnoticed.

---

## Bug 9 — Order confirmation URL pattern didn't match real URLs

**What was wrong:**
After placing an order, the test waited for the URL to match the pattern `"**/order-confirmation"`. This glob only matches URLs that end *exactly* with `/order-confirmation`. In practice, order confirmation pages almost always include a query string with the order ID — something like `/order-confirmation?orderId=abc123&total=3999`. The original pattern would never match this URL, so the test would sit and wait until the 10-second timeout expired, then fail — even when the order was successfully placed.

**What I changed:**
Changed the pattern to `"**/order-confirmation**"`. The trailing `**` means "followed by anything", which covers query strings, hash fragments, or any other URL suffix after the path.

---
