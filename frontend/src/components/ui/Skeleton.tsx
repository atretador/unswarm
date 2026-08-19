export interface SkeletonProps {
  className?: string;
  count?: number;
}

export function Skeleton({ className = "", count = 1 }: SkeletonProps) {
  return (
    <>
      {Array.from({ length: count }, (_, i) => (
        <div
          key={i}
          className={`
            rounded-[var(--radius-md)]
            bg-gradient-to-r from-[var(--color-skeleton)] via-[var(--color-skeleton-shimmer)] to-[var(--color-skeleton)]
            bg-[length:200%_100%]
            ${className}
          `}
          style={{ animation: "shimmer 1.5s ease-in-out infinite" }}
          aria-hidden="true"
        />
      ))}
    </>
  );
}
