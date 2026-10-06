# Offline work (plan steps 1.14 and 2.13, PRD §7)

The phone keeps work in two durable lists and sends it when the internet returns (also when the app is opened, a page is visited, or the connection changes). Nothing is "confirmed" until the
server accepts it, and the screens say so ("saved on the phone"). Everything kept has its own **reference** (`clientRef` / idempotency key), so a lost answer and a second send never make two
orders, payments, samples, follow-ups or check-ins.

| Work | Offline | Kept in | Reference | Rule when it is sent |
|---|---|---|---|---|
| Visit steps: on the way, arrived, check-in, check-out, report | yes (1.14, D5) | visit queue (`Outbox`) | idempotency key | In order; later steps of a visit wait for earlier ones; check-in/out go to a manager's review (D5/D2) |
| Visit form (2.7) | yes | work queue | the visit | Waits until the visit's steps are sent (the visit must be in progress) |
| Order | yes | work queue | `clientRef` | **The server prices it when it arrives** (the phone shows no price: "price set later"). A cancelled or failed visit is dropped from the order (it is placed without the visit); an unapproved customer, an unknown or inactive product, another currency or an inactive warehouse **refuse** it |
| Payment | yes | work queue | `clientRef` | The money was taken, so it is recorded; a cancelled / other-customer / other-currency order is dropped from it (recorded on the customer, audit `payment.recorded_detached`, answer `detached`). A wrong amount or a customer outside the area **refuses** it |
| Sample | yes | work queue | `clientRef` | A cancelled or failed visit is dropped from it; an inactive product or an expired date **refuses** it |
| Follow-up: create, start, complete, cancel | yes | work queue | `clientRef`; a step sent again is not a conflict | Start of a started follow-up, or complete / cancel with the same note / reason, answer success without writing again; another status or another text is a conflict and **refuses** it |
| Everything else (catalogue editing, warehouse and delivery work, payments confirmation, reports, alerts, map) | no, online only | — | — | — |

**Refused items** do not stop other work (an order does not depend on a payment). They show on the phone as "needs attention" with the reason; the user sends them again (after the reason is gone,
for example the customer was approved) or drops them. Accepted items stay visible for two days. A phone that is signed out loses its lists.

**What is kept for writing an order offline:** the catalogue (code, names, unit, currency, **no prices**) and the warehouses, refreshed every time the order page loads with internet.

Not offline in this step: photos and signatures (file storage later) and editing a refused item (the user makes a new one and drops the old one).
