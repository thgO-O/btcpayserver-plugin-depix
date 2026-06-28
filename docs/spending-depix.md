# Spending DePix

After a Pix payment, funds settle to your **DePix (Liquid) wallet**.

## Choose The Right Flow

* **SamRock + Aqua xpub**: funds go to your Aqua wallet. Spend them normally from Aqua. You do not need Liquid+ or `elements-cli`.
* **BTCPay Hot Wallet**: spend through your Elements/Liquid node using Liquid+ and `elements-cli`.

## Spend From A BTCPay Hot Wallet

1. Install the **Liquid+** plugin in BTCPay Server.
2. Click **Liquid** in the store sidebar.
3. Use Liquid+ to run the key imports on your Elements/Liquid node:

   ```bash
   importprivkey <WIF_PRIVATE_KEY>
   importblindingkey <ADDRESS> <BLINDING_KEY>
   ```

   Import every address you plan to spend from. If you generated a new address in BTCPay, import its private key and blinding key.

4. Rescan the chain so the wallet finds past UTXOs belonging to those keys:

   ```bash
   elements-cli rescanblockchain 0
   ```

   You can pass a higher start height to speed this up, for example `rescanblockchain 120000`.

5. Verify your balance:

   ```bash
   elements-cli getbalances
   ```

   DePix Asset ID:

   ```text
   02f22f8d9c76ab41661a2729e4752e2c5d1a263012141b86ea98af5472df5189
   ```

6. Send DePix using `sendtoaddress` with the DePix asset ID as the last argument:

   ```bash
   elements-cli sendtoaddress "<DEST_LIQUID_ADDRESS>" <AMOUNT> "" "" false false null null null "<DEPIX_ASSET_ID>"
   ```

This constructs and broadcasts a confidential transaction for the DePix asset to the destination address.

## Fees

Liquid fees are paid in L-BTC. Keep a small L-BTC balance in the same wallet to cover network fees.
