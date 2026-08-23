import { Routes, Route } from "react-router-dom";
import { AppShell } from "./components/layout/AppShell";
import { ProtectedRoute } from "./components/auth/ProtectedRoute";
import LoginPage from "./features/login";
import Dashboard from "./features/dashboard";
import Models from "./features/models";
import Fleet from "./features/fleet";
import Providers from "./features/providers";
import Benchmarks from "./features/benchmarks";
import Queue from "./features/queue";
import Logs from "./features/logs";
import ApiKeys from "./features/api-keys";
import Settings from "./features/settings";
import Profile from "./features/profile";
import NotFound from "./features/not-found";

export default function App() {
  return (
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
                <Route path="/fleet" element={<Fleet />} />
                <Route path="/providers" element={<Providers />} />
                <Route path="/benchmarks" element={<Benchmarks />} />
                <Route path="/queue" element={<Queue />} />
                <Route path="/logs" element={<Logs />} />
                <Route path="/api-keys" element={<ApiKeys />} />
                <Route path="/settings" element={<Settings />} />
                <Route path="/profile" element={<Profile />} />
                <Route path="*" element={<NotFound />} />
              </Routes>
            </AppShell>
          </ProtectedRoute>
        }
      />
    </Routes>
  );
}
