---
name: experimental-patch
description: 'Apply experimental patches to a game exe profile and present expected outcomes for user testing. Use when trying to fix rendering issues (UI stretch, black bars, wrong aspect ratio) by writing candidate patch sites to the profile JSON and explaining what each result means. Do NOT over-research — write the smallest plausible patch, commit it, then wait for test feedback before iterating.'
argument-hint: 'Describe the problem to fix and any known constraints'
---

# Experimental Patch Workflow

A test-driven patching loop for compiled game executables. Write the simplest plausible patch, explain the expected outcomes, then wait for the user to test before doing more research.

## Core Principle

**Write → Commit → Enumerate outcomes → Wait → Interpret → Repeat.**

Never chain multiple untested hypotheses. Each round is one patch and one test result.

---

## Step 1 — Write the Smallest Plausible Patch

- Use prior knowledge (e.g. scanning results, known struct offsets) to identify the most likely candidate sites.
- Prefer one focused patch block over a large speculative sweep.
- Add it as a new named entry in the profile JSON so the user can toggle it independently from working patches.
- Build and confirm zero errors before handing off.

---

## Step 2 — Present Expected Outcomes

After committing the patch, list every plausible result the user might observe, and what each one means for the next step. Use this format:

> **How to test**: [exact steps — which patches to apply, what to look for in-game]
>
> | Result | Interpretation | Next step |
> |--------|---------------|-----------|
> | [desired outcome] | Hypothesis correct | Done / refine to fewer sites |
> | [overcorrection] | Patch too aggressive | Bisect or restrict to subset |
> | [no change] | Wrong mechanism | Try next candidate approach |
> | [reverts to baseline] | Branch polarity wrong | Invert: NOP jz instead of jne, etc. |
> | [crash / corruption] | Load-bearing branch hit | Bisect the site list |

Always include the "no change" and "reverts to baseline" rows — these are the most common outcomes and both carry useful signal.

---

## Step 3 — Wait for Test Feedback

Stop. Do not speculate about the result or pre-emptively write follow-up patches. The user tests and reports back.

---

## Step 4 — Interpret and Iterate

Map the reported result to the outcome table from Step 2 and take the prescribed next step:

- **Desired outcome**: Optionally clean up — remove the experimental label, tighten the site list.
- **Overcorrection / crash**: Bisect — split the site list in half, keep the half that doesn't cause the problem.
- **No change**: Discard this approach. Move to the next candidate (e.g. different struct offset, different mechanism entirely).
- **Reverts to baseline**: Invert polarity — if you NOPped `jne`, try NOPping `jz` instead, or change to unconditional `jmp`.

---

## Common Polarity Mistakes

When patching `test byte [reg+offset], 1 + branch`:

| Branch opcode | Semantics | NOP effect | Use when |
|---------------|-----------|-----------|----------|
| `jne` / `jnz` (75/0F85) | "skip constrained path if flag≠0" | always fall through = unconstrained | flag=1 means "constrained", NOP forces ultrawide |
| `jz` / `je` (74/0F84) | "skip constrained path if flag=0" | always fall through = constrained | **don't NOP** — forces 16:9 |

Mixed polarity in a site list cancels out. When in doubt, only patch one polarity per round.

---

## Site List Management

- Keep experimental patches as a **separate named block** from production patches so they can be toggled independently.
- Remove failed blocks promptly — don't accumulate dead patches in the profile.
- When bisecting, split sites list in half rather than removing one at a time.

---

## Step 5 — Update the Profile README

After each test round — whether it confirms, refutes, or partially succeeds — update `profiles/assets/<steamAppId>/README.md` with what was learned. Create it if it doesn't exist yet.

The README has two sections that must be kept current:

### Confirmed Knowledge

Facts established by successful patches or conclusive test results. Use a table:

```markdown
## Confirmed

| Mechanism | Detail | Status |
|-----------|--------|--------|
| Viewport pillarbox float | rdata foff=0xXXXX controls D3D viewport width for all renderers | ✅ Confirmed |
| Camera struct +0x408/+0x428 | AR fields initialized at object creation; patching = ultrawide camera | ✅ Confirmed |
| test byte [reg+0x94],1 + jne | In KH3 these are ENABLE branches for the AR constraint, not guards; NOPping removes the constraint entirely (forces 16:9 native) | ✅ Confirmed |
```

### Current Speculation

Working hypotheses that have not yet been tested, or are partially supported. Use a table with confidence levels:

```markdown
## Speculation

| Hypothesis | Confidence | Basis | Next test |
|------------|-----------|-------|-----------|
| Site 6 (viewport float) alone causes UI stretch | Medium | Site 6 is read by UI callers per scan | Apply only site 6, check UI |
| Camera struct writes alone cause UI stretch | Medium | Struct +0x408/+0x428 read by both 3D and UI systems | Apply only struct sites, check UI |
```

Keep speculation entries short. Remove them once confirmed or refuted and move to the Confirmed table.

### PE Layout (if not already present)

Always ensure the README contains the PE section layout for the exe — it's needed to re-derive file offsets from VAs when revisiting the game later.

```markdown
## PE Layout — GAME NAME.exe (X bytes)

| Field | Value |
|-------|-------|
| IMAGE_BASE | `0x140000000` |
| `.text` RAW | `0xXXX` size `0xXXX` |
| `.rdata` RAW | `0xXXX` |
```
