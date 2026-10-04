# LifeDrop — instructions for Claude

## Start of every session (keep it cheap)
1. Read `memory-bank/phaseStatus.md`, then `memory-bank/activeContext.md` (rules, verification routine), then
   `memory-bank/codeMap.md` to find files. Read other memory-bank files or `memory-bank/archive/` only when needed.
2. Do not rescan the repository. Use `grep`/`sed -n` on the specific files from the code map; never read a large
   file in full (for example `CustomerApiIntegrationTests.cs`, `Program.cs`, `openapi-v1.json`) — grep for the part you need.
3. Read only the plan row of the current step in `docs/IMPLEMENTATION-PLAN.md` (`grep '^| 1.17 '`), not the whole plan.
4. Check the previous PR's CI once (merged? green?), then work.

## Working rules
- Reply to the owner in Persian, short. Times in Iraq local time (Asia/Baghdad).
- Branch `claude/jolly-bardeen-5q93e2`; after a merge reset it to `origin/main`. Open the PR yourself; the owner merges.
- Do not subscribe to PR events or schedule check-ins (saves credit); the owner reports a red CI.
- One PR per plan step, with tests, a short mutation check of the new rule, and in the same PR: the plan row,
  `docs/phase0-api-access-matrix.md` (API changes) and one log line in `memory-bank/phaseStatus.md`.
- Commit trailer and other conventions: see `memory-bank/activeContext.md`.

## Token budget habits
- Batch related edits in one script/command; avoid re-reading files you just wrote.
- Run the full test suites once at the end (DB tests twice only for DB changes), not after every edit.
- Keep PR bodies and replies concise; no long recaps.
