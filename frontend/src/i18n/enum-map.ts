import i18n from './index';

type EnumValue = string;

/**
 * Get a translated label for an enum value.
 * Usage: enumLabel('modelStatus', 'ready') → "pronto" (pt-BR) or "ready" (en)
 */
export function enumLabel(enumType: string, value: EnumValue): string {
  const key = `enums.${enumType}.${value}`;
  const translated = i18n.t(key);
  // If translation key is returned as-is, fall back to the raw value
  return translated === key ? value : translated;
}

/**
 * Get all translated options for a select/dropdown.
 * Usage: enumOptions('logLevel') → [{ value: 'info', label: 'info' }, ...]
 */
export function enumOptions(enumType: string): { value: string; label: string }[] {
  const keys = i18n.t(`enums.${enumType}`, { returnObjects: true }) as Record<string, string>;
  return Object.entries(keys).map(([value, label]) => ({ value, label }));
}
