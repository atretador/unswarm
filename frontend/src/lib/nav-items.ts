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
  /** Translation key in the 'nav' namespace (e.g. 'dashboard', 'models'). */
  tKey: string;
  icon: LucideIcon;
}

/**
 * Navigation items. Labels are translated via useTranslation('nav') in
 * consuming components (Sidebar, MobileDrawer). The `tKey` field holds
 * the key into `nav.json`.
 */
export const NAV_ITEMS: NavItem[] = [
  { to: "/", icon: LayoutDashboard, tKey: "dashboard" },
  { to: "/models", icon: Box, tKey: "models" },
  { to: "/swarm", icon: Container, tKey: "swarm" },
  { to: "/providers", icon: Cloud, tKey: "providers" },
  { to: "/benchmarks", icon: Gauge, tKey: "benchmarks" },
  { to: "/metrics", icon: BarChart3, tKey: "metrics" },
  { to: "/queue", icon: ListOrdered, tKey: "queue" },
  { to: "/logs", icon: ScrollText, tKey: "logs" },
  { to: "/api-keys", icon: Key, tKey: "apiKeys" },
  { to: "/router-profiles", icon: Route, tKey: "routerProfiles" },
  { to: "/settings", icon: Settings, tKey: "settings" },
];
