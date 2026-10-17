# Real-device tracking test (plan step 1.13)

Target (decision D1 as changed by the owner on 2026-10-07): while moving, at least **90 %** of the expected points (one per 3 minutes) arrive and
no gap is longer than **15 minutes**. Battery use is measured on the same run.

## Setup
1. Apply migrations up to `0016` and the grants file on the test database; run the API.
2. Install the Android app on each phone (at least one budget model and one recent model; note make, model,
   Android version). Log in with a field user on an **approved** device.
3. Allow location "while using the app" and notifications. Leave battery optimisation at the phone's default
   (record whether the maker adds its own battery saver).
4. Charge to 100 % and note the time.

## Run (per phone, about 60 minutes)
1. Home → **Start shift**. The "Shift on" notification must appear.
2. Lock the screen and put the phone in a pocket. Move for at least 45 minutes (walk or drive, mixed indoor/outdoor).
3. Unlock, Home → **End shift**. Note the battery percentage.

## Read the result
`GET /shifts/{id}/summary` with the user's token (the shift id is in `GET /shifts/current` while the shift is on,
or in the audit log `shift.started`). Record `pointCount`, `expectedPoints`, `coveragePercent`, `maxGapSeconds`
and `meetsTarget`.

| Phone | Android | Minutes | Coverage % | Max gap (s) | Meets target | Battery used % | Notes |
| --- | --- | --- | --- | --- | --- | --- | --- |
| | | | | | | | |

If a phone fails, note the maker's battery settings; the fix may be guidance for users (exclude the app from
battery optimisation) rather than code. Thresholds can be changed only by the product owner.

## Offline test (plan step 1.14, decision D5)

1. Start a shift with internet, then turn on **airplane mode** (GPS still works).
2. On a visit: Start trip → I have arrived → Check in → report → check-out. Each step must say "saved on the phone"
   and the visit must show "waiting to send" in the plan.
3. **Close the app completely** (swipe it away) and reopen it, still offline: the plan (from the phone's copy) and
   the steps must still be there; nothing is lost.
4. Turn airplane mode off. Within a minute the steps must become "accepted" (or "accepted, a manager will review
   it"), in order, and the tracking points' "on the phone" count must fall to 0.
5. On the server: the visit is `Completed`; `GET /offline-reviews` (manager) shows the step only if it needed review;
   no step or point is stored twice.

## iPhone checklist (plan step 3.8)
Install the build from TestFlight (see `docs/IOS.md`; the workflow uploads with Actions > iOS > Run workflow, upload box ticked). Allow location and notifications when asked. In Settings > GeoTrack set Location to "While Using" or "Always".
1. Sign in, close the app from the app switcher, open it again: still signed in.
2. Start a shift, lock the phone and carry it for 30-60 minutes (or drive). The blue location pill/bar shows tracking is on. Afterwards the shift report should show points about every 3 minutes while moving (iOS sends nothing when the phone stands still).
3. Force-quit the app during the shift, open it again: the shift resumes from the Home page. iOS does not restart the app by itself, so points between the quit and the reopen are missing; this is expected.
4. Open `lifedrop://join/<company code>` from Safari or Notes: the app opens with the company code filled in. Scan a company QR with the in-app camera button.
5. Airplane mode test: same as the offline test above.
6. Push (needs the API running with the Firebase service-account key, `docs/PUSH.md`): sign in as a user who should receive a message, change a customer in that user's area from the panel. The banner should appear with the app open, in the background and closed, in the phone's language.
Send back what failed, with the time it happened.
