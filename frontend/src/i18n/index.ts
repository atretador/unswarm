import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
import LanguageDetector from 'i18next-browser-languagedetector';

// Import all namespace bundles
import common from './locales/en/common.json';
import nav from './locales/en/nav.json';
import auth from './locales/en/auth.json';
import dashboard from './locales/en/dashboard.json';
import models from './locales/en/models.json';
import swarm from './locales/en/swarm.json';
import providers from './locales/en/providers.json';
import benchmarks from './locales/en/benchmarks.json';
import metrics from './locales/en/metrics.json';
import queue from './locales/en/queue.json';
import logs from './locales/en/logs.json';
import apiKeys from './locales/en/api-keys.json';
import routerProfiles from './locales/en/router-profiles.json';
import settings from './locales/en/settings.json';
import profile from './locales/en/profile.json';
import backendErrors from './locales/en/backend-errors.json';

import commonPtBR from './locales/pt-BR/common.json';
import navPtBR from './locales/pt-BR/nav.json';
import authPtBR from './locales/pt-BR/auth.json';
import dashboardPtBR from './locales/pt-BR/dashboard.json';
import modelsPtBR from './locales/pt-BR/models.json';
import swarmPtBR from './locales/pt-BR/swarm.json';
import providersPtBR from './locales/pt-BR/providers.json';
import benchmarksPtBR from './locales/pt-BR/benchmarks.json';
import metricsPtBR from './locales/pt-BR/metrics.json';
import queuePtBR from './locales/pt-BR/queue.json';
import logsPtBR from './locales/pt-BR/logs.json';
import apiKeysPtBR from './locales/pt-BR/api-keys.json';
import routerProfilesPtBR from './locales/pt-BR/router-profiles.json';
import settingsPtBR from './locales/pt-BR/settings.json';
import profilePtBR from './locales/pt-BR/profile.json';
import backendErrorsPtBR from './locales/pt-BR/backend-errors.json';

i18n
  .use(LanguageDetector)
  .use(initReactI18next)
  .init({
    resources: {
      en: {
        common, nav, auth, dashboard, models, swarm, providers,
        benchmarks, metrics, queue, logs, 'api-keys': apiKeys,
        'router-profiles': routerProfiles, settings, profile,
        'backend-errors': backendErrors,
      },
      'pt-BR': {
        common: commonPtBR, nav: navPtBR, auth: authPtBR,
        dashboard: dashboardPtBR, models: modelsPtBR, swarm: swarmPtBR,
        providers: providersPtBR, benchmarks: benchmarksPtBR,
        metrics: metricsPtBR, queue: queuePtBR, logs: logsPtBR,
        'api-keys': apiKeysPtBR, 'router-profiles': routerProfilesPtBR,
        settings: settingsPtBR, profile: profilePtBR,
        'backend-errors': backendErrorsPtBR,
      },
    },
    fallbackLng: 'en',
    ns: ['common'],
    defaultNS: 'common',
    interpolation: {
      escapeValue: false, // React already escapes
    },
    detection: {
      order: ['localStorage', 'navigator'],
      lookupLocalStorage: 'locale',
      caches: ['localStorage'],
    },
  });

export default i18n;
