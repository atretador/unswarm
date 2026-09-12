import { describe, it, expect } from "vitest";
import { readdirSync, readFileSync } from "fs";
import { join } from "path";

/**
 * Flattens a nested JSON object into dot-separated key paths.
 * Example: { a: { b: "x", c: "y" } } → ["a.b", "a.c"]
 */
function flattenKeys(obj: Record<string, unknown>, prefix = ""): string[] {
  const keys: string[] = [];
  for (const [key, value] of Object.entries(obj)) {
    const path = prefix ? `${prefix}.${key}` : key;
    if (value !== null && typeof value === "object" && !Array.isArray(value)) {
      keys.push(...flattenKeys(value as Record<string, unknown>, path));
    } else {
      keys.push(path);
    }
  }
  return keys;
}

const localesDir = join(__dirname, "../i18n/locales");
const namespaces = readdirSync(join(localesDir, "en")).filter((f) =>
  f.endsWith(".json"),
);

// Collect all locales by scanning subdirectories
const locales = readdirSync(localesDir).filter((entry) => {
  try {
    return readdirSync(join(localesDir, entry)).some((f) => f.endsWith(".json"));
  } catch {
    return false;
  }
});

describe("i18n translation key parity", () => {
  for (const namespace of namespaces) {
    describe(namespace, () => {
      // Load the first locale as reference
      const referenceLocale = locales[0];
      const referenceRaw = readFileSync(
        join(localesDir, referenceLocale, namespace),
        "utf-8",
      );
      const referenceKeys = flattenKeys(JSON.parse(referenceRaw));

      for (const locale of locales.slice(1)) {
        it(`has all keys from ${referenceLocale} in ${locale}`, () => {
          const localeRaw = readFileSync(
            join(localesDir, locale, namespace),
            "utf-8",
          );
          const localeKeys = flattenKeys(JSON.parse(localeRaw));

          const missingInLocale = referenceKeys.filter(
            (k) => !localeKeys.includes(k),
          );
          const extraInLocale = localeKeys.filter(
            (k) => !referenceKeys.includes(k),
          );

          const errors: string[] = [];
          if (missingInLocale.length > 0) {
            errors.push(
              `Missing in ${locale}: ${missingInLocale.join(", ")}`,
            );
          }
          if (extraInLocale.length > 0) {
            errors.push(
              `Extra in ${locale} (not in ${referenceLocale}): ${extraInLocale.join(", ")}`,
            );
          }

          expect(errors).toEqual([]);
        });
      }
    });
  }
});

describe("i18n locale file structure", () => {
  it("all locales have the same namespace files", () => {
    const referenceLocale = locales[0];
    const referenceFiles = namespaces;

    for (const locale of locales.slice(1)) {
      const localeFiles = readdirSync(join(localesDir, locale)).filter((f) =>
        f.endsWith(".json"),
      );
      expect(localeFiles.sort()).toEqual(referenceFiles.sort());
    }
  });

  it("all JSON files are valid JSON", () => {
    for (const locale of locales) {
      for (const namespace of namespaces) {
        const filePath = join(localesDir, locale, namespace);
        const raw = readFileSync(filePath, "utf-8");
        expect(() => JSON.parse(raw)).not.toThrow();
      }
    }
  });
});
