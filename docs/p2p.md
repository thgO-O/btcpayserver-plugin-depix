# P2P Mode

P2P mode is for stores that sell DePix. It is separate from normal merchant checkout flows where the store sells products or services.

## Setup

1. Go to **Wallets -> Pix -> Settings**.
2. Enable **P2P mode**.
3. Set **P2P commission (%)**.
4. Save the settings.

The plugin creates or repairs a separate POS app named **DePix P2P** with a checkout form named **DePix P2P checkout**.

## Required Fields

P2P invoices must identify both the DePix destination and the Pix payer:

```json
{
  "depixAddress": "lq1...",
  "endUserTaxNumber": "01234567890"
}
```

Fields:

* `depixAddress`: buyer's DePix address.
* `endUserTaxNumber`: Pix payer CPF/CNPJ as a string.

Do not send `endUserTaxNumber` as a JSON number because valid CPF/CNPJ values can start with `0`.

## Existing POS Apps

P2P mode does not modify regular POS apps. The plugin-owned **DePix P2P** POS is separate so stores can keep normal product sales and DePix sales apart.

Existing normal POS apps can keep using Pix only if their invoice metadata or form response includes `endUserTaxNumber`.
