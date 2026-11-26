# Customer returns (مرجوعی) — step 5.54

**Who:** the field (sales rep, sales manager, supervisor: `returns.create`) writes a return; the **sales manager** (`returns.approve`) approves or rejects it; sales/company/finance managers read all (`returns.read`), a supervisor the own region.

**A return** is for one customer, optionally against one *delivered* order, with one line per product: quantity, **reason** (damaged, expired, wrong item, customer's request, other) and **where the goods go**:
- `warehouse` — back on the shelf of the chosen warehouse (the default for wrong item, customer's request, other);
- `damaged` — damaged or expired goods, counted apart; **never added to the stock that can be sold** (the default for damaged and expired).

**Money:** the credit of a line is the price of the order line (with an order; no more than was delivered, minus what other returns of that order already take) or today's price of the customer (without an order). One currency per return. A return reduces the customer's balance (`Balance = Delivered − Returned − Confirmed payments`) **only when approved**; a requested, rejected or cancelled one changes nothing.

**Approval:** the `warehouse` lines are added to the stock of the chosen warehouse **only where that warehouse already counts the product**; where it does not (the product has no count there) nothing is added and the line says so ("not added to the shelf"), so a product nobody counted does not become limited by a small return. The damaged lines are only recorded (report: *Returns → Damaged and expired goods*, `GET /returns/damaged`).

**Notices:** the sales managers in scope are told of a new return (`return.new`); the author of the decision (`return.decided`). The author can cancel while it is requested.

**Where:** panel tab *Returns* (list, approve/reject, new return with order and warehouse, damaged goods of a period) and the customer's file (section and button); app home *Returns* (my returns, new return, cancel) and the *Return goods* button on a customer.

**Against an order from the app (step 5.55):** *Return goods* offers the customer's delivered orders; with one chosen the products are its lines (with what was delivered) and the credit uses its prices; without one, today's price of the customer is used.

**Pictures (step 5.55):** up to three photos of the goods (made smaller on the phone first), added by the author right after sending, while the return waits (`POST /returns/{id}/photos`, raw JPEG/PNG/WEBP, at most 3 MB). The manager sees them under the return (panel, customer file) before deciding.

**Refunds — money back to the customer (step 5.55):** when approved returns and payments are more than was delivered, the customer is owed money. The **finance manager** records it in *Payments* (tick *Refund*), or from the customer's file: a confirmed payment with direction *out*, never against an order, at most the customer's credit (`REFUND_TOO_MUCH` otherwise; the credit is approved returns + confirmed payments − refunds − delivered, per currency). It is voided like any payment, never corrected. The balance counts it: `Balance = Delivered − Returned − Confirmed + Refunded`.

**Not built (decide if needed):** a separate "damaged goods" warehouse (they are recorded and reported, not stocked), partial approval of a return line (approve all or reject all).

## Step 5.56
- Damaged / expired goods are kept **in the warehouse of the return** (`warehouse_damaged_stock`), apart from the sellable quantity; every return names a warehouse.
- Approve everything, or choose lines (`lines` in the approve body); rejected lines count nowhere (total, quotas, damaged report).
- Expired goods in stock are moved to the damaged bucket with `write-off`; after throwing them away the figure is set with `PUT .../damaged`.
- A picture is optional; the app never shows an error for one that cannot be sent.
