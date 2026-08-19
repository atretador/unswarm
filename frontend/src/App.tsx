import { Routes, Route } from "react-router-dom";
import { AppShell } from "./components/layout/AppShell";
import Dashboard from "./features/dashboard";
import Models from "./features/models";
import Fleet from "./features/fleet";
import Queue from "./features/queue";
import Logs from "./features/logs";
import Settings from "./features/settings";
import NotFound from "./features/not-found";

export default function App() {
  return (
    <AppShell>
      <Routes>
        <Route path="/" element={<Dashboard />} />
        <Route path="/models" element={<Models />} />
        <Route path="/fleet" element={<Fleet />} />
        <Route path="/queue" element={<Queue />} />
        <Route path="/logs" element={<Logs />} />
        <Route path="/settings" element={<Settings />} />
        <Route path="*" element={<NotFound />} />
      </Routes>
    </AppShell>
  );
}
