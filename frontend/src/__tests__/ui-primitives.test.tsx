import { describe, it, expect, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Button } from "../components/ui/Button";
import { Badge } from "../components/ui/Badge";
import { StatusDot } from "../components/ui/StatusDot";
import { Switch } from "../components/ui/Switch";
import { Tooltip } from "../components/ui/Tooltip";
import { Skeleton } from "../components/ui/Skeleton";
import { EmptyState } from "../components/ui/EmptyState";
import { Spinner } from "../components/ui/Spinner";

// ─── Button ───────────────────────────────────────────────────────

describe("Button", () => {
  it("renders with primary variant by default", () => {
    render(<Button>Click me</Button>);
    const btn = screen.getByRole("button", { name: "Click me" });
    expect(btn).toBeInTheDocument();
    expect(btn).toBeEnabled();
  });

  it("renders secondary variant with correct classes", () => {
    render(<Button variant="secondary">Secondary</Button>);
    const btn = screen.getByRole("button", { name: "Secondary" });
    expect(btn.className).toContain("bg-[var(--color-bg-muted)]");
  });

  it("renders danger variant", () => {
    render(<Button variant="danger">Danger</Button>);
    const btn = screen.getByRole("button", { name: "Danger" });
    expect(btn.className).toContain("bg-[var(--color-status-error)]");
  });

  it("is disabled when disabled prop is set", async () => {
    const onClick = vi.fn();
    const user = userEvent.setup();
    render(<Button disabled onClick={onClick}>Disabled</Button>);
    const btn = screen.getByRole("button", { name: "Disabled" });
    expect(btn).toBeDisabled();
    await user.click(btn);
    expect(onClick).not.toHaveBeenCalled();
  });

  it("is disabled when loading", async () => {
    const onClick = vi.fn();
    const user = userEvent.setup();
    render(<Button loading onClick={onClick}>Saving</Button>);
    const btn = screen.getByRole("button", { name: "Saving" });
    expect(btn).toBeDisabled();
    await user.click(btn);
    expect(onClick).not.toHaveBeenCalled();
  });

  it("onClick fires when enabled", async () => {
    const onClick = vi.fn();
    const user = userEvent.setup();
    render(<Button onClick={onClick}>Click</Button>);
    await user.click(screen.getByRole("button", { name: "Click" }));
    expect(onClick).toHaveBeenCalledTimes(1);
  });
});

// ─── Badge ────────────────────────────────────────────────────────

describe("Badge", () => {
  it("renders children", () => {
    render(<Badge>Active</Badge>);
    expect(screen.getByText("Active")).toBeInTheDocument();
  });

  it("applies success variant class", () => {
    render(<Badge variant="success">OK</Badge>);
    const badge = screen.getByText("OK");
    expect(badge.className).toContain("text-[var(--color-status-running)]");
  });

  it("applies error variant class", () => {
    render(<Badge variant="error">Fail</Badge>);
    const badge = screen.getByText("Fail");
    expect(badge.className).toContain("text-[var(--color-status-error)]");
  });

  it("applies outline variant with border", () => {
    render(<Badge variant="outline">Tag</Badge>);
    const badge = screen.getByText("Tag");
    expect(badge.className).toContain("border border-[var(--color-border)]");
  });
});

// ─── StatusDot ────────────────────────────────────────────────────

describe("StatusDot", () => {
  it("renders a dot with correct color for running status", () => {
    const { container } = render(<StatusDot status="running" />);
    const dot = container.querySelector(".inline-block");
    expect(dot).toHaveClass("bg-[var(--color-status-running)]");
  });

  it("renders error color for error status", () => {
    const { container } = render(<StatusDot status="error" />);
    const dot = container.querySelector(".inline-block");
    expect(dot).toHaveClass("bg-[var(--color-status-error)]");
  });

  it("shows pulse ring for starting status", () => {
    const { container } = render(<StatusDot status="starting" />);
    const pulseRing = container.querySelector(".absolute");
    expect(pulseRing).toBeInTheDocument();
    expect(pulseRing!.getAttribute("style")).toContain("pulse-ring");
  });

  it("does NOT pulse for running status", () => {
    const { container } = render(<StatusDot status="running" />);
    const pulseRing = container.querySelector(".absolute");
    expect(pulseRing).not.toBeInTheDocument();
  });

  it("has an accessible label matching the status", () => {
    const { container } = render(<StatusDot status="error" />);
    const wrapper = container.querySelector("[aria-label]");
    expect(wrapper).toHaveAttribute("aria-label", "error");
  });

  it("shows pulse for validating status", () => {
    const { container } = render(<StatusDot status="validating" />);
    const pulseRing = container.querySelector(".absolute");
    expect(pulseRing).toBeInTheDocument();
  });
});

