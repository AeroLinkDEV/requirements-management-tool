# Product Review — 2026-07-26

Findings from an evening of using AeroLink as a systems engineer would, captured in
`July 26th observations.docx` and triaged here. Twenty observations, kept in the reviewer's own framing
because the framing is part of the finding: several are not defects in behaviour but defects in what the
product appears to say.

This file records the disposition of each. It is a working record, not a product definition.

> **Closed as of 2026-07-28.** Every item below has been decided and built. The nine that needed a product
> decision were answered on 27 July and merged through PR #98; the last of them, *What the impact-disposition
> field is for*, is closed by [DEC-059](DECISIONS_AND_OPEN_QUESTIONS.md) — the computed trace impact now appears
> beside the declared disposition in the proposal card, read-only, exactly as this file argued it should.
>
> Retained as the record of what was found and what was decided. Nothing here is outstanding work.
>
> **Later decision:** DEC-071 supersedes DEC-059's author-owned disposition model. The computed trace remains
> visible as read-only context, but consuming engineers—not the change author—now decide downstream impact.
> Do not use the historical conclusions below to restore the removed author-impact selectors.

## What was wrong, and is now fixed

### Spaces could not be typed into the change case

`imtestingthesite`. The Problem, Analysis and Solution fields discarded every space.

`RichCaseField` is a controlled textarea whose value round-tripped through the content model on **every
keystroke** — `toPlainText` out, `fromPlainText` in — and `fromPlainText` trimmed. So the trailing space was
removed and written back into the field before the next letter arrived, and a space could never become a
separator. Fixed by making the round trip lossless: `fromPlainText` stores exactly what was typed, and a new
`toEditableText` reads it back verbatim. `toPlainText` still tidies, because summaries and the plain mirror
fields want that; an editor and a summary want opposite things and had been sharing one function.

This is the most serious item in the document. The product could not be used to write a sentence, and every
existing journey passed, because they all `fill()` a value in one go rather than typing it.

### The change request froze, with no error

"Complete Draft readiness" and "Check out & edit" both stopped responding.

Two faults, one behind the other.

`main.tsx` patches `window.fetch` to attach a CSRF token, caching it as `csrfToken ??= loadCsrf(url)` — the
**promise**, not the token. One failed fetch therefore became a rejected promise that `??=` would never
replace, because it is neither null nor undefined. Every later mutation in that tab awaited the same stale
rejection and threw before reaching the network, permanently, until the page was reloaded. The existing reset
could not help: it keys off a 400 response, and no request was ever sent to be answered.

Then `ScrWorkspace` had no `try/finally` around any of its handlers. `busy` drives both the disabled state
and the "Checking lock…" label, so a throw left the change request looking frozen — no error, no spinner
finishing, every button dead. `save` additionally leaked `savingRef`, after which every autosave waited on a
check-in that had already failed.

Fixed at both layers: the token cache no longer keeps a failure, and a `withBusy` helper always gives the
toolbar back and reports what went wrong.

### The System explorer listed HLRs and LLRs

1,250 requirements with `HLR-000001.00` at the top, on a page headed *System Requirements Explorer*.

The server filter was correct all along — `level=System` returns exactly 150. The client had stopped sending
it. Two paths set `level` to empty: applying a saved view that carried none, and selecting a specification.
An empty level means *no* level constraint rather than "the one this explorer is for".

Nothing looked wrong, which is the interesting part: the level control is a disabled `<select>` holding a
single option, and a select whose value matches no option still displays the first one. It went on reading
"System requirements" while sending nothing of the kind.

Fixed by treating the scope as a floor rather than a default — it is which explorer you are on, not a filter
anybody chose. The specifications rail is also scoped now, so the System explorer no longer offers HLRD and
LLRD, documents it cannot show a single requirement from.

### "1.5 · Released" was invisible in the release selector

The dropdown list is a surface the platform draws. It inherits the control's background — dark, because the
control sits in the dark sidebar — but not its text colour, so every option rendered in the platform's dark
default. Only the option under the cursor was readable, because the system highlight made it so. Fixed by
styling the options explicitly; this affected the program selector too.

### A released version reported "Not started"

FMS 1.5 is Released and Locked, and its release-campaign cell read *Not started* — the fallback for a missing
campaign record, shown without asking whether a campaign could still be pending. It cannot: the release
already happened. A released version now reads *Released*.

