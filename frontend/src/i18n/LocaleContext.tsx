import { createContext, useContext, useCallback, useState, useEffect, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';

interface LocaleContextValue {
  locale: string;
  setLocale: (locale: string) => void;
}

const LocaleContext = createContext<LocaleContextValue | null>(null);

export function LocaleProvider({ children }: { children: ReactNode }) {
  const { i18n } = useTranslation();
  const [locale, setLocaleState] = useState(i18n.language || 'en');

  // Sync with i18next on language change
  useEffect(() => {
    const handleLanguageChanged = (lng: string) => {
      setLocaleState(lng);
      document.documentElement.lang = lng;
    };
    i18n.on('languageChanged', handleLanguageChanged);
    return () => { i18n.off('languageChanged', handleLanguageChanged); };
  }, [i18n]);

  const setLocale = useCallback(async (newLocale: string) => {
    i18n.changeLanguage(newLocale);
    localStorage.setItem('locale', newLocale);
    document.documentElement.lang = newLocale;

    // Persist to backend if user is logged in (cookie-based auth)
    try {
      await fetch('/api/users/me/locale', {
        method: 'PUT',
        credentials: 'include',
        headers: {
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({ locale: newLocale }),
      });
    } catch {
      // Silently fail — localStorage fallback is fine
    }
  }, [i18n]);

  const value: LocaleContextValue = {
    locale,
    setLocale,
  };

  return (
    <LocaleContext.Provider value={value}>
      {children}
    </LocaleContext.Provider>
  );
}

export function useLocale() {
  const ctx = useContext(LocaleContext);
  if (!ctx) throw new Error('useLocale must be used within LocaleProvider');
  return ctx;
}