// ─── Switch ───────────────────────────────────────────────────────

describe("Switch", () => {
  it("toggles on click and updates aria-checked", async () => {
    const user = userEvent.setup();
    let checked = false;
    const { rerender } = render(
      <Switch checked={checked} onCheckedChange={(v) => { checked = v; }} />,
    );

    const sw = screen.getByRole("switch");
    expect(sw).toHaveAttribute("aria-checked", "false");

    await user.click(sw);
    expect(checked).toBe(true);

    rerender(<Switch checked={checked} onCheckedChange={(v) => { checked = v; }} />);
    expect(sw).toHaveAttribute("aria-checked", "true");
  });

  it("displays label when provided", () => {
    render(<Switch checked={false} onCheckedChange={() => {}} label="Enable" />);
    expect(screen.getByText("Enable")).toBeInTheDocument();
  });
});

// ─── Tooltip ──────────────────────────────────────────────────────

describe("Tooltip", () => {
  it("renders content in a tooltip element", () => {
    render(
      <Tooltip content="Help text">
        <button>Hover me</button>
      </Tooltip>,
    );
    const tooltip = screen.getByRole("tooltip");
    expect(tooltip).toHaveTextContent("Help text");
  });

  it("tooltip is linked via aria-describedby", () => {
    render(
      <Tooltip content="Tip">
        <button>Target</button>
      </Tooltip>,
    );
    const tooltip = screen.getByRole("tooltip");
    const describedBy = document.querySelector(`[aria-describedby="${tooltip.id}"]`);
    expect(describedBy).toBeInTheDocument();
  });

  it("supports bottom placement class", () => {
    render(
      <Tooltip content="Bottom" side="bottom">
        <button>Trigger</button>
      </Tooltip>,
    );
    const tooltip = screen.getByRole("tooltip");
    expect(tooltip.className).toContain("top-full");
  });

  it("supports left placement class", () => {
    render(
      <Tooltip content="Left" side="left">
        <button>Trigger</button>
      </Tooltip>,
    );
    const tooltip = screen.getByRole("tooltip");
    expect(tooltip.className).toContain("right-full");
  });

  it("supports right placement class", () => {
    render(
      <Tooltip content="Right" side="right">
        <button>Trigger</button>
      </Tooltip>,
    );
    const tooltip = screen.getByRole("tooltip");
    expect(tooltip.className).toContain("left-full");
  });
});

// ─── Skeleton ─────────────────────────────────────────────────────

describe("Skeleton", () => {
  it("renders a single skeleton by default", () => {
    const { container } = render(<Skeleton />);
    const skeletons = container.querySelectorAll("[aria-hidden='true']");
    expect(skeletons).toHaveLength(1);
  });

  it("renders multiple skeletons when count is set", () => {
    const { container } = render(<Skeleton count={3} />);
    const skeletons = container.querySelectorAll("[aria-hidden='true']");
    expect(skeletons).toHaveLength(3);
  });

  it("applies custom className", () => {
    const { container } = render(<Skeleton className="h-4 w-20" />);
    const skeleton = container.querySelector("[aria-hidden='true']");
    expect(skeleton).toHaveClass("h-4", "w-20");
  });
});

// ─── EmptyState ───────────────────────────────────────────────────

describe("EmptyState", () => {
  it("renders title", () => {
    render(<EmptyState title="No data" />);
    expect(screen.getByText("No data")).toBeInTheDocument();
  });

  it("renders description when provided", () => {
    render(<EmptyState title="Empty" description="Nothing here yet" />);
    expect(screen.getByText("Nothing here yet")).toBeInTheDocument();
  });

  it("renders action when provided", () => {
    render(
      <EmptyState
        title="Empty"
        action={<button>Get started</button>}
      />,
    );
    expect(screen.getByRole("button", { name: "Get started" })).toBeInTheDocument();
  });

  it("does not render description when omitted", () => {
    const { container } = render(<EmptyState title="Title only" />);
    expect(container.querySelectorAll("p")).toHaveLength(0);
  });
});

// ─── Spinner ──────────────────────────────────────────────────────

describe("Spinner", () => {
  it("renders an SVG with Loading aria-label", () => {
    render(<Spinner />);
    const svg = screen.getByRole("img", { name: "Loading" });
    expect(svg).toBeInTheDocument();
    expect(svg.tagName).toBe("svg");
  });

  it("applies size class", () => {
    render(<Spinner size="lg" />);
    const svg = screen.getByRole("img", { name: "Loading" });
    expect(svg.getAttribute("class")).toContain("size-8");
  });
});
