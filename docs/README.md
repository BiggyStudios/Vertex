# Vertex documentation

The documentation site: plain HTML, CSS and JavaScript, with no build step. Open `index.html` in a browser to read it
locally, or publish it with GitHub Pages.

## Publishing on GitHub Pages

In the repository's **Settings › Pages**, set **Source** to *Deploy from a branch*, the branch to `master` and the
folder to `/docs`. The site appears at `https://<owner>.github.io/<repository>/` a minute after each push.

## What's in it

| Path | What it is |
| --- | --- |
| `*.html` | The pages. Each is a `<main class="content">` inside `<div class="layout">`; `assets/site.js` adds the top bar, sidebar and contents round it. The sidebar's list of pages is at the top of `site.js`. |
| `assets/site.css` | Every page's styles, dark and light. |
| `assets/site.js` | The frame, code highlighting, search (Ctrl+K), the C# / visual scripting switch, tutorial progress. |
| `assets/graph.js` | Draws visual scripts and VFX graphs from their saved JSON, as the editor does. |
| `data/*.js` | The reference data: every node, the C# API, the samples' graphs and scripts. **Made by a tool, not by hand.** |
| `images/` | Screenshots of the editor and the samples. |

## Keeping the reference up to date

The node reference, the C# API pages and the tutorials' code and graphs come from the engine and the samples. After
changing nodes, public types or their doc comments, or the samples, make the data again from the repository's folder:

```
dotnet run --project Tools/DocsData
```

Code shown with `data-sample="Duel/Assets/Scripts/Health.cs"` and graphs shown with `data-graph="Health"` are read from
that data, so the tutorials always show the samples as they are.