### Saving said nothing

A failed save reported; a successful one changed the form's mode and otherwise stayed silent, which is
indistinguishable from nothing having happened. Check-in now confirms.

## What turned out to be working

Reported in good faith and worth recording, because "we looked and it was fine" is a finding too.

### Audit history ordering

Already newest-first. The screenshot shows `11/14 → 11/4 11:00 → 11/4 11:00 → 11/4 10:00 → 11/4 9:00 →
11/3`, which is descending; `11/14` and `11/4` are easy to misread as out of order.

The general rule was checked across the product rather than taken on trust. The remaining ascending orders
are a discussion thread and two revision sequences, where oldest-first is correct and reversing them would be
the defect. No change made.

### Only SCR-000031 on the Change Impact Review

That page is the detail view of one change — `/release-readiness/changes/{id}`, reached from a specific row
in readiness. Showing one change is what it is for.

The confusion is real, though, and is a naming problem: *Change Impact Review* sounds like a review of all
change impacts. The change number is on the page, in the eyebrow above the title, but the breadcrumb says
only "Change Impact Review". Worth naming better; not changed, because it is a judgement call rather than a
defect.

## What needs a decision before it can be built

None of these were coded. Each is a genuine gap, and each turns on a question only the product owner can
answer. They are ordered by how much of the demonstration they affect.

### 1. Revising an approved change request

> *"I need to be able to dis-approve it and work on it again (and it would become Revision A so to speak).
> This would apply to any change request, system or software, test procedures, etc."*

A mechanism already exists — `POST /api/scrs/{id}/next-revision`, which creates the next controlled revision
of an approved change request — and the workspace has a **Revise** action that calls it. So the capability is
there and the reviewer did not find it, which is itself the finding.

**The question is which of two things is wanted**, because they are different products:

- *Supersede*: the approved revision stays approved and immutable, and a new revision is created that
  supersedes it. This is what exists, and it is what PRODUCT_PRINCIPLES #3 requires — approved content is
  never silently overwritten.
- *Un-approve*: the approval is withdrawn from the existing revision, which returns to Draft carrying its
  signatures' history but not their force.

The reviewer's words — "dis-approve it and work on it again" — read as the second. The second conflicts with
principle #3 as written. That conflict is resolvable (a withdrawal is an attributable event, not an erasure)
but it is a scope decision with a recorded principle on the other side of it, so it needs a decision record
rather than an implementation.

Either way, discoverability is the immediate problem: **Revise** needs to be obvious from an approved change
request.

### 2. What the impact-disposition field is for

> *"If I modify a requirement, automatically we should know the impact — what HLRs are impacted based on
> existing trace, what test procedures are impacted. I'm not sure what this field on the right side does?"*

Both things are real and they are not the same thing:

- **Computed impact** — what the trace graph says is affected. AeroLink has this, on the Change Impact Review
  page and in the requirement inspector's Trace & impact tab. It is not shown while authoring, which is where
  the reviewer wanted it.
- **Declared disposition** — the author stating what they have considered, across five categories, frozen
  into the review snapshot hash. That is the field on the right, and it is deliberately not automatic: its
  value is that a person asserted it and signed for it.

They complement each other: the computed set should be *shown* next to the disposition so the author
dispositions something concrete instead of an empty category. That is a real improvement and a sizeable one.
The question is whether the computed impact belongs inline in the authoring flow or stays one click away.

### 3. Draft documents with a DRAFT watermark

> *"I should be able to generate a draft 1.6 document containing the full 1.5 baseline plus any APPROVED
> changes, watermarked DRAFT until the document itself is approved."*

Clear, coherent and not built. Generation today is from a **frozen** baseline; this asks for generation from
*released predecessor + approved-but-not-yet-baselined changes*, which is a different and legitimate input
set — it is how a team sees the document taking shape before freeze.

The decision needed: this produces a controlled-looking document from an input set that is still moving. That
is exactly what the watermark is for, but the rules need stating — whether such a document gets a document
number, whether it is retained, and what stops it being mistaken for the approved one later.

### 4. SYSRD section allocation

> *"Each system requirement should be allocated to a specific section in the SYSRD, and during a change
> request we need to detail which section a new requirement will go into. The section names should be
> clickable and filter to that section."*

