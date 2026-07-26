/**
 * The overview video, as data.
 *
 * THIS IS THE FILE TO EDIT. Everything a reviewer normally wants to change — wording, ordering, how long a
 * slide is held, which part of a screenshot is shown, where a highlight sits — is here. Nothing in
 * template.html needs to be touched to revise the content, and `node build.mjs` re-renders only the slides
 * whose data actually changed.
 *
 * ── The two slide shapes ──────────────────────────────────────────────────────────────────────────────
 *
 * A SCREENSHOT slide:
 *   {
 *     seconds: 16,                       // how long it is held
 *     chapter: 'Systems engineering',    // the small teal eyebrow
 *     title:   'Two lines maximum',      // ~64 characters is the practical ceiling; see README
 *     shot:    'requirements-system',    // a file in shots/, without the .png
 *     crop:    [x, y, width],            // region of the SOURCE image to show — see "Coordinates" below
 *     frameHeight: 480,                  // optional; default 690. Use for wide, short regions.
 *     marks: [                           // one or two. The first is teal, the second amber.
 *       { box: [x, y, w, h],             // in the same source coordinates as `crop`
 *         title: 'What this is',
 *         note:  'Why it matters, in one sentence.' },
 *     ],
 *   }
 *
 * A TEXT slide is `{ seconds, kind, ... }` where kind is one of: title, questions, twoColumn, stats, close.
 * Each kind's fields are visible in the entries below — copy an existing one and change the words.
 *
 * ── Coordinates ───────────────────────────────────────────────────────────────────────────────────────
 *
 * `crop` and `box` are in the pixels of the source PNG in shots/, which is 3200 x 1800 (a 1600 x 900
 * browser window captured at 2x). To find a coordinate: open the PNG, note the pixel position of the thing
 * you want, and use it directly. If you are reading positions off a viewer that displays the image at
 * 2000px wide, multiply by 1.6.
 *
 * Only crop's width is given — the height is derived from the frame so the image is never distorted.
 * Larger width = more context, smaller text. 1750-2600 is the useful range.
 *
 * ── Re-capturing a screenshot ─────────────────────────────────────────────────────────────────────────
 *
 * See README.md. The captures come from driving the real product, never from a mockup.
 */

