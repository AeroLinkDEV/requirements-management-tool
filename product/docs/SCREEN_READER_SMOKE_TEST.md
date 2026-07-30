# Screen-reader smoke test

A person has to run this one. Everything else about form semantics is measured automatically by
`product/client/tests/form-semantics.spec.ts` — accessible names, label association, required state, group
semantics, focus order, and an axe pass over each form. What no automated check can tell you is whether the
result is *usable*: whether the announcements arrive in an order that lets somebody complete a controlled
change without sighted help.

Run it on Windows with Narrator, which ships with the operating system, so no additional software is needed.
NVDA is a reasonable substitute if it is already installed; note which one you used.

## Before you start

- Start the product with `START_AEROLINK.bat` and sign in.
- Narrator: **Ctrl + Windows + Enter** toggles it. **Caps Lock + Esc** also exits.
- Keep one hand off the mouse. If you reach for it, that is a finding — write down where.

## The run: one controlled change, start to finish

The point is a whole task, not a tour of controls. A form whose fields all announce correctly can still be
impossible to finish.

1. **Reach the authoring form by keyboard alone.** From the Command Center, Tab to the navigation, open
   Systems Engineering, and open System Change Requests, then New System Change Request.
   - *Expect:* each navigation group announces its expanded or collapsed state, and the link announces where it
     goes rather than only its icon.
2. **Fill the change case.** Tab through Title, Problem, Analysis and Solution.
   - *Expect:* each field announces its own name, and the help text beneath it is read as a description rather
     than skipped or announced as a separate unlabelled item.
   - *Expect:* a required field says so when focused, not only when submission fails.
3. **Add a requirement proposal.** Activate "Introduce System requirement".
   - *Expect:* the new proposal card is announced when it appears. A control that adds content silently leaves a
     screen-reader user unsure whether the button worked.
   - *Expect:* the Change type control announces its current value and its options.
4. **Submit and read the outcome.** Save the draft.
   - *Expect:* success or failure is announced without hunting for it. The message is in a live region, so it
     should arrive on its own.
5. **Open the record you just created** from the change-request list, by keyboard.
   - *Expect:* the identifier and state are announced together closely enough to be understood as one record.
6. **Read the audit history.**
   - *Expect:* each entry announces the person, the action and the time — a person's name, not their account
     handle, which `people-not-accounts.spec.ts` enforces automatically.

## What counts as a finding

Write down anything in these categories, with the step number:

- A control that announces nothing, or announces only a symbol.
- Two controls that announce identically, so they cannot be told apart.
- Help or error text that is never read, or is read out of order relative to the field it belongs to.
- A state change — content added, saved, refused — that is silent.
- Focus that jumps somewhere unexpected, or is lost entirely after an action.
- Anywhere you could not proceed without looking at the screen. This one is the whole reason for the exercise.

## Recording the result

Add a dated entry to this file, below. Keep it short and specific: what you used, what you found, and what you
could not complete. A run that found nothing is still worth recording, because the value of this procedure is
the trend across runs.

Do not translate a finding here into a claim about conformance. This is a usability smoke test on one workflow,
and it is not an accessibility audit, a conformance statement, or evidence of certification of any kind.

## Runs

*(no runs recorded yet — this procedure was written alongside the automated form-semantics coverage)*
