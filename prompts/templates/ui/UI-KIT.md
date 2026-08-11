# UI kit

This project's user interface is built from a fixed component library that Forge
installs at `wwwroot/forge-ui/`. It is already in the repo. Use it — do not write
your own CSS for anything it covers, and do not restyle what it gives you.

Link it once, in every page's `<head>`:

```html
<link rel="stylesheet" href="/forge-ui/theme.css">
<link rel="stylesheet" href="/forge-ui/forge-ui.css">
<script src="/forge-ui/forge-ui.js" defer></script>
```

`theme.css` must come first: it holds the colours, and `forge-ui.css` reads them.

## Rules CI enforces

These are checked mechanically before review. Breaking one sends the task back to you.

- **No `style="…"` attributes.** Anywhere. Use a class.
- **No colours or font names outside the kit.** No `#hex`, `rgb()`, `hsl()`,
  `oklch()`, no named colours, no `font-family`. Everything is already themed;
  a hard-coded colour breaks dark mode and the client's accent.
- **One stylesheet of your own, at most: `wwwroot/app.css`.** It may only use the
  design tokens below — `var(--fg-…)` — for colour, spacing, radius and type.
- **Every class must be a kit class or defined in `app.css`.** A class name you
  invent and never define is a typo, and the gate reports it as one.

Reach for `app.css` only for layout the kit genuinely has no answer for. It is
reported to the reviewer, so a page that mostly ignores the kit will come back.

## Anatomy of a page

Every screen uses the same frame. Do not invent a different one.

```html
<body>
  <div class="fg-shell fg-shell--no-sidebar">
    <header class="fg-topbar">
      <span class="fg-topbar__brand">Habit Tracker</span>
      <button class="fg-btn fg-btn--primary" data-fg-open="add-modal">New habit</button>
    </header>
    <main class="fg-main">
      <div class="fg-page-header">
        <div>
          <h1 class="fg-page-header__title">Today</h1>
          <p class="fg-page-header__subtitle">Four habits, two done.</p>
        </div>
      </div>
      <!-- content -->
    </main>
  </div>
</body>
```

With a sidebar, drop `fg-shell--no-sidebar` and add `<aside class="fg-sidebar">`
holding a `fg-nav` before `<main>`.

## Components

**Button** — `fg-btn` plus one of `fg-btn--primary` (one per screen),
`fg-btn--secondary`, `fg-btn--ghost`, `fg-btn--danger`. Sizes `fg-btn--sm`,
`fg-btn--lg`; `fg-btn--block` fills its container. A single glyph goes in
`fg-icon-btn` instead.

**Form field** — wrap each control:

```html
<div class="fg-field">
  <label class="fg-field__label" for="name">Name</label>
  <input class="fg-input" id="name" type="text" placeholder="Morning run">
  <span class="fg-field__hint">Shown on the dashboard.</span>
</div>
```

Controls: `fg-input`, `fg-textarea`, `fg-select`, `fg-checkbox`, `fg-radio`.
Add `fg-input--invalid` and a `fg-field__error` when validation fails. A checkbox
with its label on one line is `fg-choice`. A settings toggle is:

```html
<label class="fg-switch"><input type="checkbox"><span class="fg-switch__track"></span></label>
```

**Card** — `fg-card` with `fg-card__header`, `fg-card__body`, `fg-card__footer`
(any of them optional). `fg-card--interactive` if the whole card is clickable.
`fg-panel` is the flat version for plain grouping.

**List** — the workhorse for records:

```html
<ul class="fg-list fg-list--boxed fg-list--divided fg-list--hoverable fg-w-lg">
  <li class="fg-list__row">
    <div class="fg-list__content">
      <span class="fg-list__title">Morning run</span>
      <span class="fg-list__meta">5 day streak</span>
    </div>
    <span class="fg-badge fg-badge--success">Done</span>
    <div class="fg-list__actions">
      <button class="fg-icon-btn" aria-label="Delete">×</button>
    </div>
  </li>
</ul>
```

`fg-list__actions` fades in on hover. `fg-list__row--selected` marks the current row.

**Table** — `fg-table` (add `fg-table--hoverable`) inside `<div class="fg-table-wrap">`,
which keeps a wide table scrolling inside the page instead of stretching it.

**Status** — `fg-badge` with `--accent`, `--success`, `--warning`, `--danger`.
`fg-chip` is a removable badge for filters. `fg-avatar` holds initials or an image.

**Alert** — `fg-alert` with `--info`, `--success`, `--warning`, `--danger`, and an
optional `fg-alert__title`. For page-level messages that stay on screen.

**Empty state** — required wherever a list can be empty. Never render a blank area:

