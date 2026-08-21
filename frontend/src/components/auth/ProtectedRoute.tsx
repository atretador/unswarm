import { Navigate, useLocation } from "react-router-dom";
import { useAuth } from "../../lib/auth-context";
import { Spinner } from "../ui/Spinner";

export function ProtectedRoute({ children }: { children: React.ReactNode }) {
  const { user, isPending } = useAuth();
  const location = useLocation();

  if (isPending) {
    return (
      <div className="flex items-center justify-center h-screen">
        <Spinner size="lg" className="text-[var(--color-primary)]" />
      </div>
    );
  }

  if (!user) {
    return <Navigate to="/login" state={{ from: location.pathname }} replace />;
  }

  return <>{children}</>;
}
