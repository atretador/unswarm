import {
  LayoutDashboard,
  Box,
  Container,
  Gauge,
  Key,
  ListOrdered,
  ScrollText,
  Settings,
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
  { to: "/fleet", icon: Container, label: "Fleet" },
  { to: "/benchmarks", icon: Gauge, label: "Benchmarks" },
  { to: "/queue", icon: ListOrdered, label: "Queue" },
  { to: "/logs", icon: ScrollText, label: "Logs" },
  { to: "/api-keys", icon: Key, label: "API Keys" },
  { to: "/settings", icon: Settings, label: "Settings" },
];
