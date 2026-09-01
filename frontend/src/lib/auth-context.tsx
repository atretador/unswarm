import { createContext, useContext, useState, useEffect, useCallback } from "react";
import type { ReactNode } from "react";
import { httpClient } from "./api/httpClient";

export interface AuthUser {
  username: string;
  isTempPassword: boolean;
}

interface AuthContextType {
  user: AuthUser | null;
  isPending: boolean;
  login: (username: string, password: string) => Promise<void>;
  logout: () => Promise<void>;
  changePassword: (currentPassword: string, newPassword: string) => Promise<void>;
}

const AuthContext = createContext<AuthContextType | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<AuthUser | null>(null);
  const [isPending, setIsPending] = useState(true);

  useEffect(() => {
    httpClient
      .getMe()
      .then((u) => setUser(u))
      .catch(() => setUser(null))
      .finally(() => setIsPending(false));
  }, []);

  const login = useCallback(async (username: string, password: string) => {
    const u = await httpClient.login(username, password);
    setUser(u);
  }, []);

  const logout = useCallback(async () => {
    await httpClient.logout();
    setUser(null);
  }, []);

  const changePassword = useCallback(
    async (currentPassword: string, newPassword: string) => {
      await httpClient.changePassword(currentPassword, newPassword);
      // Refresh user state to clear isTempPassword
      try {
        const u = await httpClient.getMe();
        setUser(u);
      } catch {
        setUser(null);
      }
    },
    [],
  );

  return (
    <AuthContext.Provider
      value={{ user, isPending, login, logout, changePassword }}
    >
      {children}
    </AuthContext.Provider>
  );
}

// eslint-disable-next-line react-refresh/only-export-components
export function useAuth() {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("useAuth must be used within AuthProvider");
  return ctx;
}
