import { useState, type CSSProperties } from "react";
import { useNavigate, useLocation } from "react-router-dom";
import { AnimatePresence, motion, useReducedMotion } from "motion/react";
import {
  Activity,
  ArrowRight,
  Eye,
  EyeOff,
  Route,
  Waypoints,
} from "lucide-react";
import { useAuth } from "../../lib/auth-context";
import { Input } from "../../components/ui/Input";
import { Button } from "../../components/ui/Button";
import { Logo } from "../../components/ui/Logo";

/* ── Motion vocabulary (matches --ease-out token) ── */
const EASE_OUT: [number, number, number, number] = [0.16, 1, 0.3, 1];

const container = {
  hidden: {},
  visible: { transition: { staggerChildren: 0.07, delayChildren: 0.15 } },
};

const item = {
  hidden: { opacity: 0, y: 14 },
  visible: {
    opacity: 1,
    y: 0,
    transition: { duration: 0.55, ease: EASE_OUT },
  },
};

const itemStatic = {
  hidden: { opacity: 0 },
  visible: { opacity: 1, transition: { duration: 0.3 } },
};

/* ── Proxy ↔ runtime constellation ─────────────────────────────
   The control plane (center) dispatching to agent runtimes:
   each link extends out, carries traffic both ways, then
   retracts and reconnects on its own staggered cycle.           */

type Satellite = {
  x: number;
  y: number;
  /** Full connect → carry traffic → retract cycle, in seconds */
  period: number;
  /** Cycle offset so satellites don't act in lockstep */
  delay: number;
};

const CORE = { x: 400, y: 400 };

const SATELLITES: Satellite[] = [
  { x: 618, y: 212, period: 11, delay: 0 },
  { x: 206, y: 262, period: 13, delay: 1.8 },
  { x: 584, y: 584, period: 12, delay: 3.4 },
  { x: 236, y: 598, period: 14, delay: 5.1 },
  { x: 400, y: 132, period: 10, delay: 6.6 },
  { x: 148, y: 428, period: 12.5, delay: 8.2 },
  { x: 652, y: 404, period: 11.5, delay: 9.7 },
];

/* Cycle fractions: extend until 12%, hold linked until 72%,
   retract by 84%, stay detached briefly, then reconnect. */
const LINK_TIMES = [0, 0.12, 0.72, 0.84, 1];

/** Traffic pulse riding the link while it is connected. */
function StreamPulse({
  s,
  outbound,
}: {
  s: Satellite;
  outbound: boolean;
}) {
  // Outbound leaves the core mid-hold; inbound returns slightly later.
  const times = outbound ? [0, 0.14, 0.46, 0.7] : [0, 0.18, 0.52, 0.74];
  const cx = outbound
    ? [CORE.x, CORE.x, s.x, s.x]
    : [s.x, s.x, CORE.x, CORE.x];
  const cy = outbound
    ? [CORE.y, CORE.y, s.y, s.y]
    : [s.y, s.y, CORE.y, CORE.y];

  return (
    <motion.circle
      r="2.6"
      fill="var(--color-primary)"
      cx={cx[0]}
      cy={cy[0]}
      initial={{ cx: cx[0], cy: cy[0], opacity: 0 }}
      animate={{ cx, cy, opacity: [0, 0, 1, 0] }}
      transition={{
        duration: s.period,
        times,
        ease: "linear",
        repeat: Infinity,
        delay: s.delay,
      }}
    />
  );
}

function Link({ s, animate }: { s: Satellite; animate: boolean }) {
  if (!animate) {
    /* Reduced motion: a calm, permanently-connected mesh */
    return (
      <g>
        <line
          x1={CORE.x}
          y1={CORE.y}
          x2={s.x}
          y2={s.y}
          stroke="var(--color-primary)"
          strokeOpacity="0.18"
          strokeWidth="1.2"
        />
        <circle
          cx={s.x}
          cy={s.y}
          r="4.5"
          fill="var(--color-primary)"
          opacity="0.55"
        />
      </g>
    );
  }

  return (
    <g>
      {/* Link — grows out of the core, holds, retracts back in */}
      <motion.line
        x1={CORE.x}
        y1={CORE.y}
        x2={CORE.x}
        y2={CORE.y}
        stroke="var(--color-primary)"
        strokeOpacity="0.28"
        strokeWidth="1.2"
        strokeLinecap="round"
        initial={{ x2: CORE.x, y2: CORE.y, opacity: 0 }}
        animate={{
          x2: [CORE.x, s.x, s.x, CORE.x, CORE.x],
          y2: [CORE.y, s.y, s.y, CORE.y, CORE.y],
          opacity: [0, 1, 1, 1, 0],
        }}
        transition={{
          duration: s.period,
          times: LINK_TIMES,
          ease: "easeInOut",
          repeat: Infinity,
          delay: s.delay,
        }}
      />

      {/* Runtime node — bright while linked, dim while detached */}
      <motion.circle
        cx={s.x}
        cy={s.y}
        r="4.5"
        fill="var(--color-primary)"
        initial={{ opacity: 0.25 }}
        animate={{ opacity: [0.25, 0.85, 0.85, 0.3, 0.25] }}
        transition={{
          duration: s.period,
          times: LINK_TIMES,
          ease: "easeInOut",
          repeat: Infinity,
          delay: s.delay,
        }}
      />

      {/* Dispatch + return traffic while the link is up */}
      <StreamPulse s={s} outbound />
      <StreamPulse s={s} outbound={false} />
    </g>
  );
}

