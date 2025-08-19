# DePix Plugin for BTCPay Server

Accept **Pix** payments in your BTCPay Server store and receive funds in **DePix** (a Liquid-based BRL stablecoin). This guide is written for BTCPay store owners and focuses on setup and day‑to‑day use.

---

## What it does

* Adds Liquid Network Stablecoin **DePix** as a payment method to your BTCPay store.
* Adds **Pix** as a payment method to your BTCPay store (via DePix).
* After you save your DePix API key, Pix is **enabled automatically** and appears on newly created invoices (from **Invoices**) and in your **Point of Sale (POS)** apps.
* Provides a **Pix Transactions** page to monitor deposits and statuses.
* Funds settle to your **DePix (Liquid) wallet**.

> Community plugin. Not affiliated with BTCPay Server or DePix. It’s new, so treat it as experimental for now. Feedback is welcome!

---

## Requirements

* BTCPay Server 2.x
* A **DePix partner API key** — request at [https://www.depix.info/#partners](https://www.depix.info/#partners)

---

## Installation

1. Install the plugin in BTCPay (Plugins → Manage Plugins → search for DePix).
2. Restart BTCPay when prompted.

![DePix plugin installed](docs/img/depix-plugin-installed.png)

> When installed, the DePix (Liquid) asset is prepared for your store. You only need to create your DePix wallet, go to Pix Settings and enter your API key to enable Pix payments.

---

## Pix setup (3 quick steps)

1. **Create your DePix wallet**
   Go to Wallets → DePix. You’ll be guided through creating a dedicated wallet. This step is required before you can access the Pix settings.

2. **Enter your API key**
   Go to **Wallets → DePix → Pix Settings** and paste your **DePix API key**. If you don’t have one yet, request it at [https://www.depix.info/#partners](https://www.depix.info/#partners). Click **Save**.


   ![Pix Settings](docs/img/pix-settings.png)


3. **Register the webhook** (optional but recommended)
   After saving the API key, the **Webhook** section appears:

   * Copy the Telegram Command with **Webhook URL** and the **one‑time secret** shown on the page.
   * In the DePix Telegram bot (Eulen), register:

     ```
     /registerwebhook deposit <WEBHOOK_URL> <SECRET>
     ```

   This enables **real‑time updates** for paid invoices.


3. **Done**
   The **Pix** payment method is now **configured automatically** and available on invoices and POS.

---

## Using it

* **Invoices**: create an invoice as usual; customers will see **Pix** as a payment method.
* **POS**: generate charges from your Point of Sale; Pix is available.

  ![Pix Invoice](docs/img/pix-invoice.png)


* **Transactions**: go to **Wallets → Pix** to track Pix deposits (status, ID, amount, time, etc.).
* **DePix Balance**: go to **Wallets → DePix** to track the received DePix converted from successful Pix Transactions

> Tip: if you lose the webhook secret, check **Regenerate secret** on the settings page and save. A new one‑time secret will be shown so you can re‑register it in the bot.

---

## Balance and spending DePix

After a Pix payment, funds settle to your **DePix (Liquid) wallet**.

To **spend/transfer** your DePix:

1. Install the **Liquid+** plugin in BTCPay Server.
2. Click on Liquid at the sidebar under the Store Settings.
3. Use Liquid+ to run the key imports on your Elements/Liquid node:

   * `importprivkey <WIF_PRIVATE_KEY>`
   * `importblindingkey <ADDRESS> <BLINDING_KEY>`

   > Import every address you plan to spend from. If you generated a new address in BTCPay, import its **privkey** and **blinding key**.
4. **Rescan** the chain so the wallet finds past UTXOs belonging to those keys (faster if you know an approximate start height):

   ```bash
   elements-cli rescanblockchain 0
   ```

   You can pass a higher start height to speed things up, e.g. `rescanblockchain 120000`.
5. **Verify your balance**:

   ```bash
   elements-cli getbalances
   ```

   (DePix Asset ID: 02f22f8d9c76ab41661a2729e4752e2c5d1a263012141b86ea98af5472df5189)
6. **Send DePix** using `sendtoaddress` with the asset id that shown in getbalances as the last argument:

   ```bash
   elements-cli sendtoaddress "<DEST_LIQUID_ADDRESS>" <AMOUNT> "" "" false false null null null "<DEPix_ASSET_ID>"
   ```

   This constructs and broadcasts a confidential transaction of the specified **asset** (DePix) to the destination so you can swap for BTC.

**Notes**

* **Fees** are paid in **L-BTC** on Liquid. Keep a small L-BTC balance in the same wallet to cover network fees.

---

## FAQ

**Where do I get the DePix API key?**
At [https://www.depix.info/#partners](https://www.depix.info/#partners)

**Is the webhook mandatory?**
No, but it’s **recommended** so you receive real‑time payment updates.

**Pix doesn’t appear as a payment method. What should I check?**
Make sure you’ve **saved** your API key in the DePix store settings. After saving, Pix is enabled automatically.

---

## Support & feedback

Open an **issue** on the repository with details of your problem or suggestion. Pull requests are welcome.

## License

MIT.
