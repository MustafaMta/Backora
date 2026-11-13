# Backora — Precision Data Protection (V2 design system)

One design language for every V2 screen. Domain model from enterprise backup platforms (Rubrik, Veeam,
Cohesity); craftsmanship benchmark from Linear. Original Backora identity and assets; nothing is copied.

## What the product communicates
- **Protection state first.** Every job has a protection state (Protected, At risk, Failing, Paused, Not run
  yet) and a one-line policy: *schedule · retention · number of sites*.
- **Recovery points are backup sets.** One point = one backup run; it lists every site that holds a copy and
  whether each copy is verified. Restores start from a point or from a copy.
- **Multi-site DR is a first-class picture.** The protection map draws Source → Primary storage → DR sites from
  real job assignments; each route is coloured by its latest copy (stored, failing, copying, none yet).
- **Honesty over reassurance.** Verification is shown separately from a *proven* (completed) restore. Paused
  jobs are never described as "running". No score is invented.

## Identity
- **Logo:** the original Backora emblem (crowned keep with a central arch and four columns on a three-step ziggurat), refined with exact geometry and faceted in sapphire: each step has a lit top edge so it reads as solid stone. Free-standing symbol `wwwroot/icons/backora-mark.svg` beside the "Backora" wordmark in Geist 650. `backora-mark-small.svg` is a bolder cut for 16–24px; `backora-mark-tile.svg` / `-square.svg` put the symbol on a graphite tile for app icons. Raster: `favicon.ico` (16/24/32/48), `favicon-32.png`, `icon-192/512.png`, `apple-touch-icon.png`. `BrandMark.razor`. Original artwork: `frontend/logo.png`.
- **Wordmark:** "Backora" set in Geist 650 beside the mark, with a small-caps "Data protection" descriptor.
- **Command rail:** a graphite sidebar in both themes; cool white (light) or graphite (dark) work surface.

## Colour (`wwwroot/styles/foundation.css`)
| Role | Light | Dark |
|---|---|---|
| Work surface / panels | `#f6f7f9` / `#ffffff` | graphite `#0b0d12` / `#10131a` |
| Ink | `#0f1220` → `#a6abb9` | `#eceef4` → `#535868` |
| Brand + action (sapphire indigo) | `#4350e0` (text `#3a46cf`) | `#6c7bff` |
| Primary button | indigo gradient `--brand-a → --brand-b`, white text | same |
| Protection flow (teal) | `#0f8f86` | `#4cc5b3` |
| Protected / at risk / failing | `#1c9f60` / `#e1921c` / `#df4639` | `#35b97b` / `#e5a030` / `#ec5b4c` |
| Command rail | graphite `#11141b → #0b0d12`, indigo active marker | `#0c0e14 → #07080c` |

Indigo marks actions, selection and live work in progress. The gold logo is the brand mark only. Green, amber and
red are used only for real operational state. The sign-in page is always dark (`.ui-dark`).

## Motion
- Live work uses `LiveFlow` (Queued → Connect → Back up → Verify → Copy to sites on runs; Queued → Unlock →
  Restore data → Verify files on restores). Light travels into the active stage; finished stages turn solid indigo.
  Stages come only from API status, never estimates.
- Progress bars are indigo with a moving sheen; copies in flight show a climbing upload glyph; healthy routes on the
  protection map carry a travelling glint; running status icons glow indigo.
- Sign-in: a real-time WebGL render of the ziggurat (`js/auth-scene.js`), SVG fallback (`AuthArt.razor`).
- Everything honours `prefers-reduced-motion`.

## Type, space, shape
Geist / Geist Mono. 13px body, 14px panel titles, 22px page titles, 11px uppercase captions for table heads,
summary labels and nav groups. 4px spacing grid; radii 5/7/9/12px; controls 34px; table rows 44px.

## Layout patterns
| Pattern | Where |
|---|---|
| Page header (crumb, title + resource glyph + state, one line of context, actions) + toolbar (tabs with counts, search) | every page |
| Table card (caps header row, rows, footer) with sort, multi-select, bulk actions, context menu, hover actions | Backup jobs, servers, storage, recovery points, history, transfers, users, audit… |
| Protection summary strip (5 facts) + main panels + properties aside | backup job, run, restore, server, execution host |
| Status band (state emblem, verdict, facts, actions) + inventory row + panels | Overview |
| Create layout: card with segmented progress or sections + live plan panel | Create backup, restore, add server, add storage / DR site, template |
| Document form (section intro left, fields right, one card) | edit user, settings, account |

## Create experiences
| Task size | Pattern | Used for |
|---|---|---|
| Small | Drawer (right side, pinned footer) | New/edit variable, invite user |
| Medium | Create page: one card (head, sections, footer with Cancel · hint · primary) + live side panel | Add server, add storage, add DR site, template |
| Large | Wizard: segmented progress + the same card + a live "plan" that fills in as you go | Create/edit backup job (5 steps), restore (6 steps) |

Rules: one primary action per screen, in the card footer; choices as compact tiles (icon, title, two-line note,
check mark) or option rows; frequency and retention as segmented controls; advanced settings collapsed;
smart defaults (most-used primary storage, suggested method for the server's engine, browser time zone).
Encrypted sites are disabled when the execution host lacks the `age` tool. A new DR site can be assigned to
jobs right after it's added. Adding a server hands off to "Protect this server" with the engine preselected.

## Components
`PageHead`, `Panel`, `Choice`, `Tile`, `Steps` (segmented progress), `Drawer`, `Dialog`, `Confirm` (typed confirmation), `Dropdown`, `Switch`,
`FormField`, `Empty`, `StatusIcon` (done/failed/warning/running/paused/idle), `Icon` (Lucide + product glyphs),
`BrandMark`; app-level `StatusBadge`, `DrTopology`, `CopyNode`, `ProtectionEmblem`, `AuthArt`
(sign-in artwork), `LiveFlow` (live pipeline), `AlertsMenu`, `Pager`, `SearchBox`, `ListSkel`.

## Interaction
⌘K search, `j`/`k` list movement, `[` collapse rail, `?` shortcuts; right-click menu on backup jobs; row actions
on hover; live progress on runs, restores and transfers; every state-changing action asks for confirmation,
destructive ones require the name typed. Motion is subtle and honours reduced-motion.

## Review safety
Automated reviews run against an instance with `Frontend:ReadOnlyReview=true`, which refuses every
state-changing API call inside the frontend (`ReadOnlyReviewTests`).