function SwarmField({ animate }: { animate: boolean }) {
  return (
    <svg
      className="absolute inset-0 h-full w-full"
      viewBox="0 0 800 800"
      preserveAspectRatio="xMidYMid slice"
      aria-hidden="true"
    >
      <defs>
        <radialGradient id="login-core-glow">
          <stop offset="0" stopColor="var(--color-primary)" stopOpacity="0.28" />
          <stop offset="1" stopColor="var(--color-primary)" stopOpacity="0" />
        </radialGradient>
      </defs>

      {/* Core glow */}
      <circle cx={CORE.x} cy={CORE.y} r="260" fill="url(#login-core-glow)" />

      {SATELLITES.map((s) => (
        <Link key={`${s.x}-${s.y}`} s={s} animate={animate} />
      ))}

      {/* Control plane core */}
      <circle
        cx={CORE.x}
        cy={CORE.y}
        r="11"
        fill="var(--color-bg-elevated)"
        stroke="var(--color-primary)"
        strokeOpacity="0.7"
        strokeWidth="1.6"
      />
      <circle cx={CORE.x} cy={CORE.y} r="4" fill="var(--color-primary)" />
    </svg>
  );
}

export default function LoginPage() {
  const { login } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const from = (location.state as { from?: string })?.from || "/";

  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [showPassword, setShowPassword] = useState(false);

  const reduceMotion = useReducedMotion() ?? false;

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    setLoading(true);
    try {
      await login(username, password);
      navigate(from, { replace: true });
    } catch {
      setError("Invalid username or password");
    } finally {
      setLoading(false);
    }
  }

  const gridStyle: CSSProperties = {
    backgroundImage:
      "linear-gradient(to right, var(--color-border-subtle) 1px, transparent 1px), linear-gradient(to bottom, var(--color-border-subtle) 1px, transparent 1px)",
    backgroundSize: "44px 44px",
    maskImage:
      "radial-gradient(ellipse at 32% 42%, black 25%, transparent 72%)",
    WebkitMaskImage:
      "radial-gradient(ellipse at 32% 42%, black 25%, transparent 72%)",
  };

  return (
    <div className="flex min-h-screen bg-[var(--color-bg-base)]">
      {/* ── Left: atmospheric brand panel (desktop only) ── */}
      <aside className="relative hidden w-[52%] shrink-0 overflow-hidden border-r border-[var(--color-border-subtle)] bg-[var(--color-bg-surface)] lg:block xl:w-[56%]">
        {/* Gradient wash + grid + swarm */}
        <div
          aria-hidden="true"
          className="absolute inset-0"
          style={{
            background:
              "radial-gradient(60rem 40rem at 18% 12%, color-mix(in oklab, var(--color-primary) 9%, transparent), transparent 65%), radial-gradient(50rem 36rem at 85% 95%, color-mix(in oklab, var(--color-primary) 7%, transparent), transparent 70%)",
          }}
        />
        <div aria-hidden="true" className="absolute inset-0" style={gridStyle} />
        <div className="absolute inset-0 opacity-80">
          <SwarmField animate={!reduceMotion} />
        </div>

        {/* Panel content */}
        <div className="relative z-10 flex h-full flex-col justify-between p-10 xl:p-14">
          <motion.div
            initial={{ opacity: 0, y: -8 }}
            animate={{ opacity: 1, y: 0 }}
            transition={{ duration: 0.5, ease: EASE_OUT }}
            className="flex items-center gap-3"
          >
            <Logo size={34} />
            <span className="font-heading text-lg font-semibold tracking-tight text-[var(--color-text-heading)]">
              unswarm
            </span>
          </motion.div>

          <motion.div
            initial="hidden"
            animate="visible"
            variants={container}
            className="max-w-md"
          >
            <motion.h2
              variants={reduceMotion ? itemStatic : item}
              className="font-heading text-3xl font-semibold leading-tight tracking-tight text-[var(--color-text-heading)] xl:text-4xl"
            >
              One console for your{" "}
              <span className="text-[var(--color-primary)]">
                entire agent swarm.
              </span>
            </motion.h2>
            <motion.p
              variants={reduceMotion ? itemStatic : item}
              className="mt-4 text-base leading-relaxed text-[var(--color-text-muted)]"
            >
              Orchestrate models, monitor agents, and keep every run
              accountable — from a single pane of glass.
            </motion.p>

            <ul className="mt-8 space-y-3.5">
              {[
                { icon: Waypoints, label: "Multi-agent orchestration at a glance" },
                { icon: Activity, label: "Live telemetry, queue depth, and run logs" },
                { icon: Route, label: "Model routing with benchmark-backed picks" },
              ].map(({ icon: Icon, label }) => (
                <motion.li
                  key={label}
                  variants={reduceMotion ? itemStatic : item}
                  className="flex items-center gap-3 text-sm text-[var(--color-text)]"
                >
                  <span className="flex size-7 shrink-0 items-center justify-center rounded-[var(--radius-md)] border border-[var(--color-border)] bg-[var(--color-bg-elevated)] text-[var(--color-primary)]">
                    <Icon size={14} strokeWidth={2} />
                  </span>
                  {label}
                </motion.li>
              ))}
            </ul>
          </motion.div>

          <p className="text-2xs tracking-wide text-[var(--color-text-muted)]">
            unswarm · swarm control plane
          </p>
        </div>
      </aside>

      {/* ── Right: sign-in form ── */}
      <main className="relative flex min-w-0 flex-1 items-center justify-center px-6 py-12 sm:px-10">
        {/* Soft ambient glow behind the card (all breakpoints) */}
        <div
          aria-hidden="true"
          className="pointer-events-none absolute inset-0"
          style={{
            background:
              "radial-gradient(36rem 26rem at 50% 38%, color-mix(in oklab, var(--color-primary) 6%, transparent), transparent 70%)",
          }}
        />

        <motion.div
          initial="hidden"
          animate="visible"
          variants={container}
          className="relative z-10 w-full max-w-sm"
        >
          {/* Compact brand row — primary brand mark on small screens */}
          <motion.div
            variants={reduceMotion ? itemStatic : item}
            className="mb-8 flex items-center justify-center gap-3 lg:hidden"
          >
            <Logo size={36} />
            <span className="font-heading text-xl font-semibold tracking-tight text-[var(--color-text-heading)]">
              unswarm
            </span>
          </motion.div>

          <motion.div
            variants={reduceMotion ? itemStatic : item}
            className="mb-8 hidden items-center gap-3 lg:flex"
          >
            <Logo size={40} />
          </motion.div>

          <motion.h1
            variants={reduceMotion ? itemStatic : item}
            className="font-heading text-2xl font-semibold tracking-tight text-[var(--color-text-heading)] lg:text-3xl"
          >
            Sign in to unswarm
          </motion.h1>
          <motion.p
            variants={reduceMotion ? itemStatic : item}
            className="mt-2 mb-8 text-sm text-[var(--color-text-muted)]"
          >
            Enter your credentials to access the control plane.
          </motion.p>

          <form onSubmit={handleSubmit} className="space-y-4">
            <motion.div variants={reduceMotion ? itemStatic : item}>
              <Input
                label="Username"
                value={username}
                onChange={(e) => setUsername(e.target.value)}
                autoFocus
                required
                autoComplete="username"
                className="h-10"
              />
            </motion.div>

            <motion.div variants={reduceMotion ? itemStatic : item}>
              <div className="relative">
                <Input
                  label="Password"
                  type={showPassword ? "text" : "password"}
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  required
                  autoComplete="current-password"
                  className="h-10 pr-10"
                />
                <button
                  type="button"
                  onClick={() => setShowPassword((v) => !v)}
                  aria-label={showPassword ? "Hide password" : "Show password"}
                  className="absolute right-2 top-[1.75rem] flex size-7 cursor-pointer items-center justify-center rounded-[var(--radius-md)] text-[var(--color-text-muted)] transition-colors duration-[var(--duration-fast)] hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text)] focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--color-focus-ring)]"
                >
                  {showPassword ? <EyeOff size={15} /> : <Eye size={15} />}
                </button>
              </div>
            </motion.div>

            {/* Error — animates in without shifting the submit button abruptly */}
            <AnimatePresence initial={false}>
              {error && (
                <motion.div
                  key="error"
                  initial={{ opacity: 0, height: 0 }}
                  animate={{ opacity: 1, height: "auto" }}
                  exit={{ opacity: 0, height: 0 }}
                  transition={{ duration: reduceMotion ? 0 : 0.25, ease: EASE_OUT }}
                  className="overflow-hidden"
                >
                  <div
                    role="alert"
                    className="rounded-[var(--radius-md)] border border-[var(--color-status-error)]/25 bg-[var(--color-status-error)]/10 px-3 py-2.5"
                  >
                    <p className="text-sm text-[var(--color-status-error)]">
                      {error}
                    </p>
                  </div>
                </motion.div>
              )}
            </AnimatePresence>

            <motion.div variants={reduceMotion ? itemStatic : item}>
              <Button
                type="submit"
                size="lg"
                disabled={loading}
                loading={loading}
                className="mt-2 w-full font-semibold tracking-wide"
              >
                Sign in
                {!loading && <ArrowRight size={15} aria-hidden="true" />}
              </Button>
            </motion.div>
          </form>

          <motion.p
            variants={reduceMotion ? itemStatic : item}
            className="mt-8 text-center text-xs text-[var(--color-text-muted)]"
          >
            Need access? Contact your swarm administrator.
          </motion.p>
        </motion.div>
      </main>
    </div>
  );
}
