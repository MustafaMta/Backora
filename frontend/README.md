# Backora V2 frontend

The Blazor (.NET 10) frontend for Backora V2: BFF sign-in, same backend API. Production deployment lives in
`deploy/production` at the repository root.

## Layout

| Path | What |
|---|---|
| `wwwroot/styles` | The design system: `foundation.css` (tokens, light + dark), `components.css` (ui-* primitives), `shell.css` (sidebar, top bar), `pages.css` (page compositions) |
| `wwwroot/icons`, `wwwroot/illustrations` | Brand mark, icon licence. Icons and illustrations themselves are Razor components so they follow the theme |
| `src/Backora.DesignSystem` | Icons (Lucide paths + Backora domain icons), brand mark, illustrations, formatting, toasts, JS interop. Serves `wwwroot/*` at `_content/Backora.DesignSystem/` (its `wwwroot/styles`, `icons`, `illustrations` are relative symlinks to the top-level folders) |
| `src/Backora.Components` | Buttons, badges, panels, dialogs, confirmations, wizard steps, choice cards, switches, command palette |
| `src/Backora.Web` | The app: BFF sign-in (session cookie only, tokens server-side), API clients, shell and pages |
| `tests/Backora.Web.Tests` | bUnit + host tests |
| `Dockerfile` | Container build (context = repository root) |

## Run

Local (needs the API reachable on `127.0.0.1:5080`, e.g. through the `bko-api-proxy` container):

```bash
cd src/Backora.Web && ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:8444 dotnet run
```

Production: see `../deploy/production/README.md`.

Tests:

```bash
dotnet test tests/Backora.Web.Tests
```

## Pages in V2

Every sidebar entry and secondary screen is a V2 page on the same design system (see `docs/DESIGN.md`):
sign-in and two-step verification · Overview · Backups, backup detail, new/edit backup wizard (5 steps, any number of
DR sites) · History and run detail (per-site copy status and retry) · Recovery points, restore wizard, restores and
restore detail · Servers, server detail (connection test, host-key trust), add/edit server · Storage & DR, storage
form (pause, test, client-side encryption), Transfers · Execution host (status, trust, capabilities, connection
settings) · Templates, template editor, Variables · Users & access, user form, Audit log · Settings (alerts, email,
Teams, shared storage, default SSH key) · Your account (profile, password, two-step verification, access tokens,
sessions).

## Safe visual review

Run a second instance with `Frontend__ReadOnlyReview=true` (for example on port 8445) for screenshots and automated
browsing. It refuses every state-changing call before it reaches the API.
