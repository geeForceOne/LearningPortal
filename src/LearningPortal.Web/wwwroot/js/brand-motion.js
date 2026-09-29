// Animates the yellow dot in the logo (Components/Shared/BrandMark.razor). Three motions, picked
// at random for each play and never the same twice in a row:
//   trip  - the dot travels out to a neighbouring node, lights it up and comes back;
//   rim   - it leaves the hub, runs once around the outer ring and returns;
//   pulse - it swells in place while a wave of light passes out to the other nodes.
// The header and sign-in logos play one motion when hovered or focused; a mark with
// data-motion="once" (the sign-in page) plays one shortly after it appears, and one with
// data-motion="loop" (the busy indicator) keeps playing for as long as it's on the page. Nothing
// moves when the user asks for reduced motion.
(() => {
    const MOTIONS = ["trip", "rim", "pulse"];
    const reduced = matchMedia("(prefers-reduced-motion: reduce)");
    const marks = new Map(); // svg -> state, for the marks playing right now
    let lastMotion = null;
    let frameRequested = false;

    const ease = t => t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2;
    const dist = (a, b) => Math.hypot(a[0] - b[0], a[1] - b[1]);

    function pickMotion() {
        const options = MOTIONS.filter(m => m !== lastMotion);
        lastMotion = options[Math.floor(Math.random() * options.length)];
        return lastMotion;
    }

    // The network as drawn: node positions, edges, hub, rim and the elements to animate.
    function read(svg) {
        const dot = svg.querySelector(".bm-dot");
        const halo = svg.querySelector(".bm-halo");
        if (!dot || !halo) return null;
        const nodes = [];
        const glows = [];
        for (const c of svg.querySelectorAll("circle[data-node]")) {
            const i = +c.dataset.node;
            if (c.classList.contains("bm-glow")) glows[i] = c;
            else nodes[i] = [+c.getAttribute("cx"), +c.getAttribute("cy")];
        }
        const edges = [...svg.querySelectorAll("line[data-a]")].map(l => [+l.dataset.a, +l.dataset.b]);
        return {
            nodes, edges, glows, dot, halo,
            hub: +dot.dataset.node,
            dotR: +dot.getAttribute("r"),
            haloR: +halo.getAttribute("r"),
            rim: (svg.dataset.rim || "").split(",").filter(Boolean).map(Number),
            entry: +svg.dataset.entry,
            speed: nodes.length > 8 ? 0.075 : 0.06, // network units per ms
        };
    }

    // One play of a motion as steps: move (the dot from a to b, lighting "arrive" on arrival),
    // hold (the dot rests on a node), pulse, and pause.
    function steps(motion, net, state) {
        const P = i => net.nodes[i];
        if (motion === "trip") {
            const options = net.edges.filter(e => e.includes(net.hub))
                .map(e => e[0] === net.hub ? e[1] : e[0]).filter(n => n !== state.lastNode);
            const to = options[Math.floor(Math.random() * options.length)];
            state.lastNode = to;
            return [
                { move: [P(net.hub), P(to)], dur: 520, ease: true, arrive: to },
                { hold: to, dur: 260 },
                { move: [P(to), P(net.hub)], dur: 520, ease: true },
                { pause: true, dur: 700 },
            ];
        }
        if (motion === "rim" && net.rim.length > 2) {
            const start = net.rim.indexOf(net.entry);
            const order = [...net.rim.slice(start), ...net.rim.slice(0, start), net.entry];
            const leg = (a, b, arrive) => ({ move: [P(a), P(b)], dur: dist(P(a), P(b)) / net.speed, arrive });
            const list = [leg(net.hub, order[0], order[0])];
            for (let i = 1; i < order.length; i++) list.push(leg(order[i - 1], order[i], order[i]));
            list.push(leg(net.entry, net.hub), { pause: true, dur: 800 });
            return list;
        }
        return [{ pulse: true, dur: 1500 }, { pause: true, dur: 500 }];
    }

    function play(svg, loop) {
        if (reduced.matches || marks.has(svg)) return;
        const net = read(svg);
        if (!net) return;
        const state = { net, loop, motion: pickMotion(), flashes: new Map() };
        marks.set(svg, state);
        requestFrame();
    }

    function rest(svg, s) {
        const hub = s.net.nodes[s.net.hub];
        s.net.dot.setAttribute("cx", hub[0]);
        s.net.dot.setAttribute("cy", hub[1]);
        s.net.dot.setAttribute("r", s.net.dotR);
        s.net.halo.setAttribute("r", s.net.haloR);
        s.net.halo.setAttribute("fill-opacity", ".25");
        for (const g of s.net.glows) g?.setAttribute("opacity", "0");
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
            if (!s.steps) {
                // The pause only separates loops; a single play ends as soon as the dot is home.
                s.steps = steps(s.motion, s.net, s).filter(x => s.loop || !x.pause);
                s.i = 0;
                s.t0 = now;
            }

            let step = s.steps[s.i];
            let done = false;
            while (now - s.t0 >= step.dur) {
                s.t0 += step.dur;
                if (step.arrive !== undefined) s.flashes.set(step.arrive, s.t0);
                if (++s.i >= s.steps.length) {
                    if (!s.loop) { done = true; break; }
                    s.steps = steps(s.motion, s.net, s);
                    s.i = 0;
                }
                step = s.steps[s.i];
            }
            if (done) { rest(svg, s); continue; }

            const t = Math.min(1, (now - s.t0) / step.dur);
            const hub = s.net.nodes[s.net.hub];
            let pos = hub, swell = 0;
            if (step.move) {
                const k = step.ease ? ease(t) : t;
                const [a, b] = step.move;
                pos = [a[0] + (b[0] - a[0]) * k, a[1] + (b[1] - a[1]) * k];
            } else if (step.hold !== undefined) {
                pos = s.net.nodes[step.hold];
            } else if (step.pulse) {
                swell = Math.sin(Math.PI * Math.min(1, t / 0.55));
                if (t > 0.18 && !step.waved) {
                    step.waved = true;
                    s.net.nodes.forEach((p, n) => { if (n !== s.net.hub) s.flashes.set(n, now + dist(p, hub) * 14); });
                }
            }

            // Away from the hub the dot shrinks, so it reads as travelling rather than the whole node moving.
            const away = Math.min(1, dist(pos, hub) / 10);
            s.net.dot.setAttribute("cx", pos[0].toFixed(2));
            s.net.dot.setAttribute("cy", pos[1].toFixed(2));
            s.net.dot.setAttribute("r", (s.net.dotR * (1 + 0.28 * swell) * (1 - 0.35 * away)).toFixed(2));
            s.net.halo.setAttribute("r", (s.net.haloR * (1 + 0.25 * swell)).toFixed(2));
            s.net.halo.setAttribute("fill-opacity", (0.25 - 0.15 * away + 0.2 * swell).toFixed(3));

            const fade = s.motion === "pulse" ? 700 : s.motion === "rim" ? 450 : 650;
            const peak = s.motion === "pulse" ? 0.7 : 1;
            s.net.glows.forEach((g, n) => {
                if (!g) return;
                const start = s.flashes.get(n);
                const d = start === undefined ? Infinity : now - start;
                g.setAttribute("opacity", d < 0 || d > fade ? "0" : (peak * (1 - d / fade)).toFixed(3));
            });
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
