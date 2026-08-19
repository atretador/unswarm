import {
  LayoutDashboard,
  Box,
  Container,
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
  { to: "/queue", icon: ListOrdered, label: "Queue" },
  { to: "/logs", icon: ScrollText, label: "Logs" },
  { to: "/settings", icon: Settings, label: "Settings" },
];
