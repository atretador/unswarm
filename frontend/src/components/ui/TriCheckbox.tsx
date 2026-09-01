/** Checkbox with tri-state support (checked / indeterminate / unchecked). */
export function TriCheckbox({
  checked,
  indeterminate,
  onChange,
  disabled,
  label,
}: {
  checked: boolean;
  indeterminate?: boolean;
  onChange: (checked: boolean) => void;
  disabled?: boolean;
  label: string;
}) {
  return (
    <label className="inline-flex items-center gap-2 cursor-pointer select-none group/cb">
      <input
        type="checkbox"
        checked={checked}
        ref={(el) => {
          if (el) el.indeterminate = !!indeterminate && !checked;
        }}
        disabled={disabled}
        onChange={(e) => onChange(e.target.checked)}
        aria-label={label}
        className="size-3.5 rounded accent-[var(--color-primary)] cursor-pointer disabled:cursor-not-allowed disabled:opacity-50"
      />
    </label>
  );
}
