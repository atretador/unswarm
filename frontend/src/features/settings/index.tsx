import { Settings as SettingsIcon } from "lucide-react";
import { Card, EmptyState, Input, Switch } from "../../components/ui";

export default function Settings() {
  return (
    <div className="p-6 space-y-6 max-w-3xl">
      <div>
        <h2 className="text-lg font-semibold text-[var(--color-text-heading)]">
          Settings
        </h2>
        <p className="text-xs text-[var(--color-text-muted)] mt-0.5">
          System configuration, API keys, and preferences.
        </p>
      </div>

      {/* Settings form preview */}
      <Card padding="lg">
        <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider mb-4">
          General
        </p>
        <div className="space-y-4">
          <Input label="Default model" placeholder="llama-3.1-70b" disabled />
          <Input
            label="Request timeout (seconds)"
            placeholder="120"
            type="number"
            disabled
          />
          <Input
            label="Health check interval (seconds)"
            placeholder="10"
            type="number"
            disabled
          />
          <Switch
            checked={true}
            onCheckedChange={() => {}}
            label="Auto-shutdown idle containers"
          />
          <Switch
            checked={true}
            onCheckedChange={() => {}}
            label="Enable benchmarking"
          />
        </div>
      </Card>

      <EmptyState
        icon={<SettingsIcon className="size-12" strokeWidth={1.5} />}
        title="Full settings panel"
        description="API key management, advanced configuration, and import/export ship in Phase 2."
      />
    </div>
  );
}
