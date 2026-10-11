// Animates the logo (Components/Shared/BrandMark.razor). Six motions, picked at random for each
// play and never the same twice in a row:
//   gather  - all dots slide into the center, it flashes, and everything springs back out;
//   chain   - light jumps from the center to one dot and spreads through the network along the lines;
//   scatter - the dots are pushed outward, the lines stretch, and everything snaps back;
//   redraw  - the lines vanish and draw themselves again from the center, lighting each dot they reach;
//   rim     - the yellow dot leaves the center, runs once around the outer ring and returns;
//   pulse   - the center swells while a wave of light passes out to the other dots.
// The header and sign-in logos play one motion when hovered or focused; a mark with
// data-motion="once" (the sign-in page) plays one shortly after it appears, and one with
// data-motion="loop" (the busy indicator) keeps playing for as long as it's on the page. Nothing
// moves when the user asks for reduced motion. Only attributes of existing elements change, and
// every one of them is put back when a play ends.
(() => {
    const LOOP_PAUSE = 600; // ms between plays in a loop
    const reduced = matchMedia("(prefers-reduced-motion: reduce)");
    const marks = new Map(); // svg -> state, for the marks playing right now
    let lastMotion = null;
    let frameRequested = false;

    const clamp = x => Math.max(0, Math.min(1, x));
    const easeInOut = t => t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2;
    const easeOut = t => 1 - Math.pow(1 - t, 3);
    const easeIn = t => t * t * t;
    // Overshoots a little before settling.
    const backOut = t => 1 + 2.4 * Math.pow(t - 1, 3) + 1.4 * Math.pow(t - 1, 2);
    // 1 at "at", fading to 0 over "width"; 0 before and after.
    const bump = (t, at, width) => { const d = (t - at) / width; return d < 0 || d > 1 ? 0 : 1 - d; };
    const dist = (a, b) => Math.hypot(a[0] - b[0], a[1] - b[1]);

    // Each motion fills a frame f for time t (0..1): scale (the network's size around the hub),
    // glow and opacity per node, how lit, visible and far drawn each line is, and the yellow
    // dot's position and swell.
    const MOTIONS = {
        gather: {
            dur: 1700,
            draw(t, net, f) {
                if (t < 0.36) f.scale = 1 - 0.9 * easeIn(t / 0.36);
                else if (t < 0.5) f.scale = 0.1;
                else f.scale = 0.1 + 0.9 * backOut(clamp((t - 0.5) / 0.4));
                f.swell = bump(t, 0.34, 0.4) * 0.5;
                f.glow.fill(bump(t, 0.78, 0.22) * 0.9);
            },
        },
        chain: {
            dur: 2100,
            draw(t, net, f) {
                const step = 0.62 / net.maxWave;
                f.swell = bump(t, 0, 0.2) * 0.3;
                net.nodes.forEach((_, n) => { if (n !== net.hub) f.glow[n] = bump(t, 0.06 + net.wave[n] * step, 0.3); });
                net.edges.forEach(([a, b], e) => {
                    // A line lights while the wave crosses it; a line between dots of the same step lights with them.
                    const from = Math.min(net.wave[a], net.wave[b]), to = Math.max(net.wave[a], net.wave[b]);
                    f.lineLit[e] = bump(t, 0.06 + (to === from ? to : from) * step, 0.26);
                });
            },
        },
        scatter: {
            dur: 1400,
            draw(t, net, f) {
                if (t < 0.22) f.scale = 1 + 0.32 * easeOut(t / 0.22);
                else { const u = (t - 0.22) / 0.78; f.scale = 1 + 0.32 * Math.exp(-5.5 * u) * Math.cos(10 * u); }
                f.swell = bump(t, 0, 0.3) * 0.45;
                f.glow.fill(bump(t, 0.14, 0.4) * 0.85);
            },
        },
        redraw: {
            dur: 2400,
            draw(t, net, f) {
                const fade = clamp(t / 0.12);
                const span = 0.6 / (net.maxDepth + 1);
                net.nodes.forEach((_, n) => {
                    if (n === net.hub) return;
                    const reached = 0.16 + net.depth[n] * span;
                    f.nodeOpacity[n] = t < reached ? 1 - 0.72 * fade : 1;
                    f.glow[n] = bump(t, reached, 0.25);
                });
                net.edges.forEach(([a, b], e) => {
                    // Drawn from the end nearer the hub, once the redraw has reached that end.
                    const start = 0.16 + Math.min(net.depth[a], net.depth[b]) * span;
                    f.lineFrom[e] = net.depth[a] <= net.depth[b] ? a : b;
                    f.lineGrow[e] = t < start ? 0 : easeInOut(clamp((t - start) / span));
                    f.lineOpacity[e] = t < 0.12 ? 1 - fade : t < start ? 0 : 1;
                });
                f.swell = bump(t, 0.1, 0.25) * 0.35;
            },
        },
        rim: {
            dur: 2400,
            draw(t, net, f) {
                const d = t * net.lapLength;
                let acc = 0;
                for (let i = 0; i < net.lapLegs.length; i++) {
                    const leg = net.lapLegs[i];
                    if (i < net.lapLegs.length - 1)
                        f.glow[net.lap[i + 1]] = Math.max(f.glow[net.lap[i + 1]], bump(t, (acc + leg) / net.lapLength, 0.16));
                    if (d >= acc && d <= acc + leg) {
                        const a = net.nodes[net.lap[i]], b = net.nodes[net.lap[i + 1]], k = (d - acc) / leg;
                        f.dot = [a[0] + (b[0] - a[0]) * k, a[1] + (b[1] - a[1]) * k];
                    }
                    acc += leg;
                }
            },
        },
        pulse: {
            dur: 1500,
            draw(t, net, f) {
                const hub = net.nodes[net.hub];
                f.swell = Math.sin(Math.PI * clamp(t / 0.55)) * 0.28;
                net.nodes.forEach((p, n) => { if (n !== net.hub) f.glow[n] = bump(t, 0.18 + dist(p, hub) * 0.0093, 0.46) * 0.7; });
            },
        },
    };

    function pickMotion() {
        const options = Object.keys(MOTIONS).filter(m => m !== lastMotion);
        lastMotion = options[Math.floor(Math.random() * options.length)];
        return lastMotion;
    }

    // Steps from "start" to every node along the lines, optionally without passing "skip".
    function steps(nodes, edges, start, skip) {
        const d = nodes.map(() => Infinity);
        d[start] = 0;
        const queue = [start];
        while (queue.length) {
            const n = queue.shift();
            for (const [a, b] of edges) {
                const o = a === n ? b : b === n ? a : -1;
                if (o < 0 || o === skip || d[o] <= d[n] + 1) continue;
                d[o] = d[n] + 1;
                queue.push(o);
            }
        }
        return d;
    }

    // The network as drawn: node positions, lines, hub, rim and the elements to animate.
    function read(svg) {
        const dot = svg.querySelector(".bm-dot");
        const halo = svg.querySelector(".bm-halo");
        if (!dot || !halo) return null;
        const nodes = [], base = [], glows = [];
        for (const c of svg.querySelectorAll("circle[data-node]")) {
            const i = +c.dataset.node;
            if (c.classList.contains("bm-glow")) { glows[i] = c; continue; }
            nodes[i] = [+c.getAttribute("cx"), +c.getAttribute("cy")];
            if (c !== dot) base[i] = c;
        }
        const lines = [...svg.querySelectorAll("line[data-a]")];
        const edges = lines.map(l => [+l.dataset.a, +l.dataset.b]);
        const hub = +dot.dataset.node;
        const rim = (svg.dataset.rim || "").split(",").filter(Boolean).map(Number);
        const entry = +svg.dataset.entry;

        // depth: steps from the hub. wave: steps from the hub when the light first jumps to the
        // entry node and then spreads without passing the hub again.
        const depth = steps(nodes, edges, hub, -1);
        const wave = steps(nodes, edges, entry, hub).map(d => d + 1);
        wave[hub] = 0;

        // The lap: hub, around the rim from the entry node and back to it, hub.
        const at = Math.max(0, rim.indexOf(entry));
        const lap = [hub, ...rim.slice(at), ...rim.slice(0, at), entry, hub];
        const lapLegs = lap.slice(1).map((n, i) => dist(nodes[n], nodes[lap[i]]));

        return {
            nodes, edges, lines, base, glows, dot, halo, hub, depth, wave, lap, lapLegs,
            maxDepth: Math.max(...depth.filter(isFinite)),
            maxWave: Math.max(...wave.filter(isFinite)),
            lapLength: lapLegs.reduce((a, b) => a + b, 0),
            dotR: +dot.getAttribute("r"),
            haloR: +halo.getAttribute("r"),
        };
    }

    function play(svg, loop) {
        if (reduced.matches || marks.has(svg)) return;
        const net = read(svg);
        if (!net) return;
        marks.set(svg, { net, loop, motion: pickMotion(), t0: null });
        requestFrame();
    }

    // Draws the network at time t of a motion; without a motion, as it stands at rest.
    function draw(net, motion, t) {
        const hub = net.nodes[net.hub], count = net.nodes.length;
        const f = {
            scale: 1, swell: 0, dot: hub, glow: new Array(count).fill(0), nodeOpacity: new Array(count).fill(1),
            lineLit: [], lineGrow: [], lineFrom: [], lineOpacity: [],
        };
        if (motion) MOTIONS[motion].draw(t, net, f);

        const pos = net.nodes.map(p => [hub[0] + (p[0] - hub[0]) * f.scale, hub[1] + (p[1] - hub[1]) * f.scale]);
        net.nodes.forEach((_, i) => {
            if (i === net.hub) return;
            for (const c of [net.base[i], net.glows[i]]) {
                c.setAttribute("cx", pos[i][0].toFixed(2));
                c.setAttribute("cy", pos[i][1].toFixed(2));
            }
            if (f.nodeOpacity[i] < 1) net.base[i].setAttribute("opacity", f.nodeOpacity[i].toFixed(3));
            else net.base[i].removeAttribute("opacity");
            net.glows[i].setAttribute("opacity", clamp(f.glow[i]).toFixed(3));
        });
        net.edges.forEach(([a, b], e) => {
            const from = f.lineFrom[e] === b ? b : a, to = from === a ? b : a;
            const grow = f.lineGrow[e] ?? 1;
            const line = net.lines[e];
            // At rest a line runs a to b, as rendered.
            const [p, q] = motion ? [pos[from], pos[to]] : [pos[a], pos[b]];
            line.setAttribute("x1", p[0].toFixed(2));
            line.setAttribute("y1", p[1].toFixed(2));
            line.setAttribute("x2", (p[0] + (q[0] - p[0]) * grow).toFixed(2));
            line.setAttribute("y2", (p[1] + (q[1] - p[1]) * grow).toFixed(2));
            const lit = f.lineLit[e] || 0;
            const opacity = f.lineOpacity[e] ?? 1;
            // Stroke and its opacity come from the group; they're only set while a line differs.
            if (lit > 0.02) {
                line.setAttribute("stroke", "var(--brand-lit)");
                line.setAttribute("stroke-opacity", (0.75 + 0.25 * lit).toFixed(3));
            } else {
                line.removeAttribute("stroke");
                if (opacity < 1) line.setAttribute("stroke-opacity", (0.75 * opacity).toFixed(3));
                else line.removeAttribute("stroke-opacity");
            }
        });

        // Away from the hub the dot shrinks, so it reads as traveling rather than the whole node moving.
        const away = Math.min(1, dist(f.dot, hub) / 10);
        net.dot.setAttribute("cx", f.dot[0].toFixed(2));
        net.dot.setAttribute("cy", f.dot[1].toFixed(2));
        net.dot.setAttribute("r", (net.dotR * (1 + f.swell) * (1 - 0.35 * away)).toFixed(2));
        net.halo.setAttribute("r", (net.haloR * (1 + 0.9 * f.swell)).toFixed(2));
        net.halo.setAttribute("fill-opacity", (0.25 - 0.15 * away + 0.5 * f.swell).toFixed(3));
    }

    function rest(svg, s) {
        draw(s.net, null, 0);
        s.net.halo.setAttribute("fill-opacity", ".25");
        marks.delete(svg);
    }

    function requestFrame() {
        if (!frameRequested) {
            frameRequested = true;
            requestAnimationFrame(frame);
        }
    }

    function frame(now) {
        frameRequested = false;
        for (const [svg, s] of marks) {
            if (!svg.isConnected || reduced.matches) { rest(svg, s); continue; }
            s.t0 ??= now;
            const dur = MOTIONS[s.motion].dur;
            let elapsed = now - s.t0;
            if (elapsed >= dur) {
                // A single play ends here; a loop rests briefly, then plays another motion.
                if (!s.loop) { rest(svg, s); continue; }
                if (elapsed >= dur + LOOP_PAUSE) {
                    s.motion = pickMotion();
                    s.t0 = now;
                    elapsed = 0;
                } else {
                    draw(s.net, null, 0);
                    continue;
                }
            }
            draw(s.net, s.motion, elapsed / MOTIONS[s.motion].dur);
        }
        if (marks.size > 0) requestFrame();
    }

    // Header logo: one play per hover or keyboard focus.
    const onEnter = e => {
        const brand = e.target.closest?.(".brand, .auth-brand");
        // Moving between the logo's own parts isn't a new hover.
        if (!brand || (e.relatedTarget && brand.contains(e.relatedTarget))) return;
        const svg = brand.querySelector("svg.brand-mark");
        if (svg) play(svg, false);
    };
    document.addEventListener("mouseover", onEnter);
    document.addEventListener("focusin", onEnter);

    // Busy indicators and the sign-in logo come and go as Blazor renders; start each one that
    // appears. A "once" logo plays a single time per element, after a short pause.
    const welcomed = new WeakSet();
    const startLoops = () => {
        for (const svg of document.querySelectorAll('svg.brand-mark[data-motion="loop"]')) play(svg, true);
        for (const svg of document.querySelectorAll('svg.brand-mark[data-motion="once"]')) {
            if (welcomed.has(svg)) continue;
            welcomed.add(svg);
            setTimeout(() => { if (svg.isConnected) play(svg, false); }, 500);
        }
    };
    new MutationObserver(startLoops).observe(document.documentElement, { childList: true, subtree: true });
    reduced.addEventListener?.("change", () => { if (!reduced.matches) startLoops(); else requestFrame(); });
    startLoops();
})();
