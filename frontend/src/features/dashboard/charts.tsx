// Dashboard charts. Recharts is imported statically here and this whole
// module is lazy-loaded once from index.tsx. Do NOT lazy-load individual
// recharts components behind nested <Suspense> boundaries: recharts 3.x sets
// state inside a ref callback (RechartsWrapper portal refs), which loops
// infinitely under React 19 when the chart subtree suspends/reappears
// (recharts#7463).
import {
  AreaChart,
  Area,
  XAxis,
  YAxis,
  Tooltip,
  ResponsiveContainer,
} from "recharts";

const tooltipContentStyle = {
  background: "var(--color-bg-elevated)",
  border: "1px solid var(--color-border)",
  borderRadius: "var(--radius-lg)",
  fontSize: "12px",
};

export function RequestsPerMinuteChart({ values }: { values: number[] }) {
  const data = values.map((v, i) => ({ time: `${i}m`, value: v }));
  return (
    <ResponsiveContainer width="100%" height={200}>
      <AreaChart data={data}>
        <defs>
          <linearGradient id="rpmGrad" x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stopColor="var(--color-primary)" stopOpacity={0.3} />
            <stop offset="100%" stopColor="var(--color-primary)" stopOpacity={0} />
          </linearGradient>
        </defs>
        <XAxis dataKey="time" tick={{ fontSize: 10, fill: "var(--color-text-muted)" }} tickLine={false} axisLine={false} />
        <YAxis tick={{ fontSize: 10, fill: "var(--color-text-muted)" }} tickLine={false} axisLine={false} width={30} />
        <Tooltip contentStyle={tooltipContentStyle} />
        <Area
          type="monotone"
          dataKey="value"
          stroke="var(--color-primary)"
          fill="url(#rpmGrad)"
          strokeWidth={2}
        />
      </AreaChart>
    </ResponsiveContainer>
  );
}

export function TokensPerSecondChart({ values }: { values: number[] }) {
  const data = values.map((v, i) => ({ time: `${i}s`, value: v }));
  return (
    <ResponsiveContainer width="100%" height={200}>
      <AreaChart data={data}>
        <defs>
          <linearGradient id="tpsGrad" x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stopColor="var(--color-status-running)" stopOpacity={0.3} />
            <stop offset="100%" stopColor="var(--color-status-running)" stopOpacity={0} />
          </linearGradient>
        </defs>
        <XAxis dataKey="time" tick={{ fontSize: 10, fill: "var(--color-text-muted)" }} tickLine={false} axisLine={false} />
        <YAxis tick={{ fontSize: 10, fill: "var(--color-text-muted)" }} tickLine={false} axisLine={false} width={30} />
        <Tooltip contentStyle={tooltipContentStyle} />
        <Area
          type="monotone"
          dataKey="value"
          stroke="var(--color-status-running)"
          fill="url(#tpsGrad)"
          strokeWidth={2}
        />
      </AreaChart>
    </ResponsiveContainer>
  );
}
