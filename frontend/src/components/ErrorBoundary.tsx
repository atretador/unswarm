import { Component } from "react";
import type { ErrorInfo, ReactNode } from "react";
import { AlertTriangle } from "lucide-react";
import { useTranslation } from "react-i18next";
import { Button } from "./ui";

interface ErrorBoundaryProps {
  children: ReactNode;
}

interface ErrorBoundaryState {
  hasError: boolean;
}

function ErrorFallback({
  onRetry,
  onReload,
}: {
  onRetry: () => void;
  onReload: () => void;
}) {
  const { t } = useTranslation("common");

  return (
    <div
      role="alert"
      className="flex h-full flex-col items-center justify-center gap-3 p-6 text-center"
    >
      <AlertTriangle className="size-8 text-[var(--color-status-warning)]" />
      <h1 className="text-lg font-semibold text-[var(--color-text-heading)]">
        {t("errorBoundary.title")}
      </h1>
      <p className="max-w-md text-sm text-[var(--color-text-muted)]">
        {t("errorBoundary.description")}
      </p>
      <div className="flex flex-wrap items-center justify-center gap-2">
        <Button variant="secondary" onClick={onRetry}>
          {t("retry")}
        </Button>
        <Button onClick={onReload}>{t("errorBoundary.reload")}</Button>
      </div>
    </div>
  );
}

/**
 * Catches render errors in the routed page subtree so a single broken page
 * (failed render, exhausted lazy-chunk retries, recharts React 19 loop) can't
 * blank the whole app while the URL stays updated. The AppShell keys this
 * boundary by pathname, so navigating away automatically resets it.
 */
export class ErrorBoundary extends Component<ErrorBoundaryProps, ErrorBoundaryState> {
  state: ErrorBoundaryState = { hasError: false };

  static getDerivedStateFromError(): ErrorBoundaryState {
    return { hasError: true };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error("[ErrorBoundary] Unhandled UI error", error, info.componentStack);
  }

  private handleRetry = () => {
    this.setState({ hasError: false });
  };

  private handleReload = () => {
    window.location.reload();
  };

  render() {
    if (this.state.hasError) {
      return <ErrorFallback onRetry={this.handleRetry} onReload={this.handleReload} />;
    }
    return this.props.children;
  }
}
