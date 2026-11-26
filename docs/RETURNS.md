# Customer returns (مرجوعی) — step 5.54

**Who:** the field (sales rep, sales manager, supervisor: `returns.create`) writes a return; the **sales manager** (`returns.approve`) approves or rejects it; sales/company/finance managers read all (`returns.read`), a supervisor the own region.

**A return** is for one customer, optionally against one *delivered* order, with one line per product: quantity, **reason** (damaged, expired, wrong item, customer's request, other) and **where the goods go**:
- `warehouse` — back on the shelf of the chosen warehouse (the default for wrong item, customer's request, other);
- `damaged` — damaged or expired goods, counted apart; **never added to the stock that can be sold** (the default for damaged and expired).

**Money:** the credit of a line is the price of the order line (with an order; no more than was delivered, minus what other returns of that order already take) or today's price of the customer (without an order). One currency per return. A return reduces the customer's balance (`Balance = Delivered − Returned − Confirmed payments`) **only when approved**; a requested, rejected or cancelled one changes nothing.

**Approval:** the `warehouse` lines are added to the stock of the chosen warehouse **only where that warehouse already counts the product**; where it does not (the product has no count there) nothing is added and the line says so ("not added to the shelf"), so a product nobody counted does not become limited by a small return. The damaged lines are only recorded (report: *Returns → Damaged and expired goods*, `GET /returns/damaged`).

**Notices:** the sales managers in scope are told of a new return (`return.new`); the author of the decision (`return.decided`). The author can cancel while it is requested.

**Where:** panel tab *Returns* (list, approve/reject, new return with order and warehouse, damaged goods of a period) and the customer's file (section and button); app home *Returns* (my returns, new return, cancel) and the *Return goods* button on a customer.

**Not built (decide if needed):** returns against an order from the app (the app uses today's price; the panel can pick the order), a separate "damaged goods" warehouse, refunding money (the balance only goes down), photos of the damaged goods.
