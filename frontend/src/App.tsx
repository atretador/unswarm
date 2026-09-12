import { Suspense } from "react";
import { Routes, Route, Navigate } from "react-router-dom";
import { AppShell } from "./components/layout/AppShell";
import { ProtectedRoute } from "./components/auth/ProtectedRoute";
import { lazyWithRetry } from "./lib/lazy-with-retry";

// Route-level code splitting: each feature page is its own chunk, loaded on
// first navigation. One Suspense boundary at the shell level covers every
// lazy page — do not add nested per-page boundaries.
//
// lazyWithRetry (not plain React.lazy): React caches rejected import promises,
// so one transient chunk failure (429, network blip, post-deploy asset swap)
// would otherwise freeze that route until a manual reload — URL updates, page
// never renders.
const LoginPage = lazyWithRetry(() => import("./features/login"));
const Dashboard = lazyWithRetry(() => import("./features/dashboard"));
const Models = lazyWithRetry(() => import("./features/models"));
const Swarm = lazyWithRetry(() => import("./features/swarm"));
const Providers = lazyWithRetry(() => import("./features/providers"));
const Benchmarks = lazyWithRetry(() => import("./features/benchmarks"));
const Metrics = lazyWithRetry(() => import("./features/metrics"));
const Queue = lazyWithRetry(() => import("./features/queue"));
const Logs = lazyWithRetry(() => import("./features/logs"));
const ApiKeys = lazyWithRetry(() => import("./features/api-keys"));
const RouterProfiles = lazyWithRetry(() => import("./features/router-profiles"));
const Settings = lazyWithRetry(() => import("./features/settings"));
const Profile = lazyWithRetry(() => import("./features/profile"));
const NotFound = lazyWithRetry(() => import("./features/not-found"));

function PageFallback() {
  return (
    <div className="p-6 max-w-6xl" aria-busy="true" aria-live="polite">
      <div className="h-6 w-32 rounded animate-pulse bg-[var(--color-bg-muted)]" />
    </div>
  );
}

export default function App() {
  return (
    <Suspense fallback={<PageFallback />}>
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route element={<ProtectedRoute />}>
          <Route element={<AppShell />}>
            <Route path="/" element={<Dashboard />} />
            <Route path="/models" element={<Models />} />
            <Route path="/swarm" element={<Swarm />} />
            <Route path="/fleet" element={<Navigate to="/swarm" replace />} />
            <Route path="/providers" element={<Providers />} />
            <Route path="/benchmarks" element={<Benchmarks />} />
            <Route path="/metrics" element={<Metrics />} />
            <Route path="/queue" element={<Queue />} />
            <Route path="/logs" element={<Logs />} />
            <Route path="/api-keys" element={<ApiKeys />} />
            <Route path="/router-profiles" element={<RouterProfiles />} />
            <Route path="/settings" element={<Settings />} />
            <Route path="/profile" element={<Profile />} />
            <Route path="*" element={<NotFound />} />
          </Route>
        </Route>
      </Routes>
    </Suspense>
  );
}
