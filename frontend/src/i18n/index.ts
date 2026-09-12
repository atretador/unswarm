import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
import LanguageDetector from 'i18next-browser-languagedetector';

// ── English (base) ──
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

// ── Portuguese (Brazil) ──
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

// ── Spanish ──
import commonEs from './locales/es/common.json';
import navEs from './locales/es/nav.json';
import authEs from './locales/es/auth.json';
import dashboardEs from './locales/es/dashboard.json';
import modelsEs from './locales/es/models.json';
import swarmEs from './locales/es/swarm.json';
import providersEs from './locales/es/providers.json';
import benchmarksEs from './locales/es/benchmarks.json';
import metricsEs from './locales/es/metrics.json';
import queueEs from './locales/es/queue.json';
import logsEs from './locales/es/logs.json';
import apiKeysEs from './locales/es/api-keys.json';
import routerProfilesEs from './locales/es/router-profiles.json';
import settingsEs from './locales/es/settings.json';
import profileEs from './locales/es/profile.json';
import backendErrorsEs from './locales/es/backend-errors.json';

// ── Chinese (Simplified) ──
import commonZhCN from './locales/zh-CN/common.json';
import navZhCN from './locales/zh-CN/nav.json';
import authZhCN from './locales/zh-CN/auth.json';
import dashboardZhCN from './locales/zh-CN/dashboard.json';
import modelsZhCN from './locales/zh-CN/models.json';
import swarmZhCN from './locales/zh-CN/swarm.json';
import providersZhCN from './locales/zh-CN/providers.json';
import benchmarksZhCN from './locales/zh-CN/benchmarks.json';
import metricsZhCN from './locales/zh-CN/metrics.json';
import queueZhCN from './locales/zh-CN/queue.json';
import logsZhCN from './locales/zh-CN/logs.json';
import apiKeysZhCN from './locales/zh-CN/api-keys.json';
import routerProfilesZhCN from './locales/zh-CN/router-profiles.json';
import settingsZhCN from './locales/zh-CN/settings.json';
import profileZhCN from './locales/zh-CN/profile.json';
import backendErrorsZhCN from './locales/zh-CN/backend-errors.json';

const allNs = {
  common, nav, auth, dashboard, models, swarm, providers,
  benchmarks, metrics, queue, logs, 'api-keys': apiKeys,
  'router-profiles': routerProfiles, settings, profile,
  'backend-errors': backendErrors,
};

i18n
  .use(LanguageDetector)
  .use(initReactI18next)
  .init({
    resources: {
      en: allNs,
      'pt-BR': {
        common: commonPtBR, nav: navPtBR, auth: authPtBR,
        dashboard: dashboardPtBR, models: modelsPtBR, swarm: swarmPtBR,
        providers: providersPtBR, benchmarks: benchmarksPtBR,
        metrics: metricsPtBR, queue: queuePtBR, logs: logsPtBR,
        'api-keys': apiKeysPtBR, 'router-profiles': routerProfilesPtBR,
        settings: settingsPtBR, profile: profilePtBR,
        'backend-errors': backendErrorsPtBR,
      },
      es: {
        common: commonEs, nav: navEs, auth: authEs,
        dashboard: dashboardEs, models: modelsEs, swarm: swarmEs,
        providers: providersEs, benchmarks: benchmarksEs,
        metrics: metricsEs, queue: queueEs, logs: logsEs,
        'api-keys': apiKeysEs, 'router-profiles': routerProfilesEs,
        settings: settingsEs, profile: profileEs,
        'backend-errors': backendErrorsEs,
      },
      'zh-CN': {
        common: commonZhCN, nav: navZhCN, auth: authZhCN,
        dashboard: dashboardZhCN, models: modelsZhCN, swarm: swarmZhCN,
        providers: providersZhCN, benchmarks: benchmarksZhCN,
        metrics: metricsZhCN, queue: queueZhCN, logs: logsZhCN,
        'api-keys': apiKeysZhCN, 'router-profiles': routerProfilesZhCN,
        settings: settingsZhCN, profile: profileZhCN,
        'backend-errors': backendErrorsZhCN,
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
