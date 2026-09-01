import { Suspense, lazy } from "react";
import { Routes, Route, Navigate } from "react-router-dom";
import { AppShell } from "./components/layout/AppShell";
import { ProtectedRoute } from "./components/auth/ProtectedRoute";

// Route-level code splitting: each feature page is its own chunk, loaded on
// first navigation. One Suspense boundary at the shell level covers every
// lazy page — do not add nested per-page boundaries.
const LoginPage = lazy(() => import("./features/login"));
const Dashboard = lazy(() => import("./features/dashboard"));
const Models = lazy(() => import("./features/models"));
const Swarm = lazy(() => import("./features/swarm"));
const Providers = lazy(() => import("./features/providers"));
const Benchmarks = lazy(() => import("./features/benchmarks"));
const Metrics = lazy(() => import("./features/metrics"));
const Queue = lazy(() => import("./features/queue"));
const Logs = lazy(() => import("./features/logs"));
const ApiKeys = lazy(() => import("./features/api-keys"));
const RouterProfiles = lazy(() => import("./features/router-profiles"));
const Settings = lazy(() => import("./features/settings"));
const Profile = lazy(() => import("./features/profile"));
const NotFound = lazy(() => import("./features/not-found"));

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
        <Route
          path="*"
          element={
            <ProtectedRoute>
              <AppShell>
                <Routes>
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
                </Routes>
              </AppShell>
            </ProtectedRoute>
          }
        />
      </Routes>
    </Suspense>
  );
}