The structure exists — specifications have sections, requirements have specification nodes, and the workspace
can filter by specification. Two pieces are missing: the section headings are not clickable filters, and the
change-request authoring flow does not ask which section a new requirement belongs in.

The clickable filter is small. The authoring change is not, because it makes section a required attribute of
a proposed requirement and therefore part of the review snapshot.

**Decided 2026-07-27: the filter was built; the allocation was not, and deliberately.** Requirements keep the
placement they have — `(digits of the identifier) % 5`, spread evenly across five headings the product
invents. The reasoning is that there is no real programme yet, so allocating into a structure AeroLink made
up would be building something to be replaced the moment a real SYSRD arrives.

Worth being exact about one word, because it decides whether this is safe: the placement is **stable, not
random**. `SYSR-000042` lands in the same section on every machine and every seed, so a demonstration behaves
the same tomorrow as today and the same on the presenting machine as on the author's. A genuinely random
assignment would reshuffle and would not be acceptable. Revisit when a programme's own SYSRD structure
exists; until then the sections navigate correctly and mean nothing, and nobody should claim otherwise.

### 5. "Selected for baseline", and a place for unassigned changes

> *"'Selected for baseline' doesn't make sense. A change request should automatically be assigned to the
> in-work build. There should be a 'rainy day' collector for work not yet ready to be allocated. And the
> state should be 'Incorporated', not 'Baseline selected' — Approved means approved but not yet released."*

Three separate requests:

- **Renaming the displayed state** is safe and cheap: `stateLabel()` already exists for exactly this and the
  `ScrState` enum need not move. But *Incorporated* and *SelectedForBaseline* do not mean the same thing —
  selected-for-baseline means chosen into a candidate that has not necessarily been frozen or released.
  Confirm which meaning the label should carry before changing it.
- **Auto-assigning to the in-work version** contradicts nothing, but the deliberate separation of *approved*
  from *included* is a load-bearing product principle (#7), so it needs stating carefully.
- **A "rainy day" collector** is a new concept — closest to the existing `Deferred` state plus `Retarget`,
  which already means "approved, and deliberately not in this release" (CAPABILITY_ROADMAP item 5). Worth
  checking whether Deferred already is the rainy day, under a worse name.

### 6. Verification should open on the work the release created

> *"I would expect to land on a page where 1.6 activities are front and centre — new test procedures required
> for new requirements, procedures to modify, links to remove."*

This exists as data: approving a change raises a verification-impact item per introduced or modified
requirement, and the queue has owners and states. It is not what the Verification surface opens on. This is a
landing-page change rather than new capability, and it is probably the highest-value small change in the
whole document — it is beat 1 of the demonstration.

### 7. Criticality and Owner on a change request

> *"Criticality doesn't make sense, we don't need it. And who is the 'owner'? The author is the author."*

Removing fields is a scope decision and they may be referenced by schema records, saved views or the seeded
showcase. Left alone pending a decision. Worth noting that Owner is genuinely ambiguous next to Author, and
that the reviewer is right that nothing in the product currently distinguishes them.

### 8. Existing versus modified requirement wording

> *"Perhaps the existing wording goes into a non-editable 'existing requirement wording' field, with
> 'modified requirement wording' right below it."*

Clear and a good idea. It is a change to the proposal editor's layout for the Modify case, and it affects the
redline the reviewer sees. Small, but it changes an authoring surface that is under review discipline, so it
is listed here rather than done quietly.

### 9. Real names and portraits everywhere

> *"We have a lot of fictitious people created; I expect to see their names and pictures everywhere instead
> of 'assurance.reviewer'."*

`PeopleRegistry.ts` already maps every username the reviewer saw — `cm.fms`, `lead.reviewer`,
`systems.author`, `assurance.reviewer` — to a name, a role and a portrait. Only two surfaces use it. Every
other surface renders the raw username.

Not a decision so much as a sweep: it touches audit histories, approval steps, control status panels,
assignments and queues across most of the product. Worth doing as its own change, with a guard that fails when
a bare username reaches the screen.

## Verification

Everything fixed above ships with a test that fails without the fix. The two most important were checked by
reverting the fix and confirming the test caught it: the space test fails against the trimming version, and
the explorer test fails when the scope stops being a floor.
