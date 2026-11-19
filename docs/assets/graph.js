// Draws visual scripts and VFX graphs (their saved JSON) the way the editor does: nodes, ports and wires, which can be
// panned, zoomed and hovered for what each node does. "Run" sends pulses along the flow wires from each event, in the
// order the script would run them.

window.GraphView = (() => {
    const NS = "http://www.w3.org/2000/svg";
    const header = 24, row = 19, pad = 7, width = 196;

    const nodes = () => window.VertexNodes;

    function el(name, attributes = {}, parent) {
        const e = document.createElementNS(NS, name);
        for (const [k, v] of Object.entries(attributes)) e.setAttribute(k, v);
        parent?.append(e);
        return e;
    }

    function find(kind, id) {
        return nodes()?.libraries[kind]?.nodes.find(n => n.id === id);
    }

    function color(type) {
        return nodes()?.types[type] ?? "#9ea3b2";
    }

    /** A value as the editor shows it on a node: numbers short, vectors with commas, files by name. */
    function show(value) {
        if (value == null) return "";
        if (Array.isArray(value)) return value.map(v => +(+v).toFixed(2)).join(", ");
        if (typeof value === "boolean" || value === "true" || value === "false") return value === true || value === "true" ? "✓" : "✗";
        if (typeof value === "number") return String(+value.toFixed(3));
        const text = String(value);
        if (text.includes("/")) return text.split("/").pop().replace(/\.[a-z]+$/, "");
        // A choice from a list, as words ("NoRepeats" as "No Repeats"), as the editor shows it.
        return /^[A-Za-z]+$/.test(text) ? text.replace(/([a-z])([A-Z])/g, "$1 $2") : text;
    }

    /**
     * A node's repeated inputs ("Sound 1", "Sound 2"...), as the editor shows them: as many as it has linked, and one
     * more. An audio graph's Random takes any number of sounds this way.
     */
    function repeatedInputs(graph, node, type) {
        if (!type.repeated) return [];
        const prefix = type.repeated.name + " ";
        const linked = (graph.links ?? []).filter(l => l.to === node.id && l.in.startsWith(prefix)).map(l => +l.in.slice(prefix.length) || 0);
        const count = Math.max(type.repeated.atLeast ?? 2, Math.max(0, ...linked) + 1);
        return Array.from({ length: count }, (_, i) => ({ ...type.repeated, name: prefix + (i + 1) }));
    }

    /** Where each node and port goes: rows of inputs on the left, outputs on the right. */
    function layout(graph, kind) {
        const placed = new Map();
        for (const node of graph.nodes) {
            const type = find(kind, node.type) ?? { title: node.type.split("/").pop(), category: node.type.split("/")[0], inputs: [], outputs: [], description: "" };
            const values = node.values ?? {};
            let title = type.title;
            if (/^Variables\/(Get|Set) Variable$/.test(node.type) && values.Variable) title = (node.type.includes("Get") ? "Get " : "Set ") + values.Variable;
            if (/^Variables\/(Get|Set) Property$/.test(node.type) && values.Property) title = (node.type.includes("Get") ? "Get " : "Set ") + values.Property;
            if (node.type === "Properties/Get Property" && values.Variable) title = "Get " + values.Variable;

            const inputs = type.inputs.filter(p => !p.variable).concat(repeatedInputs(graph, node, type));
            const rows = Math.max(inputs.length, type.outputs.length, 1);
            const longest = Math.max(title.length * 7.2 + 24, ...inputs.map(p => p.name.length * 6 + show(values[p.name] ?? p.default).length * 5.6 + 40));
            const w = Math.max(width, Math.min(longest, 280));
            placed.set(node.id, { node, type, title, inputs, values, x: node.x, y: node.y, w, h: header + rows * row + pad });
        }

        return placed;
    }

    function portY(box, index) {
        return box.y + header + index * row + row / 2 + 3;
    }

    function wirePath(x1, y1, x2, y2) {
        const dx = Math.max(40, Math.abs(x2 - x1) * 0.5);
        return `M${x1},${y1} C${x1 + dx},${y1} ${x2 - dx},${y2} ${x2},${y2}`;
    }

    function draw(svg, graph, kind, view) {
        const world = el("g", {}, svg);
        const wires = el("g", {}, world);
        const pulses = el("g", {}, world);
        const boxes = layout(graph, kind);
        const nodeEls = new Map();
        const linked = new Set((graph.links ?? []).map(l => `${l.to}:${l.in}`));
        const categories = nodes()?.libraries[kind]?.categories ?? {};

        for (const box of boxes.values()) {
            const g = el("g", { class: "g-node", transform: `translate(${box.x},${box.y})` }, world);
            nodeEls.set(box.node.id, g);
            el("rect", { class: "body", width: box.w, height: box.h, rx: 7 }, g);
            const head = categories[box.type.category] ?? "#555b6e";
            el("path", { d: `M0,7 a7,7 0 0 1 7,-7 h${box.w - 14} a7,7 0 0 1 7,7 v${header - 7} h${-box.w} z`, fill: head }, g);
            const title = el("text", { class: "title", x: 10, y: 16 }, g);
            title.textContent = box.title;

            box.inputs.forEach((port, i) => {
                const y = portY(box, i) - box.y;
                const isLinked = linked.has(`${box.node.id}:${port.name}`);
                if (port.setting) {
                    const t = el("text", { class: "value", x: 10, y: y + 4 }, g);
                    t.textContent = `${port.name}  ${show(box.values[port.name] ?? port.default)}`;
                    return;
                }

                if (port.type === "Flow") el("path", { d: `M-4,${y - 5} l8,5 l-8,5 z`, fill: "#e9e9ee" }, g);
                else el("circle", { cx: 0, cy: y, r: 4.2, fill: isLinked ? color(port.type) : "#22252e", stroke: color(port.type), "stroke-width": 2 }, g);
                const label = el("text", { x: 10, y: y + 4 }, g);
                label.textContent = port.name === "In" ? "" : port.name;
                if (!isLinked && port.type !== "Flow") {
                    const value = show(box.values[port.name] ?? port.default);
                    if (value !== "") {
                        const v = el("text", { class: "value", x: 16 + port.name.length * 6, y: y + 4 }, g);
                        v.textContent = port.type === "Object" && value === "" ? "Self" : value;
                    } else if (port.type === "Object") {
                        const v = el("text", { class: "value", x: 16 + port.name.length * 6, y: y + 4 }, g);
                        v.textContent = "Self";
                    }
                }
            });

            box.type.outputs.forEach((port, i) => {
                const y = portY(box, i) - box.y;
                if (port.type === "Flow") el("path", { d: `M${box.w - 4},${y - 5} l8,5 l-8,5 z`, fill: "#e9e9ee" }, g);
                else el("circle", { cx: box.w, cy: y, r: 4.2, fill: color(port.type), stroke: color(port.type), "stroke-width": 2 }, g);
                const label = el("text", { x: box.w - 10, y: y + 4, "text-anchor": "end" }, g);
                label.textContent = port.name === "Then" && box.type.outputs.filter(o => o.type === "Flow").length === 1 ? "" : port.name;
            });

            g.addEventListener("pointerenter", () => view.hover(box, g));
            g.addEventListener("pointerleave", () => view.hover(null, g));
        }

        const flowLinks = [], links = [];
        for (const link of graph.links ?? []) {
            const from = boxes.get(link.from), to = boxes.get(link.to);
            if (!from || !to) continue;
            const outIndex = from.type.outputs.findIndex(p => p.name === link.out);
            const inIndex = to.inputs.findIndex(p => p.name === link.in);
            if (outIndex < 0 || inIndex < 0) continue;
            const type = from.type.outputs[outIndex].type;
            const d = wirePath(from.x + from.w + (type === "Flow" ? 4 : 0), portY(from, outIndex), to.x - (type === "Flow" ? 4 : 0), portY(to, inIndex));
            const path = el("path", { d, class: "g-wire" + (type === "Flow" ? " flow" : ""), stroke: type === "Flow" ? "#e9e9ee" : color(type) }, wires);
            if (type === "Flow") flowLinks.push({ link, path });
            links.push({ link, path });
        }

        let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
        for (const b of boxes.values()) {
            minX = Math.min(minX, b.x); minY = Math.min(minY, b.y);
            maxX = Math.max(maxX, b.x + b.w); maxY = Math.max(maxY, b.y + b.h);
        }

        return { world, pulses, boxes, nodeEls, flowLinks, links, bounds: { minX, minY, maxX, maxY } };
    }

    /** Puts a graph in an element: by name from the samples (window.VertexGraphs), or as its JSON. */
    async function mount(container, graphOrName, options = {}) {
        if (!window.VertexNodes) await Site.load("data/nodes.js");
        if (!window.VertexGraphs) await Site.load("data/graphs.js");
        const graph = typeof graphOrName === "string" ? window.VertexGraphs?.[graphOrName] : graphOrName;
        if (!graph) { container.textContent = `No graph called ${graphOrName}`; return; }

        const kind = graph.kind ?? "script";
        container.classList.add("graph-view");
        container.innerHTML = "";
        const svg = el("svg", {}, container);
        const tip = document.createElement("div");
        tip.className = "graph-tip";
        container.append(tip);

        const name = typeof graphOrName === "string" ? graphOrName : options.title ?? "Graph";
        const bar = document.createElement("div");
        bar.className = "graph-bar";
        const kindName = { vfx: "VFX graph", audio: "Audio graph" }[kind] ?? "Visual script";
        const runButton = kind === "script" ? '<button class="run" title="Send the flow along the wires, as the script runs">▶ Run</button>'
            : kind === "audio" ? '<button class="run" title="Work it out as the game would (a different choice each time), with tones standing in for the sound files">▶ Play</button>' : "";
        bar.innerHTML = `<div class="graph-title">${Site.escape(name)}<span>${kindName} · ${graph.nodes.length} nodes</span></div><div class="spacer"></div>
            ${runButton}<button class="fit" title="Fit the graph in view">Fit</button>`;
        if (!options.bare) container.append(bar);

        if (graph.variables?.length && !options.bare) {
            const vars = document.createElement("div");
            vars.className = "graph-vars";
            vars.innerHTML = graph.variables.map(v => `<span><i style="background:${color(v.type)}"></i>${Site.escape(v.name)} <small style="color:#8a92a3">${show(v.value) || v.type}</small></span>`).join("");
            container.append(vars);
        }

        let view = { x: 0, y: 0, k: 1 };
        const apply = () => drawn.world.setAttribute("transform", `translate(${view.x},${view.y}) scale(${view.k})`);
        const viewHandlers = {
            hover(box, g) {
                g.classList.toggle("hot", !!box);
                if (!box) { tip.classList.remove("shown"); return; }
                const ins = box.type.inputs.filter(p => p.type !== "Flow" && !p.setting && !p.variable).map(p => p.name);
                const outs = box.type.outputs.filter(p => p.type !== "Flow").map(p => p.name);
                tip.innerHTML = `<b>${Site.escape(box.title)}</b>${Site.escape(box.type.description || "")}<br><small>${box.type.category}${ins.length ? " · in: " + ins.join(", ") : ""}${outs.length ? " · out: " + outs.join(", ") : ""}</small>`;
                const r = g.getBoundingClientRect(), c = container.getBoundingClientRect();
                tip.style.left = Math.min(r.left - c.left, c.width - 310) + "px";
                tip.style.top = (r.bottom - c.top + 8 > c.height - 90 ? r.top - c.top - 90 : r.bottom - c.top + 8) + "px";
                tip.classList.add("shown");
            }
        };
        const drawn = draw(svg, graph, kind, viewHandlers);

        const fit = () => {
            const { minX, minY, maxX, maxY } = drawn.bounds;
            const w = container.clientWidth, h = container.clientHeight;
            const top = options.bare ? 20 : 60, bottom = graph.variables?.length && !options.bare ? 50 : 20;
            const whole = Math.min(1.1, (w - 40) / (maxX - minX), (h - top - bottom) / (maxY - minY));
            // Too big to read whole: shown readably from its top-left, where its events start, to pan round.
            view.k = whole < 0.5 && !options.whole ? 0.5 : whole;
            view.x = view.k > whole ? 20 - minX * view.k : (w - (maxX - minX) * view.k) / 2 - minX * view.k;
            view.y = view.k > whole ? top - minY * view.k : top + (h - top - bottom - (maxY - minY) * view.k) / 2 - minY * view.k;
            apply();
        };

        fit();
        new ResizeObserver(fit).observe(container);
        bar.querySelector(".fit")?.addEventListener("click", fit);

        // Dragging pans, the wheel zooms round the mouse.
        let drag = null;
        container.addEventListener("pointerdown", e => {
            if (e.target.closest("button")) return;
            drag = { x: e.clientX, y: e.clientY, vx: view.x, vy: view.y };
            container.setPointerCapture(e.pointerId);
            container.classList.add("dragging");
        });
        container.addEventListener("pointermove", e => {
            if (!drag) return;
            view.x = drag.vx + e.clientX - drag.x;
            view.y = drag.vy + e.clientY - drag.y;
            apply();
        });
        container.addEventListener("pointerup", () => { drag = null; container.classList.remove("dragging"); });
        container.addEventListener("wheel", e => {
            if (options.bare) return;
            e.preventDefault();
            const r = container.getBoundingClientRect();
            const mx = e.clientX - r.left, my = e.clientY - r.top;
            const k = Math.min(2, Math.max(0.2, view.k * Math.exp(-e.deltaY * 0.0015)));
            view.x = mx - (mx - view.x) * k / view.k;
            view.y = my - (my - view.y) * k / view.k;
            view.k = k;
            apply();
        }, { passive: false });

        const run = () => kind === "audio" ? playAudio(drawn, graph) : runFlow(drawn, graph);
        bar.querySelector(".run")?.addEventListener("click", run);
        if (options.autoplay) {
            const loop = () => { if (container.isConnected) run().then(() => setTimeout(loop, 900)); };
            setTimeout(loop, 600);
        }

        return { fit, run };
    }

    /** Pulses along each flow wire in the order the script runs them, lighting each node as the flow reaches it. */
    async function runFlow(drawn, graph) {
        const byFrom = new Map();
        for (const f of drawn.flowLinks) {
            const key = f.link.from;
            if (!byFrom.has(key)) byFrom.set(key, []);
            byFrom.get(key).push(f);
        }

        // Outputs in the order the node's written: Then 1 before Then 2, True before False.
        for (const list of byFrom.values()) {
            list.sort((a, b) => {
                const outs = drawn.boxes.get(a.link.from).type.outputs.map(p => p.name);
                return outs.indexOf(a.link.out) - outs.indexOf(b.link.out);
            });
        }

        const starts = graph.nodes.filter(n => n.type.startsWith("Events/")).map(n => n.id);
        const light = id => {
            const g = drawn.nodeEls.get(id);
            if (!g) return;
            g.classList.add("lit");
            setTimeout(() => g.classList.remove("lit"), 700);
        };

        const pulse = path => new Promise(resolve => {
            const length = path.getTotalLength();
            const p = el("path", { d: path.getAttribute("d"), class: "g-pulse", "stroke-dasharray": `26 ${length + 30}` }, drawn.pulses);
            const start = performance.now(), duration = Math.min(700, 260 + length * 0.9);
            const step = now => {
                const t = Math.min(1, (now - start) / duration);
                p.setAttribute("stroke-dashoffset", String(26 - (length + 26) * (t * (2 - t))));
                if (t < 1) requestAnimationFrame(step);
                else { p.remove(); resolve(); }
            };
            requestAnimationFrame(step);
        });

        const walk = async (id, seen) => {
            if (seen.has(id)) return;
            seen.add(id);
            light(id);
            for (const f of byFrom.get(id) ?? []) {
                await pulse(f.path);
                await walk(f.link.to, seen);
            }
        };

        await Promise.all(starts.map(id => walk(id, new Set())));
    }

    // ---------------------------------------------------------------- Audio graphs

    let audio = null;
    const lastChoice = new Map();

    /**
     * Plays an audio graph as the engine works it out: from the Output back, a Random choosing one of its sounds (not
     * the last one again), a pitch picked between Min and Max, a sequence one after another. Each node lights up and its
     * wire pulses as it starts, and a tone stands in for each sound file (its note from its name, at its pitch). A loop
     * that would go on until stopped goes round four times here, with its gaps a quarter as long, to keep it short.
     */
    function playAudio(drawn, graph) {
        audio ??= new (window.AudioContext || window.webkitAudioContext)();
        audio.resume?.();
        const begin = audio.currentTime + 0.06;
        const byId = new Map(graph.nodes.map(n => [n.id, n]));
        const into = (node, port) => drawn.links.find(l => l.link.to === node.id && l.link.in === port);
        const defaultOf = (node, port) => {
            const p = find("audio", node.type)?.inputs.find(i => i.name === port);
            return p?.default != null ? +p.default : 0;
        };
        let end = begin;

        // When something starts: light its node, and pulse the wire it goes along.
        const at = (time, id, wire) => setTimeout(() => {
            const g = drawn.nodeEls.get(id);
            if (g) { g.classList.add("lit"); setTimeout(() => g.classList.remove("lit"), 600); }
            if (wire) pulseAlong(drawn, wire.path);
        }, Math.max(0, (time - audio.currentTime) * 1000));

        const number = (node, port, time) => {
            const wire = into(node, port);
            if (!wire) return node.values?.[port] != null ? +node.values[port] : defaultOf(node, port);
            const from = byId.get(wire.link.from);
            at(time, from.id, wire);
            switch (from.type) {
                case "Math/Random Number": { const min = number(from, "Min", time), max = number(from, "Max", time); return min + Math.random() * (max - min); }
                case "Math/Add": return number(from, "A", time) + number(from, "B", time);
                case "Math/Multiply": return number(from, "A", time) * number(from, "B", time);
                case "Properties/Get Property": return +(graph.variables?.find(v => v.name === from.values?.Variable)?.value ?? 0);
                default: return 0;
            }
        };

        const repeated = node => (graph.links ?? []).filter(l => l.to === node.id && /^Sound \d+$/.test(l.in))
            .map(l => +l.in.slice(6)).sort((a, b) => a - b);

        // Plays what's linked into a node's input from `time`, with the volume and pitch from the nodes it's gone
        // through; returns when it ends.
        const input = (node, port, time, volume, pitch) => {
            const wire = into(node, port);
            if (!wire) return time;
            at(time, wire.link.from, wire);
            return play(byId.get(wire.link.from), time, volume, pitch);
        };

        const play = (node, time, volume, pitch) => {
            switch (node.type) {
                case "Sounds/Sound": {
                    const length = 0.32 / pitch;
                    tone(String(node.values?.Sound ?? ""), time, pitch, volume * (node.values?.Volume ?? 1), length);
                    return time + length;
                }
                case "Choose/Random": {
                    const choices = repeated(node);
                    if (!choices.length) return time;
                    const mode = node.values?.Mode ?? "NoRepeats", last = lastChoice.get(node);
                    let pick = choices[Math.floor(Math.random() * choices.length)];
                    if (mode === "InTurn") pick = choices.find(c => c > (last ?? 0)) ?? choices[0];
                    else if (mode !== "Random") while (choices.length > 1 && pick === last) pick = choices[Math.floor(Math.random() * choices.length)];
                    lastChoice.set(node, pick);
                    return input(node, `Sound ${pick}`, time, volume, pitch);
                }
                case "Choose/Switch": {
                    const choices = repeated(node);
                    if (!choices.length) return time;
                    const which = Math.min(Math.max(Math.round(number(node, "Which", time)), 1), choices[choices.length - 1]);
                    return input(node, `Sound ${which}`, time, volume, pitch);
                }
                case "Choose/Chance": return Math.random() < number(node, "Chance", time) ? input(node, "Sound", time, volume, pitch) : time;
                case "Combine/Sequence": {
                    const gap = Math.max(0, number(node, "Gap", time));
                    let t = time;
                    repeated(node).forEach((i, n) => { t = input(node, `Sound ${i}`, t + (n ? gap : 0), volume, pitch); });
                    return t;
                }
                case "Combine/Layer": return Math.max(time, ...repeated(node).map(i => input(node, `Sound ${i}`, time, volume, pitch)));
                case "Timing/Delay": return input(node, "Sound", time + Math.max(0, number(node, "Seconds", time)), volume, pitch);
                case "Timing/Loop": {
                    const times = Math.round(number(node, "Times", time)) || 4;
                    let t = time;
                    for (let i = 0; i < times; i++) {
                        const ended = input(node, "Sound", t, volume, pitch);
                        t = Math.max(ended, t + 0.05) + Math.max(0, number(node, "Gap", ended)) * 0.25;
                    }
                    return t;
                }
                case "Change/Volume": return input(node, "Sound", time, volume * number(node, "Volume", time), pitch);
                case "Change/Pitch": return input(node, "Sound", time, volume, pitch * number(node, "Pitch", time));
                case "Change/Random Pitch": { const min = number(node, "Min", time), max = number(node, "Max", time); return input(node, "Sound", time, volume, pitch * (min + Math.random() * (max - min))); }
                case "Change/Random Volume": { const min = number(node, "Min", time), max = number(node, "Max", time); return input(node, "Sound", time, volume * (min + Math.random() * (max - min)), pitch); }
                case "Change/Fade In": return input(node, "Sound", time, volume, pitch);
                default: return time;
            }
        };

        const output = graph.nodes.find(n => n.type === "Output/Output");
        if (output) {
            at(begin, output.id);
            end = input(output, "Sound", begin, 1, 1);
        }

        return new Promise(resolve => setTimeout(resolve, Math.max(0, (end - audio.currentTime) * 1000)));
    }

    /** A tone standing in for a sound file: its note from its name (Step 1 and Step 2 differ), shaped like a pluck. */
    function tone(name, time, pitch, volume, length) {
        const notes = [262, 294, 330, 392, 440, 523, 587, 659];
        let hash = 0;
        for (const c of name) hash = (hash * 31 + c.charCodeAt(0)) >>> 0;
        const low = /thunder|body|rain|thud|echo/i.test(name);
        const oscillator = audio.createOscillator(), gain = audio.createGain();
        oscillator.type = low ? "sawtooth" : "triangle";
        oscillator.frequency.value = notes[hash % notes.length] * (low ? 0.25 : 1) * pitch;
        gain.gain.setValueAtTime(0.0001, time);
        gain.gain.exponentialRampToValueAtTime(0.0001 + 0.2 * Math.min(volume, 2), time + 0.01);
        gain.gain.exponentialRampToValueAtTime(0.0001, time + length);
        oscillator.connect(gain).connect(audio.destination);
        oscillator.start(time);
        oscillator.stop(time + length + 0.05);
    }

    /** A pulse running along a wire, from its output to its input. */
    function pulseAlong(drawn, path) {
        const length = path.getTotalLength();
        const p = el("path", { d: path.getAttribute("d"), class: "g-pulse", "stroke-dasharray": `26 ${length + 30}` }, drawn.pulses);
        const start = performance.now(), duration = Math.min(600, 220 + length * 0.8);
        const step = now => {
            const t = Math.min(1, (now - start) / duration);
            p.setAttribute("stroke-dashoffset", String(26 - (length + 26) * (t * (2 - t))));
            if (t < 1) requestAnimationFrame(step);
            else p.remove();
        };
        requestAnimationFrame(step);
    }

    /** One node on its own, as HTML: its header in its category's colour, its inputs and outputs. */
    function nodeCard(kind, node) {
        const categories = nodes().libraries[kind].categories;
        const port = (p, out) => {
            const dot = p.type === "Flow" ? '<i class="flow"></i>' : `<i class="${p.setting ? "set" : ""}" style="color:${color(p.type)}${out ? ";background:" + color(p.type) : ""}"></i>`;
            const value = !out && p.default != null && p.default !== "" && p.type !== "Flow" ? `<em>${Site.escape(show(p.default))}</em>` : "";
            const name = (p.name === "In" || p.name === "Then") && p.type === "Flow" ? "" : Site.escape(p.name);
            return out ? `<div class="port out">${name}${dot}</div>` : `<div class="port">${dot}${name}${value}</div>`;
        };

        // A repeated input shows as its first two, and more to come.
        const inputs = node.inputs.filter(p => !p.variable).concat(node.repeated
            ? [1, 2, "…"].map(n => ({ ...node.repeated, name: `${node.repeated.name} ${n}` }))
            : []);
        const rows = Math.max(inputs.length, node.outputs.length);
        let html = "";
        for (let i = 0; i < rows; i++) {
            html += inputs[i] ? port(inputs[i], false) : "<div></div>";
            html += node.outputs[i] ? port(node.outputs[i], true) : "<div></div>";
        }

        return `<div class="node-card"><div class="head" style="background:${categories[node.category] ?? "#555b6e"}">${Site.escape(node.title)}</div><div class="rows">${html}</div></div>`;
    }

    return { mount, nodeCard, color, show };
})();
