# Anti-fraud signals (plan step 2.10, PRD §6)

LifeDrop does not claim to be cheat-proof. It uses layers and keeps evidence. Every signal below has a computed definition, a fixed threshold, an action and a human review path.
**Action for every signal:** a *security alert* (see 2.9) that keeps the evidence (`alerts.evidence`). Nothing is blocked, hidden or deleted automatically.
**Review:** a manager with `alerts.handle` (and `alerts.security.read`) marks the alert seen, then handles it with a note **and a result: `valid`, `suspicious` or `fraud`**. The result is kept for later reports. A handled event never makes a new alert.

The scan runs every 5 minutes (and on demand) and looks only at the tracking points of shifts that started in the last 48 hours. One alert per shift and signal (the first point that breaks the rule is the evidence).

| Signal (alert kind) | Definition | Threshold | Evidence kept |
|---|---|---|---|
| Fake GPS (`gps_mocked`, 2.9) | The phone says the position is mocked (check-in/out or tracking point) | any | event / shift, time |
| Impossible speed (`impossible_speed`) | Two following points of a shift (ordered by the phone's capture time) are more than 1 km apart and the speed between them is above 180 km/h | 1 km and 180 km/h | both point ids, times, positions, distance, seconds, speed |
| Phone clock ahead (`clock_tamper`) | A tracking point was captured more than 5 minutes **after** the server received it, or a check-in/out was refused for clock skew. A point that arrives late is normal (the phone keeps points offline), so only a clock in the future counts | 5 minutes | point id, captured and received time, seconds ahead |
| Far from the work area (`outside_area`) | A tracking point is more than 30 km from every located customer of the region of the user's team. The region has no drawn border yet, so its customers stand for the work area. Users without a team region, or regions without a located customer, are not checked | 30 km | point, position, region, distance to the nearest customer |
| Many inaccurate points (`poor_accuracy`) | 5 or more points of a shift have an accuracy worse than 200 m | 5 points, 200 m | count, worst accuracy, first time |
| Another phone in the shift (`device_mismatch`) | A point of a shift was sent by a phone other than the one that started the shift | any | shift device, other device ids, count |
| Impossible travel between visits (`impossible_travel`, 4.8) | Two accepted check-in/out events of one user, from different visits, are more than 1 km apart and the speed between them (previous check-out or check-in to this check-in) is above 180 km/h. Needs no tracking points | 1 km and 180 km/h | both visit ids, times, distance, seconds, speed |
| Stay too short (`short_stay`, 4.8) | Accepted check-out less than 2 minutes after the accepted check-in of the same visit | 2 minutes | check-in and check-out time, seconds |
| Low shift coverage (`low_coverage`, 4.8, operational, no review result needed) | A finished shift of at least 1 hour has fewer than 50 % of the expected tracking points (one per 3 minutes, same rule as the shift summary) | 1 hour, 50 % | points, expected points, coverage, shift times |
| Tracking gap (`tracking_gap`, 2.9) | An open shift with no point for 15 minutes | 15 minutes | shift |
| Staying in one place (`stationary`, 4.33, operational) | In the first N hours of a shift (default 3) the rep stays within 50 m of one point for more than M minutes (default 20); evidence says whether a customer is within 100 m | N and M set per company in the panel (sales manager) | point, from/to time, minutes, place, at-customer flag |
| Shift ended early (`short_shift`, 4.33, operational) | A shift ended before N hours (default 3) | N | start/end time, minutes |
| New device (`new_device`, 2.9) | A phone waits for approval | — | device label |

The thresholds are fixed in code (`AlertRules`); a panel setting may come later. Detection on a tampered phone is never absolute: a signal is a reason to look, not a verdict.
