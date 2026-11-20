// Vertex documentation: what every page shares. The pages are plain HTML; this adds the frame round them (top bar,
// sidebar, contents), highlights code, and runs the interactive parts (search, the C# / visual scripting switch,
// tutorial progress, pictures that open big).

const Site = (() => {
    const pages = [
        { group: "Start", items: [
            { href: "index.html", title: "Home", icon: "◆" },
            { href: "download.html", title: "Download", icon: "↓" },
            { href: "getting-started.html", title: "Getting started", icon: "▶" }
        ] },
        { group: "Tutorials", items: [
            { href: "tutorial-duel.html", title: "Duel: a 1v1 shooter", icon: "✦", tag: "Editor" },
            { href: "tutorial-code-only.html", title: "Coin Grab: code only", icon: "●", tag: "C#" }
        ] },
        { group: "Guides", items: [
            { href: "editor.html", title: "The editor", icon: "▣" },
            { href: "csharp.html", title: "C# scripting", icon: "{}" },
            { href: "visual-scripting.html", title: "Visual scripting", icon: "⌁" },
            { href: "effects.html", title: "Particles and VFX graphs", icon: "✺" },
            { href: "shaders.html", title: "Shaders and post effects", icon: "▦" },
            { href: "animation.html", title: "Animation", icon: "◈" },
            { href: "audio.html", title: "Sound and audio graphs", icon: "♪" },
            { href: "ai.html", title: "Navigation and AI", icon: "⌖" },
            { href: "multiplayer.html", title: "Multiplayer", icon: "⇆" },
            { href: "workflows.html", title: "Code and the editor", icon: "⊞" }
        ] },
        { group: "Reference", items: [
            { href: "nodes.html", title: "Nodes", icon: "◇" },
            { href: "api.html", title: "C# API", icon: "≡" }
        ] }
    ];

    const here = location.pathname.split("/").pop() || "index.html";
    const store = {
        get: (key, fallback) => { try { return localStorage.getItem("vertex." + key) ?? fallback; } catch { return fallback; } },
        set: (key, value) => { try { localStorage.setItem("vertex." + key, value); } catch { } }
    };

    // ---------------------------------------------------------------- The frame

    function chrome() {
        document.documentElement.dataset.theme = store.get("theme", "dark");

        const top = document.createElement("header");
        top.className = "topbar";
        top.innerHTML = `
            <button class="icon-button menu-button" aria-label="Menu">☰</button>
            <a class="brand" href="index.html"><span class="logo">V</span> Vertex <small>docs</small></a>
            <a class="badge stage" href="download.html#pre-alpha" title="Vertex is in pre-alpha: not recommended for production yet">pre-alpha</a>
            <nav class="top-links">
                <a href="download.html">Download</a>
                <a href="getting-started.html">Start</a>
                <a href="tutorial-duel.html">Tutorial</a>
                <a href="nodes.html">Nodes</a>
                <a href="api.html">C# API</a>
            </nav>
            <div class="top-spacer"></div>
            <button class="search-button" aria-label="Search">⌕ <span>Search the docs</span> <kbd>Ctrl K</kbd></button>
            <button class="icon-button theme-button" aria-label="Light or dark">◐</button>
            <a class="icon-button" href="https://github.com/BiggyStudios/Vertex" aria-label="GitHub" title="Vertex on GitHub">⌂</a>`;
        document.body.prepend(top);
        top.querySelectorAll(".top-links a").forEach(a => { if (a.getAttribute("href") === here) a.classList.add("active"); });
        top.querySelector(".theme-button").addEventListener("click", () => {
            const next = document.documentElement.dataset.theme === "light" ? "dark" : "light";
            document.documentElement.dataset.theme = next;
            store.set("theme", next);
        });
        top.querySelector(".menu-button").addEventListener("click", () => document.body.classList.toggle("menu-open"));
        top.querySelector(".search-button").addEventListener("click", openSearch);

        const layout = document.querySelector(".layout");
        if (!layout) return;

        const side = document.createElement("aside");
        side.className = "sidebar";
        side.innerHTML = pages.map(group => `<h4>${group.group}</h4>` + group.items.map(item =>
            `<a href="${item.href}" class="${item.href === here ? "active" : ""}"><span>${item.icon}</span>${item.title}${item.tag ? `<span class="tag">${item.tag}</span>` : ""}</a>`).join("")).join("");
        layout.prepend(side);

        if (!layout.classList.contains("wide")) {
            const toc = document.createElement("nav");
            toc.className = "toc";
            layout.append(toc);
        }

        // Next and previous, at the bottom of guides and tutorials.
        const flat = pages.flatMap(g => g.items);
        const index = flat.findIndex(p => p.href === here);
        const main = layout.querySelector("main");
        if (index > 0 && main && !layout.classList.contains("wide")) {
            const prev = flat[index - 1], next = flat[index + 1];
            const nav = document.createElement("div");
            nav.className = "cards";
            nav.style.marginTop = "70px";
            nav.innerHTML = (prev ? `<a class="card" href="${prev.href}"><p>← Back</p><h3>${prev.title}</h3></a>` : "<span></span>") +
                (next ? `<a class="card" href="${next.href}" style="text-align:right"><p>Next →</p><h3>${next.title}</h3></a>` : "");
            main.append(nav);
        }
    }

    // ---------------------------------------------------------------- Headings and contents

    function slug(text) {
        return text.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "");
    }

    function headings() {
        const main = document.querySelector("main.content");
        const toc = document.querySelector(".toc");
        if (!main) return;

        const found = [...main.querySelectorAll("h2, h3")].filter(h => !h.closest(".card, .node-entry, .api-type, .no-toc"));
        for (const h of found) {
            if (!h.id) h.id = slug(h.textContent);
            const anchor = document.createElement("a");
            anchor.className = "anchor";
            anchor.href = "#" + h.id;
            anchor.textContent = "#";
            h.append(anchor);
        }

        if (!toc || found.length < 2) return;
        toc.innerHTML = "<h5>On this page</h5>" + found.map(h =>
            `<a href="#${h.id}" class="${h.tagName === "H3" ? "sub" : ""}" data-for="${h.id}">${h.firstChild.textContent}</a>`).join("");

        // The heading being read is lit up in the contents.
        const links = new Map([...toc.querySelectorAll("a")].map(a => [a.dataset.for, a]));
        let current = null;
        const spy = () => {
            let best = found[0];
            for (const h of found) if (h.getBoundingClientRect().top < 140) best = h;
            if (best === current) return;
            current = best;
            links.forEach(a => a.classList.remove("active"));
            links.get(best.id)?.classList.add("active");
        };
        addEventListener("scroll", spy, { passive: true });
        spy();
    }

    // ---------------------------------------------------------------- Code

    const keywords = new Set(("abstract as base bool break byte case catch char class const continue decimal default delegate do double else enum event " +
        "explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator " +
        "out override params private protected public readonly record ref return sbyte sealed short sizeof static string struct switch this throw true try " +
        "typeof uint ulong unchecked unsafe ushort using var virtual void volatile while async await get set init value when where yield nameof not and or").split(" "));

    function escape(text) {
        return text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
    }

    /** C# as HTML with colours: keywords, types, strings, numbers, comments, method calls and attributes. */
    function highlight(source) {
        const pattern = /(\/\/[^\n]*|\/\*[\s\S]*?\*\/)|(\$?@?"(?:[^"\\]|\\.)*"|'(?:[^'\\]|\\.)')|(\b\d[\d_]*(?:\.\d+)?[fdmFDM]?\b)|(\[[A-Z][A-Za-z]*(?=[(\]]))|([A-Za-z_][A-Za-z0-9_]*)(\s*(?=[(<]))?/g;
        let out = "", last = 0, match;
        while ((match = pattern.exec(source))) {
            out += escape(source.slice(last, match.index));
            last = pattern.lastIndex;
            const [text, comment, string, number, attribute, word, call] = match;
            if (comment) out += `<span class="tok-c">${escape(comment)}</span>`;
            else if (string) out += `<span class="tok-s">${escape(string)}</span>`;
            else if (number) out += `<span class="tok-n">${number}</span>`;
            else if (attribute) out += `[<span class="tok-a">${attribute.slice(1)}</span>`;
            else if (word) {
                const rest = call ?? "";
                const next = source[pattern.lastIndex];
                if (keywords.has(word)) out += `<span class="tok-k">${word}</span>${rest}`;
                else if (next === "(") out += `<span class="tok-m">${word}</span>${rest}`;
                else if (/^[A-Z]/.test(word)) out += `<span class="tok-t">${word}</span>${rest}`;
                else out += word + rest;
            }
            else out += escape(text);
        }

        return out + escape(source.slice(last));
    }

    /** Turns <pre class="cs"> (or one with data-sample, filled from the samples) into a framed block with a copy button. */
    function codeBlocks(root = document) {
        root.querySelectorAll("pre.cs, pre.plain, pre.sh").forEach(pre => {
            if (pre.closest(".code")) return;
            let source = pre.dataset.sample && window.VertexSamples ? window.VertexSamples[pre.dataset.sample] : pre.textContent;
            if (source == null) source = `// ${pre.dataset.sample} wasn't found`;
            source = source.replace(/^\n+|\s+$/g, "");
            if (pre.dataset.from) source = source.slice(source.indexOf(pre.dataset.from));

            const block = document.createElement("div");
            block.className = "code";
            const file = pre.dataset.file ?? (pre.dataset.sample ? pre.dataset.sample.split("/").pop() : pre.classList.contains("sh") ? "Terminal" : "C#");
            block.innerHTML = `<div class="code-head"><span class="dot"></span><span class="file">${file}</span><button class="copy">Copy</button></div><pre><code></code></pre>`;
            block.querySelector("code").innerHTML = pre.classList.contains("cs") ? highlight(source) : escape(source);
            // Its id goes with it, for pages that fill it in later; Copy copies what it shows then.
            if (pre.id) block.querySelector("code").id = pre.id;
            block.querySelector(".copy").addEventListener("click", e => {
                navigator.clipboard?.writeText(block.querySelector("code").textContent);
                e.target.textContent = "Copied";
                e.target.classList.add("done");
                setTimeout(() => { e.target.textContent = "Copy"; e.target.classList.remove("done"); }, 1400);
            });

            if (source.split("\n").length > 34 && !pre.hasAttribute("data-open")) {
                block.classList.add("collapsed");
                const expand = document.createElement("button");
                expand.className = "expand";
                expand.textContent = `Show all ${source.split("\n").length} lines`;
                expand.addEventListener("click", () => {
                    const open = block.classList.toggle("collapsed");
                    expand.textContent = open ? `Show all ${source.split("\n").length} lines` : "Show less";
                });
                block.append(expand);
            }

            pre.replaceWith(block);
        });
    }

    // ---------------------------------------------------------------- Movement and pictures

    function reveals() {
        const watch = new IntersectionObserver(entries => entries.forEach(e => {
            if (!e.isIntersecting) return;
            e.target.classList.add("shown");
            watch.unobserve(e.target);
        }), { rootMargin: "0px 0px -8% 0px" });
        document.querySelectorAll(".reveal").forEach((el, i) => {
            el.style.transitionDelay = (el.dataset.delay ?? 0) + "ms";
            watch.observe(el);
        });

        // Cards glow where the mouse is.
        document.addEventListener("pointermove", e => {
            const card = e.target.closest?.("a.card");
            if (!card) return;
            const r = card.getBoundingClientRect();
            card.style.setProperty("--mx", `${e.clientX - r.left}px`);
            card.style.setProperty("--my", `${e.clientY - r.top}px`);
        });

        document.querySelectorAll(".figure img").forEach(img => img.addEventListener("click", () => {
            const box = document.createElement("div");
            box.className = "lightbox";
            box.innerHTML = `<img src="${img.src}" alt="">`;
            box.addEventListener("click", () => { box.classList.remove("open"); setTimeout(() => box.remove(), 250); });
            document.body.append(box);
            requestAnimationFrame(() => box.classList.add("open"));
        }));
    }

    // ---------------------------------------------------------------- C# or visual scripting

    function tracks() {
        const switches = document.querySelectorAll(".track-switch:not([data-own])");
        if (!switches.length && !document.querySelector("[data-track]")) return;

        const show = (track, animate = true) => {
            document.body.dataset.showing = track;
            store.set("track", track);
            switches.forEach(s => {
                s.querySelectorAll("button").forEach(b => b.classList.toggle("on", b.dataset.show === track));
                const on = s.querySelector("button.on"), pill = s.querySelector(".pill");
                if (on && pill) {
                    if (!animate) pill.style.transition = "none";
                    pill.style.left = on.offsetLeft + "px";
                    pill.style.width = on.offsetWidth + "px";
                    if (!animate) requestAnimationFrame(() => pill.style.transition = "");
                }
            });
            document.dispatchEvent(new CustomEvent("track", { detail: track }));
        };

        switches.forEach(s => {
            s.insertAdjacentHTML("afterbegin", '<span class="pill"></span>');
            s.querySelectorAll("button").forEach(b => b.addEventListener("click", () => {
                const y = s.getBoundingClientRect().top;
                show(b.dataset.show);
                // The switch stays under the mouse while the page changes length above it.
                scrollBy(0, s.getBoundingClientRect().top - y);
            }));
        });

        show(new URLSearchParams(location.search).get("track") ?? store.get("track", "cs"), false);
        addEventListener("resize", () => show(document.body.dataset.showing, false));
    }

    // ---------------------------------------------------------------- Tutorial progress

    function steps() {
        const all = [...document.querySelectorAll(".step[data-step]")];
        if (!all.length) return;

        const key = "steps." + here;
        const done = new Set(JSON.parse(store.get(key, "[]")));
        const bar = document.querySelector(".progress");
        const update = () => {
            all.forEach(s => s.classList.toggle("done", done.has(s.dataset.step)));
            if (!bar) return;
            const count = all.filter(s => done.has(s.dataset.step)).length;
            bar.querySelector(".bar span").style.width = (count / all.length * 100) + "%";
            bar.querySelector(".count").textContent = `${count} of ${all.length} steps done`;
        };

        all.forEach(step => {
            const toggle = document.createElement("button");
            toggle.className = "done-toggle";
            toggle.addEventListener("click", () => {
                done.has(step.dataset.step) ? done.delete(step.dataset.step) : done.add(step.dataset.step);
                store.set(key, JSON.stringify([...done]));
                update();
                toggle.textContent = done.has(step.dataset.step) ? "✓ Done" : "Mark as done";
            });
            toggle.textContent = done.has(step.dataset.step) ? "✓ Done" : "Mark as done";
            step.append(toggle);
        });

        update();
    }

    // ---------------------------------------------------------------- Search

    let palette, index = null, chosen = 0, found = [];

    // Each data file is loaded once; everything asking for it waits for that same load.
    const loading = new Map();

    function load(src) {
        if (!loading.has(src)) {
            loading.set(src, new Promise(resolve => {
                const existing = [...document.scripts].find(s => s.getAttribute("src") === src);
                if (existing && (window.VertexNodes && src.includes("nodes") || window.VertexApi && src.includes("api") || window.VertexGraphs && src.includes("graphs") || window.VertexSamples && src.includes("samples"))) return resolve();
                const s = document.createElement("script");
                s.src = src;
                s.onload = resolve;
                s.onerror = resolve;
                document.head.append(s);
            }));
        }

        return loading.get(src);
    }

    async function buildIndex() {
        await Promise.all([load("data/nodes.js"), load("data/api.js")]);
        const entries = [];
        for (const item of pages.flatMap(g => g.items)) entries.push({ kind: "Page", title: item.title, sub: item.href, href: item.href, icon: "¶" });
        for (const [section, list] of Object.entries(window.VertexSearchSections ?? {})) for (const s of list) entries.push(s);

        const libraries = window.VertexNodes?.libraries ?? {};
        for (const [kind, library] of Object.entries(libraries)) {
            for (const node of library.nodes) {
                entries.push({ kind: { vfx: "VFX node", audio: "Audio node" }[kind] ?? "Node", title: node.title, sub: `${node.category} · ${node.description}`,
                    href: `nodes.html?kind=${kind}#${encodeURIComponent(node.id)}`, icon: "◇", words: node.keywords });
            }
        }

        for (const ns of window.VertexApi?.namespaces ?? []) {
            for (const type of ns.types) {
                entries.push({ kind: "Type", title: type.name, sub: `${type.kind} in ${ns.name}`, href: `api.html#${type.fullName}`, icon: "T" });
                for (const m of type.members) {
                    if (m.kind === "constructor") continue;
                    entries.push({ kind: "Member", title: `${type.name}.${m.name}`, sub: m.signature, href: `api.html#${type.fullName}.${m.name}`, icon: m.kind === "method" ? "M" : m.kind === "property" ? "P" : m.kind === "event" ? "E" : "F" });
                }
            }
        }

        index = entries;
    }

    function score(entry, words) {
        const title = entry.title.toLowerCase(), hay = (entry.title + " " + (entry.words ?? "") + " " + entry.sub).toLowerCase();
        let total = 0;
        for (const w of words) {
            if (title === w) total += 100;
            else if (title.startsWith(w)) total += 40;
            else if (title.includes(w)) total += 20;
            else if (hay.includes(w)) total += 5;
            else return 0;
        }

        return total + (entry.kind === "Page" ? 15 : entry.kind === "Node" || entry.kind === "Type" ? 8 : 0) - title.length * 0.05;
    }

    function search(text) {
        const words = text.toLowerCase().split(/\s+/).filter(Boolean);
        const results = palette.querySelector(".results");
        if (!words.length) {
            found = index.filter(e => e.kind === "Page");
        } else {
            found = index.map(e => [score(e, words), e]).filter(([s]) => s > 0).sort((a, b) => b[0] - a[0]).slice(0, 40).map(([, e]) => e);
        }

        chosen = 0;
        if (!found.length) {
            results.innerHTML = `<div class="empty">Nothing matches “${escape(text)}”</div>`;
            return;
        }

        let html = "", group = null;
        found.forEach((e, i) => {
            if (e.kind !== group) { group = e.kind; html += `<div class="group">${group === "Member" ? "Members" : group + "s"}</div>`; }
            html += `<a class="result ${i === 0 ? "on" : ""}" href="${e.href}" data-i="${i}"><span class="kind">${e.icon}</span><span class="what"><div class="title">${escape(e.title)}</div><div class="sub">${escape(e.sub)}</div></span></a>`;
        });
        results.innerHTML = html;
    }

    async function openSearch() {
        if (!palette) {
            palette = document.createElement("div");
            palette.className = "palette";
            palette.innerHTML = `<div class="palette-box"><input placeholder="Search pages, nodes and the C# API…" aria-label="Search"><div class="results"><div class="empty">Loading…</div></div>
                <div class="hint"><span><kbd>↑</kbd> <kbd>↓</kbd> to pick</span><span><kbd>Enter</kbd> to go</span><span><kbd>Esc</kbd> to close</span></div></div>`;
            document.body.append(palette);
            palette.addEventListener("click", e => { if (e.target === palette) closeSearch(); });
            const input = palette.querySelector("input");
            input.addEventListener("input", () => search(input.value));
            input.addEventListener("keydown", e => {
                if (e.key === "ArrowDown" || e.key === "ArrowUp") {
                    e.preventDefault();
                    chosen = Math.max(0, Math.min(found.length - 1, chosen + (e.key === "ArrowDown" ? 1 : -1)));
                    palette.querySelectorAll(".result").forEach(r => r.classList.toggle("on", +r.dataset.i === chosen));
                    palette.querySelector(".result.on")?.scrollIntoView({ block: "nearest" });
                }
                if (e.key === "Enter" && found[chosen]) { location.href = found[chosen].href; closeSearch(); }
            });
            palette.querySelector(".results").addEventListener("click", () => setTimeout(closeSearch, 50));
        }

        palette.classList.add("open");
        const input = palette.querySelector("input");
        input.focus();
        input.select();
        if (!index) await buildIndex();
        search(input.value);
    }

    function closeSearch() {
        palette?.classList.remove("open");
    }

    addEventListener("keydown", e => {
        const typing = /INPUT|TEXTAREA/.test(document.activeElement?.tagName);
        if ((e.key === "k" && (e.ctrlKey || e.metaKey)) || (e.key === "/" && !typing)) { e.preventDefault(); openSearch(); }
        if (e.key === "Escape") { closeSearch(); document.body.classList.remove("menu-open"); }
    });

    // ---------------------------------------------------------------- Start

    function start() {
        chrome();
        codeBlocks();
        headings();
        tracks();
        steps();
        reveals();
        document.querySelectorAll("[data-graph]").forEach(el => window.GraphView?.mount(el, el.dataset.graph));
    }

    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start);
    else start();

    return { highlight, escape, codeBlocks, store, slug, load };
})();
