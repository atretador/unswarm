import { useId } from "react";

export interface LogoProps {
  /** Rendered size in px (square). */
  size?: number;
  className?: string;
}

/**
 * Unswarm brand mark — an ordered constellation of swarm nodes
 * around a central control core. The bright top-right node is the
 * active model; the hex cell in the core echoes the ordered hive.
 */
export function Logo({ size = 24, className }: LogoProps) {
  const uid = useId().replace(/:/g, "");
  const bgId = `${uid}-bg`;
  const glowId = `${uid}-glow`;
  const brightId = `${uid}-bright`;
  const dimId = `${uid}-dim`;

  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 64 64"
      role="img"
      aria-label="Unswarm"
      className={className}
    >
      <defs>
        <linearGradient id={bgId} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor="#1b2030" />
          <stop offset="1" stopColor="#0b0d14" />
        </linearGradient>
        <radialGradient id={glowId} cx="0.5" cy="0.5" r="0.5">
          <stop offset="0" stopColor="#22d3ee" stopOpacity="0.35" />
          <stop offset="1" stopColor="#22d3ee" stopOpacity="0" />
        </radialGradient>
        <linearGradient id={brightId} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor="#a5f3fc" />
          <stop offset="1" stopColor="#06b6d4" />
        </linearGradient>
        <linearGradient id={dimId} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor="#0e7490" />
          <stop offset="1" stopColor="#164e63" />
        </linearGradient>
      </defs>

      <rect width="64" height="64" rx="14" fill={`url(#${bgId})`} />
      <circle cx="32" cy="32" r="26" fill={`url(#${glowId})`} />

      <g stroke="#67e8f9" strokeOpacity="0.45" strokeWidth="2.5" strokeLinecap="round">
        <line x1="32" y1="32" x2="32" y2="13" />
        <line x1="32" y1="32" x2="48.5" y2="22.5" />
        <line x1="32" y1="32" x2="48.5" y2="41.5" />
        <line x1="32" y1="32" x2="32" y2="51" />
        <line x1="32" y1="32" x2="15.5" y2="41.5" />
        <line x1="32" y1="32" x2="15.5" y2="22.5" />
      </g>

      <g fill={`url(#${dimId})`}>
        <circle cx="32" cy="13" r="4" />
        <circle cx="48.5" cy="41.5" r="4" />
        <circle cx="32" cy="51" r="4" />
        <circle cx="15.5" cy="41.5" r="4" />
        <circle cx="15.5" cy="22.5" r="4" />
      </g>

      <circle cx="48.5" cy="22.5" r="6.8" fill="none" stroke="#22d3ee" strokeOpacity="0.55" strokeWidth="1.6" />
      <circle cx="48.5" cy="22.5" r="4.6" fill={`url(#${brightId})`} />

      <circle cx="32" cy="32" r="7.4" fill={`url(#${brightId})`} />
      <path
        d="M32 28.6 L34.94 30.3 L34.94 33.7 L32 35.4 L29.06 33.7 L29.06 30.3 Z"
        fill="none"
        stroke="#083344"
        strokeOpacity="0.6"
        strokeWidth="1.3"
        strokeLinejoin="round"
      />
    </svg>
  );
}