export const slides = [

  { seconds: 7, kind: 'title',
    kicker: 'Controlled engineering lifecycle platform',
    name: 'AeroLink',
    lede: 'The record of how a system was built, kept as data instead of documents.',
    meta: 'A four-minute overview · Systems · Software · Verification · Management' },

  { seconds: 16, kind: 'questions',
    kicker: 'The problem',
    title: 'Five questions that should take seconds',
    items: [
      'Which exact requirement revision was approved for this release?',
      'Which controlled change introduced it, and what was the reasoning?',
      'Which approved procedure verifies it, and what was the result?',
      'What failed, what evidence exists, and what retest superseded it?',
      'Can a released document be reproduced from its exact controlled inputs?',
    ],
    footnote: 'Today those answers are spread across documents, spreadsheets, test repositories, issue ' +
      'trackers, file shares and people’s memory. <em>Assembling them is the job. That is the job this ' +
      'removes.</em>' },

  { seconds: 15,
    chapter: 'What it is · everyone',
    title: 'A programme’s real state, computed from its own records',
    shot: 'command-center', crop: [550, 250, 2580],
    marks: [
      { box: [648, 304, 2464, 272],
        title: 'The active release, and how ready it actually is',
        note: 'Readiness is derived from open work, not typed into a status field.' },
      { box: [648, 872, 2464, 430],
        title: 'The controlled inventory this release inherits',
        note: '150 system requirements, 400 high-level, 700 low-level, 1,100 trace links, 515 procedures.' },
    ] },

  { seconds: 16,
    chapter: 'Systems engineering',
    title: 'Requirements are authoritative, and deliberately read-only',
    shot: 'requirements-system', crop: [1400, 850, 1750],
    marks: [
      { box: [2320, 996, 790, 120],
        title: 'A requirement cannot be edited in place',
        note: 'Which is what makes “the approved revision” a phrase that means something.' },
      { box: [2320, 1136, 790, 86],
        title: 'The only route to a change is a controlled change request',
        note: 'So every edit arrives with a reason, an author, and a review attached.' },
    ] },

  { seconds: 15,
    chapter: 'Systems engineering',
    title: 'A change opens with its case, and closes its impact',
    shot: 'change-request-new', crop: [620, 640, 2500],
    marks: [
      { box: [648, 690, 2440, 124],
        title: 'Three gates, in order',
        note: 'Review cannot begin while impacts are still open — here, nought of one closed.' },
      { box: [2290, 1090, 800, 165],
        title: 'Identity and authorship come from the server',
        note: 'The number is assigned on save and the author from the session, so neither can be typed in.' },
    ] },

  { seconds: 16,
    chapter: 'Software engineering',
    title: 'Every approval is a signature over a known snapshot',
    shot: 'change-request-detail', crop: [620, 500, 2500],
    marks: [
      { box: [648, 528, 1700, 468],
        title: 'The engineering case is part of the record',
        note: 'Problem, analysis and proposed solution stay with the change permanently.' },
      { box: [2413, 1096, 700, 300],
        title: 'Signed by a named approver, against a content hash',
        note: 'An approval refers to an exact snapshot, never to “the latest version”.' },
    ] },

  { seconds: 16,
    chapter: 'Software engineering · configuration management',
    title: 'A baseline is an exact manifest, not a folder of files',
    shot: 'baselines-detail', crop: [620, 460, 2500],
    marks: [
      { box: [1219, 488, 1880, 290],
        title: 'It inherits an exact prior baseline, by hash',
        note: 'So what shipped can be reconstructed from controlled inputs rather than from a shared drive.' },
      { box: [2429, 1150, 690, 350],
        title: 'Append-only control history',
        note: 'Who selected what, and when. Events are added; nothing is quietly edited away.' },
    ] },

  { seconds: 15,
    chapter: 'Test engineering',
    title: 'Procedures are approved before they can be used',
    shot: 'verification-procedures', crop: [620, 720, 2500],
    marks: [
      { box: [648, 744, 2440, 220],
        title: 'Coverage is measured, not asserted',
        note: '150 requirements, 150 with procedure coverage, zero gaps — counted from the links themselves.' },
      { box: [690, 1296, 2390, 300],
        title: 'Approved, authored, and linked to exact requirements',
        note: 'A procedure that has not been independently approved cannot record a result.' },
    ] },

  { seconds: 15,
    chapter: 'Test engineering',
    title: 'A result is a determination, and it is never overwritten',
    shot: 'verification-executions', crop: [620, 946, 2500], frameHeight: 560,
    marks: [
      { box: [678, 1146, 2410, 146],
        title: 'Immutable determinations, with retest lineage',
        note: 'A later retest supersedes an earlier failure operationally — without erasing it historically.' },
      { box: [678, 1306, 2410, 350],
        title: 'Who, when, and against what',
        note: 'The engineer, the timestamp, and the evidence reference are recorded, not reconstructed afterwards.' },
    ] },

  { seconds: 16,
    chapter: 'The thread · everyone',
    title: 'One question, answered across the whole lifecycle',
    shot: 'traceability', crop: [680, 1288, 1750], frameHeight: 480,
    marks: [
      { box: [700, 1366, 1720, 430],
        title: 'System requirement → software requirement → procedure → evidence → the baseline it shipped in',
        note: 'This is the chain an auditor asks for, assembled on demand rather than by hand — and the ' +
          'product states plainly where a link is only partially evidenced.' },
    ] },

  { seconds: 14,
    chapter: 'Outputs · everyone',
    title: 'Documents are generated, not maintained by hand',
    shot: 'traceability', crop: [620, 540, 2500],
    marks: [
      { box: [648, 536, 624, 84],
        title: 'Controlled documents are outputs',
        note: 'The structured record is authoritative; the document is a view of it at a moment.' },
      { box: [2740, 676, 352, 80],
        title: 'Produced on demand, in PDF or Word',
        note: 'Generated from the records as they stand, so a document cannot drift away from them.' },
    ] },

  { seconds: 17,
    chapter: 'Management',
    title: 'Readiness is computed from the record, so it cannot be optimistic',
    shot: 'release-readiness', crop: [620, 700, 2500],
    marks: [
      { box: [680, 756, 2400, 232],
        title: 'Gates that reflect actual state',
        note: 'Trace and verification are flagged because the underlying records say so — not because a ' +
          'slide was updated.' },
      { box: [680, 1248, 1720, 504],
        title: 'The exact remaining work, and what clears it',
        note: '“5 of 7 remaining”, with the specific action beside it. No interpretation required.' },
    ] },

  { seconds: 15, kind: 'twoColumn',
    kicker: 'Scope · written for the quality group',
    title: 'What it does, and what it deliberately does not claim',
    left: {
      heading: 'What it does', tone: 'yes',
      items: [
        'Holds requirements, changes, procedures, results, baselines and approvals as structured, controlled records',
        'Keeps approved history immutable — a correction creates a new revision, never a silent overwrite',
        'Generates documents reproducibly, from named controlled inputs',
        'Ties every action to an authenticated person and an electronic signature',
      ] },
    right: {
      heading: 'What it does not claim', tone: 'no',
      items: [
        'It makes no certification, compliance, or tool-qualification claim of any kind',
        'It does not run tests and does not decide outcomes — a person records a determination',
        'It is not a document editor, and does not manage plans, standards, architecture or source code',
        'It contains no AI features, and its client makes no external request at runtime',
      ] } },

  { seconds: 14, kind: 'stats',
    kicker: 'Deployment · measured, not estimated',
    title: 'It runs entirely inside our own network',
    items: [
      { value: '150', label: 'simultaneous database clients, measured against a single-workstation deployment' },
      { value: '50,000', label: 'requirements held and queried on that same one machine' },
      { value: '0', label: 'external requests at runtime — no cloud service, no vendor dependency, nothing leaving the building' },
    ],
    footnote: 'On-premises by design, because controlled programme data should not leave our network. ' +
      '<em>Every number here came from running it, not from a datasheet.</em>' },

  { seconds: 16, kind: 'close',
    kicker: 'Where this stands',
    title: 'This exists, and it runs today',
    body: 'Everything in this video is the working product against a live flight-management dataset — not ' +
      'a prototype and not mockups. It is not finished, and the gaps are known and written down.',
    ask: 'The ask is support to keep building it.',
    next: 'The most useful next step is one real programme willing to try it alongside what they use today.' },

];

/** Shown in the corner of every frame, and in the shareable player's chapter readout. */
export const branding = {
  wordmark: 'AeroLink',
  footer: 'Internal overview · 2026',
};
