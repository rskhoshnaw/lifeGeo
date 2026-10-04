# Real-device tracking test (plan step 1.13)

Target (decision D1, 2026-10-04): while moving, at least **90 %** of the expected points (one per 30 s) arrive and
no gap is longer than **5 minutes**. Battery use is measured on the same run.

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