```html
<div class="fg-empty">
  <div class="fg-empty__icon">◎</div>
  <h2 class="fg-empty__title">No habits yet</h2>
  <p class="fg-empty__body">Add your first habit and it will show up here.</p>
  <button class="fg-btn fg-btn--primary" data-fg-open="add-modal">New habit</button>
</div>
```

**Loading** — `fg-skeleton` with `--text`, `--title` or `--block` while data is
fetched; `fg-spinner` for an in-place wait; `fg-progress` with an inner
`fg-progress__bar` whose `width` you set as a percentage.

**Modal** — markup only; the script does the rest:

```html
<div class="fg-modal" id="add-modal" hidden>
  <div class="fg-modal__dialog">
    <div class="fg-modal__header">New habit<button class="fg-icon-btn" data-fg-close>×</button></div>
    <div class="fg-modal__body"> … </div>
    <div class="fg-modal__footer">
      <button class="fg-btn fg-btn--secondary" data-fg-close>Cancel</button>
      <button class="fg-btn fg-btn--primary" id="save">Save</button>
    </div>
  </div>
</div>
```

`data-fg-open="add-modal"` on any element opens it; `data-fg-close` closes it, as
do Escape and a click on the backdrop. From script: `fg.openModal(id)`, `fg.closeModal(id)`.

**Toast** — `fg.toast("Habit saved", "success")`. Kinds: `success`, `danger`, or
omitted for neutral. No markup needed.

**Tabs** — each tab carries `data-fg-panel` naming the panel it shows:

```html
<div class="fg-tabs">
  <button class="fg-tabs__tab fg-tabs__tab--active" data-fg-panel="p-week">Week</button>
  <button class="fg-tabs__tab" data-fg-panel="p-month">Month</button>
</div>
<div class="fg-tabs__panel" id="p-week"> … </div>
<div class="fg-tabs__panel" id="p-month" hidden> … </div>
```

**Menu** — `fg-menu` wrapping a trigger with `data-fg-menu` and a
`ul.fg-menu__list[hidden]` of `button.fg-menu__item` (add `--danger` for a
destructive entry). Opens on click, closes on an outside click.

**Carousel** — `fg-carousel` containing `fg-carousel__track` of
`fg-carousel__slide` children, plus two `fg-carousel__arrow` buttons with
`--prev` and `--next`. Snapping, scrolling and arrow disabling are automatic.

**Also** — `fg-divider`, `fg-breadcrumb` (+ `fg-breadcrumb__sep`),
`fg-pagination`, `fg-tooltip` wrapping a `fg-tooltip__bubble`, `fg-nav` /
`fg-nav__item` / `fg-nav__item--active`.

## Layout and spacing

Arrange with these; never with a bare `display: flex` of your own.

- Direction: `fg-stack` (column), `fg-row` (row), `fg-wrap`, `fg-grid`
  (auto-fill cards), `fg-grid--2`, `fg-grid--3`.
- Alignment: `fg-center`, `fg-between`, `fg-end`, `fg-start`, `fg-grow`.
- Gaps: `fg-gap-1` … `fg-gap-6`. Padding: `fg-pad-2` … `fg-pad-5`.
  Margins: `fg-mt-2`, `fg-mt-4`, `fg-mb-2`, `fg-mb-4`.

**Width is a class, never a number.** `fg-w-sm` (22rem), `fg-w-md` (34rem),
`fg-w-lg` (52rem), `fg-w-xl` (72rem), `fg-w-full`, `fg-w-auto`. Add
`fg-container` to centre a width-limited block in the page.

Text: `fg-text-xs`, `fg-text-sm`, `fg-text-lg`, `fg-text-xl`, `fg-muted`,
`fg-strong`, `fg-mono`, `fg-truncate`. Other: `fg-scroll`, `fg-hide`, `fg-sr-only`.

## Tokens

The only values `app.css` may use. Colours: `--fg-canvas`, `--fg-surface`,
`--fg-surface-sunken`, `--fg-ink`, `--fg-ink-muted`, `--fg-ink-faint`,
`--fg-border`, `--fg-border-strong`, `--fg-accent`, `--fg-accent-hover`,
`--fg-accent-soft`, `--fg-on-accent`, `--fg-danger`, `--fg-success`,
`--fg-warning`, and the `-soft` variant of each status colour.

Spacing `--fg-space-1` … `--fg-space-6`; radius `--fg-radius-sm|md|lg|full`;
type `--fg-text-xs` … `--fg-text-2xl`; fonts `--fg-font-sans|heading|mono`;
elevation `--fg-elevation-1|2|3`; motion `--fg-motion-fast|slow` and `--fg-ease`.

Transitions, hover and focus states, and modal/toast animation are already in the
kit. You do not add motion; adding it in one place and not another is what makes
an application feel inconsistent.
