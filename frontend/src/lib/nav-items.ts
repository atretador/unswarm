import {
  LayoutDashboard,
  Box,
  Container,
  Gauge,
  Cloud,
  Key,
  ListOrdered,
  ScrollText,
  Settings,
  BarChart3,
  Route,
} from "lucide-react";
import type { LucideIcon } from "lucide-react";

export interface NavItem {
  to: string;
  label: string;
  icon: LucideIcon;
}

export const NAV_ITEMS: NavItem[] = [
  { to: "/", icon: LayoutDashboard, label: "Dashboard" },
  { to: "/models", icon: Box, label: "Models" },
  { to: "/swarm", icon: Container, label: "Swarm" },
  { to: "/providers", icon: Cloud, label: "Providers" },
  { to: "/benchmarks", icon: Gauge, label: "Benchmarks" },
  { to: "/metrics", icon: BarChart3, label: "Metrics" },
  { to: "/queue", icon: ListOrdered, label: "Queue" },
  { to: "/logs", icon: ScrollText, label: "Logs" },
  { to: "/api-keys", icon: Key, label: "API Keys" },
  { to: "/router-profiles", icon: Route, label: "Router Profiles" },
  { to: "/settings", icon: Settings, label: "Settings" },
];
