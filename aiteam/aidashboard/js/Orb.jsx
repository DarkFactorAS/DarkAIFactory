import { useEffect, useRef } from "react";

// Particle orb ported from ThorDashboard (web/src/components/Orb.tsx): a rotating network of
// points and lines whose motion, glow and colour follow the model state.
// States: "idle" | "working" | "waiting" | "error" | "off".

// Parameters per state; the animation eases between them.
const TARGET = {
  off: { speed: 0.02, jitter: 0, pulse: 0, glow: 0.15, alpha: 0.35, flicker: 0 },
  idle: { speed: 0.12, jitter: 0.01, pulse: 0.015, glow: 0.55, alpha: 0.85, flicker: 0.1 },
  working: { speed: 0.55, jitter: 0.05, pulse: 0.03, glow: 1, alpha: 1, flicker: 0.7 },
  waiting: { speed: 0.15, jitter: 0.01, pulse: 0.08, glow: 0.9, alpha: 1, flicker: 0.3 },
  error: { speed: 0.3, jitter: 0.08, pulse: 0.05, glow: 0.9, alpha: 1, flicker: 0.5 },
};
const STATE_RGB = {
  waiting: [240, 184, 72],
  error: [240, 96, 96],
  off: [130, 138, 150],
};

const N = 250;
// Max neighbours per point in the network.
const MAX_LINKS = 6;
// Max distance (in orb radii) for two points to be linked.
const LINK_DIST = 0.36;

// Points in an irregular sphere: dense core, sparse and jagged edge.
function makeNodes() {
  const nodes = [];
  for (let i = 0; i < N; i++) {
    const u = Math.random() * 2 - 1;
    const a = Math.random() * Math.PI * 2;
    const s = Math.sqrt(1 - u * u);
    const r = Math.random() < 0.08 ? 1 + Math.random() * 0.18 : 0.45 + Math.pow(Math.random(), 0.45) * 0.55;
    nodes.push({ x: s * Math.cos(a) * r, y: u * r, z: s * Math.sin(a) * r, hub: Math.random() < 0.06, seed: Math.random() * 1000 });
  }
  return nodes;
}

// Links each point to its nearest neighbours (distances are kept as the orb rotates).
function makeLinks(nodes) {
  const links = new Map();
  nodes.forEach((p, i) => {
    const near = nodes
      .map((q, j) => ({ j, d: Math.hypot(p.x - q.x, p.y - q.y, p.z - q.z) }))
      .filter((x) => x.j !== i && x.d < LINK_DIST)
      .sort((a, b) => a.d - b.d)
      .slice(0, MAX_LINKS);
    for (const { j, d } of near) links.set(i < j ? `${i}-${j}` : `${j}-${i}`, [Math.min(i, j), Math.max(i, j), d]);
  });
  return [...links.values()];
}

// Pre-rendered glow sprite (faster than shadowBlur).
function makeSprite() {
  const c = document.createElement("canvas");
  c.width = c.height = 32;
  const g = c.getContext("2d");
  const grad = g.createRadialGradient(16, 16, 0, 16, 16, 16);
  grad.addColorStop(0, "rgba(255,255,255,1)");
  grad.addColorStop(0.25, "rgba(255,255,255,0.55)");
  grad.addColorStop(1, "rgba(255,255,255,0)");
  g.fillStyle = grad;
  g.fillRect(0, 0, 32, 32);
  return c;
}

// With paused (or prefers-reduced-motion) a single still frame is drawn.
export default function Orb({ state, rgb, intensity = 1, paused, size = 120, label }) {
  const ref = useRef(null);
  const live = useRef({ state, rgb, intensity, paused });
  live.current = { state, rgb, intensity, paused };
  const redraw = useRef(() => {});

  useEffect(() => {
    const canvas = ref.current;
    const ctx = canvas?.getContext?.("2d");
    // No canvas support (e.g. test environments): keep the accessible element only.
    if (!ctx) return undefined;
    const dpr = Math.min(2, window.devicePixelRatio || 1);
    canvas.width = size * dpr;
    canvas.height = size * dpr;
    ctx.scale(dpr, dpr);

    const nodes = makeNodes();
    const links = makeLinks(nodes);
    const sprite = makeSprite();
    // The tinted sprite is rebuilt when the colour changes noticeably.
    const tinted = document.createElement("canvas");
    tinted.width = tinted.height = 32;
    let tintKey = "";
    const tint = (r, g, b) => {
      const key = `${r >> 3},${g >> 3},${b >> 3}`;
      if (key === tintKey) return;
      tintKey = key;
      const t = tinted.getContext("2d");
      t.clearRect(0, 0, 32, 32);
      t.globalCompositeOperation = "source-over";
      t.drawImage(sprite, 0, 0);
      t.globalCompositeOperation = "source-in";
      t.fillStyle = `rgb(${r},${g},${b})`;
      t.fillRect(0, 0, 32, 32);
    };

    const cur = { ...TARGET[live.current.state], col: [...(STATE_RGB[live.current.state] ?? live.current.rgb)] };
    let rotY = Math.random() * Math.PI * 2;
    let t = 0;
    let last = performance.now();
    let raf = 0;
    const reduced = window.matchMedia("(prefers-reduced-motion: reduce)");
    const proj = new Float32Array(N * 3); // x, y, depth 0-1

    const draw = () => {
      const { state, rgb, intensity } = live.current;
      const tgt = TARGET[state];
      const col = STATE_RGB[state] ?? rgb;
      const k = 0.05;
      const boost = state === "working" ? Math.min(1.8, 0.85 + 0.2 * intensity) : 1;
      cur.speed += (tgt.speed * boost - cur.speed) * k;
      cur.jitter += (tgt.jitter - cur.jitter) * k;
      cur.pulse += (tgt.pulse - cur.pulse) * k;
      cur.glow += (tgt.glow - cur.glow) * k;
      cur.alpha += (tgt.alpha - cur.alpha) * k;
      cur.flicker += (tgt.flicker - cur.flicker) * k;
      for (let i = 0; i < 3; i++) cur.col[i] += (col[i] - cur.col[i]) * k;
      const [r, g, b] = cur.col.map(Math.round);
      tint(r, g, b);

      const c = size / 2;
      const R = size * 0.37 * (1 + Math.sin(t * (state === "waiting" ? 3.5 : 1.6)) * cur.pulse);
      ctx.clearRect(0, 0, size, size);

      // Faint glow behind the orb.
      const halo = ctx.createRadialGradient(c, c, 0, c, c, size * 0.5);
      halo.addColorStop(0, `rgba(${r},${g},${b},${0.22 * cur.glow})`);
      halo.addColorStop(0.6, `rgba(${r},${g},${b},${0.06 * cur.glow})`);
      halo.addColorStop(1, `rgba(${r},${g},${b},0)`);
      ctx.fillStyle = halo;
      ctx.fillRect(0, 0, size, size);

      // Rotate (Y axis) and tilt slightly (X axis), with perspective.
      const cy = Math.cos(rotY), sy = Math.sin(rotY);
      const tilt = 0.35 + Math.sin(t * 0.2) * 0.08;
      const cx = Math.cos(tilt), sx = Math.sin(tilt);
      for (let i = 0; i < N; i++) {
        const p = nodes[i];
        const j = cur.jitter ? 1 + Math.sin(t * 4 + p.seed) * cur.jitter : 1;
        const x1 = (p.x * cy + p.z * sy) * j;
        const z1 = (-p.x * sy + p.z * cy) * j;
        const y1 = p.y * j;
        const y2 = y1 * cx - z1 * sx;
        const z2 = y1 * sx + z1 * cx;
        const persp = 1 / (1.9 - z2 * 0.55);
        proj[i * 3] = c + x1 * R * persp * 1.35;
        proj[i * 3 + 1] = c + y2 * R * persp * 1.35;
        proj[i * 3 + 2] = (z2 + 1.2) / 2.4;
      }

      ctx.globalCompositeOperation = "lighter";

      // Lines between neighbours: fainter at the back and the longer they are.
      ctx.lineWidth = 0.75 * (size / 136);
      for (let e = 0; e < links.length; e++) {
        const [a, bIdx, d] = links[e];
        const depth = (proj[a * 3 + 2] + proj[bIdx * 3 + 2]) / 2;
        const flick = cur.flicker ? 1 - cur.flicker * 0.5 + cur.flicker * 0.5 * Math.sin(t * 6 + e * 1.7) : 1;
        const alpha = (1 - (d / LINK_DIST) * 0.7) * (0.12 + 0.6 * depth * depth) * cur.alpha * flick;
        if (alpha < 0.01) continue;
        ctx.strokeStyle = `rgba(${r},${g},${b},${alpha})`;
        ctx.beginPath();
        ctx.moveTo(proj[a * 3], proj[a * 3 + 1]);
        ctx.lineTo(proj[bIdx * 3], proj[bIdx * 3 + 1]);
        ctx.stroke();
      }

      // Points: small dots, and glowing hubs.
      const scale = size / 136;
      for (let i = 0; i < N; i++) {
        const x = proj[i * 3], y = proj[i * 3 + 1], depth = proj[i * 3 + 2];
        const p = nodes[i];
        const a = (0.25 + 0.75 * depth) * cur.alpha;
        if (p.hub) {
          const twinkle = 0.75 + 0.25 * Math.sin(t * 2 + p.seed);
          const s = (5 + depth * 6) * scale * (0.7 + 0.3 * cur.glow);
          ctx.globalAlpha = a * twinkle * (0.5 + 0.5 * cur.glow);
          ctx.drawImage(tinted, x - s / 2, y - s / 2, s, s);
          ctx.globalAlpha = 1;
        }
        const dot = (0.4 + depth * 0.8) * scale * (p.hub ? 1.5 : 1);
        ctx.fillStyle = `rgba(${Math.min(255, r + 60)},${Math.min(255, g + 60)},${Math.min(255, b + 60)},${a})`;
        ctx.beginPath();
        ctx.arc(x, y, dot, 0, Math.PI * 2);
        ctx.fill();
      }
      ctx.globalCompositeOperation = "source-over";
    };

    const frame = (now) => {
      const dt = Math.min(0.05, (now - last) / 1000);
      last = now;
      if (live.current.paused || reduced.matches) {
        raf = 0;
        return;
      }
      t += dt;
      rotY += dt * cur.speed;
      draw();
      raf = requestAnimationFrame(frame);
    };

    const kick = () => {
      if (live.current.paused || reduced.matches) {
        // Still frame: jump straight to the target state.
        Object.assign(cur, TARGET[live.current.state]);
        cur.col = [...(STATE_RGB[live.current.state] ?? live.current.rgb)];
        draw();
      } else if (!raf) {
        last = performance.now();
        raf = requestAnimationFrame(frame);
      }
    };
    redraw.current = kick;
    kick();
    reduced.addEventListener("change", kick);
    return () => {
      cancelAnimationFrame(raf);
      raf = 0;
      redraw.current = () => {};
      reduced.removeEventListener("change", kick);
    };
  }, [size]);

  // Redraw / restart the animation when state or pause changes.
  useEffect(() => redraw.current(), [state, paused, intensity, rgb]);

  return <canvas ref={ref} style={{ width: size, height: size, display: "block" }} role="img" aria-label={label} />;
}
